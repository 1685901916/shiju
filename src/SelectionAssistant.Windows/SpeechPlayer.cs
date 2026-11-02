using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using SelectionAssistant.Core;

namespace SelectionAssistant.Windows;

// Azure when the user configured it; otherwise Edge's Natural voices through the installed Edge; then the keyless
// web voices (Youdao recordings for words and phrases, Google for sentences, Baidu when Google is unreachable);
// the Windows voice is only the offline fallback. Web clips are cached on disk, so replays never refetch.
public sealed class SpeechPlayer : IDisposable
{
    private readonly Func<SpeechSettings> settings;
    private readonly string cacheDirectory;
    private readonly EdgeVoice? edge;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private MediaPlayer? media;
    private CancellationTokenSource? cancellation;
    private dynamic? sapi;
    private int version;
    private DateTime googleUnavailableUntil;
    public event Action<bool>? SpeakingChanged;

    public SpeechPlayer(Func<SpeechSettings> settings, string cacheDirectory, EdgeVoice? edge = null)
    {
        this.settings = settings;
        this.cacheDirectory = cacheDirectory;
        this.edge = edge;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
    }

    // Must be called on the UI thread (MediaPlayer lives there). Returns a status line worth showing, else "".
    public async Task<string> SpeakAsync(string text, SpeechSettings? overrideSettings = null)
    {
        Stop();
        text = Regex.Replace(text, @"\.{3}|…", ", ").Trim();
        if (text.Length == 0) return "没有可朗读的英文。";
        var current = ++version;
        SpeechSettings speech;
        try { speech = overrideSettings ?? settings(); }
        catch (Exception) { speech = new(); }
        var voice = SpeechVoices.Find(speech.Voice);
        using var tokenSource = new CancellationTokenSource();
        cancellation = tokenSource;
        var token = tokenSource.Token;
        string problem = "";
        try
        {
            SpeakingChanged?.Invoke(true);
            if (speech.Configured && text.Length <= SpeechClient.MaxLength)
            {
                try
                {
                    var file = await Cached($"azure|{speech.Region}|{voice.Key}|{speech.Rate}|{text}", t => new SpeechClient(http).SynthesizeAsync(speech, text, t), token);
                    await Play(file, token);
                    return "";
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    problem = (ex is ArgumentException or InvalidDataException or HttpRequestException { StatusCode: not null } ? ex.Message : "连不上 Azure 语音服务。") + "已改用其他语音。";
                }
            }
            if (edge?.Available == true)
            {
                try
                {
                    await edge.SpeakAsync(text, voice, speech.Rate, token);
                    return problem;
                }
                catch (Exception) when (!token.IsCancellationRequested) { problem = "Edge 语音暂时不可用，已改用在线发音。"; }
            }
            var slow = SpeechVoices.IsSlow(speech.Rate);
            if (OnlineSpeech.IsWordOrPhrase(text))
            {
                try
                {
                    await Play(await Cached($"youdao|{voice.Key}|{text.ToLowerInvariant()}", t => Download(OnlineSpeech.Youdao(text, voice), t), token), token);
                    return problem;
                }
                catch (Exception) when (!token.IsCancellationRequested) { }
            }
            // Fetch the next piece while the current one plays, so long text flows without gaps.
            var pieces = OnlineSpeech.Chunks(text);
            var next = Sentence(pieces[0], voice, slow, token);
            for (int i = 0; i < pieces.Count; i++)
            {
                var file = await next;
                if (i + 1 < pieces.Count) next = Sentence(pieces[i + 1], voice, slow, token);
                await Play(file, token);
            }
            return problem;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return ""; }
        catch (Exception) { return current == version ? SpeakLocally(text, "在线发音暂时连不上，已改用 Windows 自带语音。") : ""; }
        finally { if (current == version) { cancellation = null; SpeakingChanged?.Invoke(false); } }
    }

    private async Task<string> Sentence(string text, SpeechVoice voice, bool slow, CancellationToken token)
    {
        if (DateTime.UtcNow >= googleUnavailableUntil)
        {
            try { return await Cached($"google|{voice.Key}|{slow}|{text}", t => Download(OnlineSpeech.Google(text, voice, slow), t), token); }
            // Google is often unreachable without a proxy; skip it for a while instead of waiting on every sentence.
            catch (Exception) when (!token.IsCancellationRequested) { googleUnavailableUntil = DateTime.UtcNow.AddMinutes(10); }
        }
        return await Cached($"baidu|{voice.Key}|{slow}|{text}", t => Download(OnlineSpeech.Baidu(text, voice, slow), t), token);
    }

    private async Task<byte[]> Download(Uri uri, CancellationToken token)
    {
        using var response = await http.GetAsync(uri, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) != true) throw new InvalidDataException("没有返回音频。");
        var audio = await response.Content.ReadAsByteArrayAsync(token);
        return audio.Length > 1000 ? audio : throw new InvalidDataException("返回的音频为空。");
    }

    private async Task<string> Cached(string key, Func<CancellationToken, Task<byte[]>> fetch, CancellationToken token)
    {
        var path = Path.Combine(cacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".mp3");
        if (File.Exists(path)) return path;
        var audio = await fetch(token);
        Directory.CreateDirectory(cacheDirectory);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, audio, token);
        File.Move(temp, path, true);
        Prune();
        return path;
    }

    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(cacheDirectory).GetFiles("*.mp3");
            if (files.Length <= 500) return;
            foreach (var old in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - 400)) old.Delete();
        }
        catch (Exception) { }
    }

    // Close before every Open so MediaOpened fires even when replaying the same cached file. Speed is never
    // changed here: on some machines any SpeedRatio other than 1 stalls MP3 playback at 0:00.
    private async Task Play(string path, CancellationToken token)
    {
        var player = media ??= new MediaPlayer();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Opened(object? sender, EventArgs e) => opened.TrySetResult();
        void Ended(object? sender, EventArgs e) => ended.TrySetResult();
        void Failed(object? sender, ExceptionEventArgs e) { opened.TrySetException(e.ErrorException); ended.TrySetException(e.ErrorException); }
        player.MediaOpened += Opened; player.MediaEnded += Ended; player.MediaFailed += Failed;
        try
        {
            player.Close();
            player.Open(new Uri(path));
            player.Play();
            // Watchdog: a stalled player must not leave the speaker button stuck on "stop".
            await Within(opened.Task, TimeSpan.FromSeconds(8), token);
            var length = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.FromSeconds(60);
            await Within(ended.Task, length + TimeSpan.FromSeconds(4), token);
        }
        finally
        {
            player.MediaOpened -= Opened; player.MediaEnded -= Ended; player.MediaFailed -= Failed;
            if (!ended.Task.IsCompletedSuccessfully) player.Stop();
        }
    }

    private static async Task Within(Task task, TimeSpan limit, CancellationToken token)
    {
        var finished = await Task.WhenAny(task, Task.Delay(limit, token));
        token.ThrowIfCancellationRequested();
        if (finished != task) throw new TimeoutException("音频播放没有开始。");
        await task;
    }

    // Start Edge ahead of the first click so speech starts without waiting for the browser to launch.
    public void WarmUp()
    {
        try { if (edge?.Available == true && !settings().Configured) _ = edge.WarmUpAsync(); } catch (Exception) { }
    }

    public void Stop()
    {
        ++version;
        cancellation?.Cancel();
        cancellation = null;
        try { media?.Stop(); } catch (Exception) { }
        try { sapi?.Speak("", 2); } catch (Exception) { }
        SpeakingChanged?.Invoke(false);
    }

    // Offline fallback only.
    private string SpeakLocally(string text, string status)
    {
        try
        {
            if (sapi is null)
            {
                sapi = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new InvalidOperationException());
                // Chinese Windows defaults to a Chinese voice, which mangles English; prefer an installed English one.
                foreach (var language in new[] { "Language=409", "Language=809" })
                {
                    var voices = sapi!.GetVoices(language);
                    if (voices.Count > 0) { sapi.Voice = voices.Item(0); break; }
                }
            }
            sapi!.Speak(text, 3);
            return status;
        }
        catch (Exception) { return "朗读失败：网络不可用，系统语音也无法使用。"; }
    }

    public void Dispose()
    {
        Stop();
        http.Dispose();
        media?.Close();
        edge?.Dispose();
        if (sapi is not null) { try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sapi); } catch (Exception) { } }
    }
}
