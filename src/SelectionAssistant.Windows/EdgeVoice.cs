using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SelectionAssistant.Core;

namespace SelectionAssistant.Windows;

// Reads with Edge's own "Online (Natural)" voices by running the installed Edge headless and calling the
// standard Web Speech API in it, exactly as any web page in Edge may. No key and no impersonation: it is
// Edge itself talking to its speech service. Edge gets a private empty profile, is driven over a local
// DevTools socket, and is shut down after a while without speech.
public sealed class EdgeVoice : IDisposable
{
    private const string Bootstrap = """
        window.__shiju = window.__shiju || (() => {
          const voices = () => new Promise(resolve => {
            const list = speechSynthesis.getVoices();
            if (list.length) return resolve(list);
            const done = () => resolve(speechSynthesis.getVoices());
            speechSynthesis.addEventListener('voiceschanged', done, { once: true });
            setTimeout(done, 5000);
          });
          return {
            ready: async () => (await voices()).filter(v => v.name.includes('Online (Natural)')).length,
            speak: async (text, name, rate) => {
              const list = await voices();
              const voice = list.find(v => v.name === name) || list.find(v => v.lang.startsWith('en') && v.name.includes('Natural'));
              speechSynthesis.cancel();
              return new Promise(resolve => {
                const u = new SpeechSynthesisUtterance(text);
                if (voice) { u.voice = voice; u.lang = voice.lang; }
                u.rate = rate;
                let started = false;
                const watchdog = setTimeout(() => { if (!started) { speechSynthesis.cancel(); resolve('error:timeout'); } }, 8000);
                u.onstart = () => { started = true; };
                u.onend = () => { clearTimeout(watchdog); resolve('end'); };
                u.onerror = e => { clearTimeout(watchdog); resolve(e.error === 'interrupted' || e.error === 'canceled' ? 'stopped' : 'error:' + e.error); };
                speechSynthesis.speak(u);
              });
            },
            stop: () => { speechSynthesis.cancel(); return 'ok'; }
          };
        })();
        window.__shiju.ready()
        """;

    private readonly string profile;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly System.Threading.Timer idle;
    private Process? edge;
    private ClientWebSocket? socket;
    private string? browserAddress;
    private int nextId;

    public static string? Executable { get; } = FindEdge();
    public bool Available => Executable is not null;

    public EdgeVoice(string dataDirectory)
    {
        profile = Path.Combine(dataDirectory, "edge-voice");
        idle = new(_ => Shutdown(), null, Timeout.Infinite, Timeout.Infinite);
    }

    // Starting Edge takes a second or two, so callers warm it up as soon as speech becomes likely.
    public async Task WarmUpAsync()
    {
        try { await EnsureAsync(CancellationToken.None); KeepAlive(); }
        catch (Exception) { }
    }

    public async Task SpeakAsync(string text, SpeechVoice voice, int rate, CancellationToken token)
    {
        await EnsureAsync(token);
        idle.Change(Timeout.Infinite, Timeout.Infinite);
        try
        {
            using var stop = token.Register(() => _ = Evaluate("window.__shiju.stop()", CancellationToken.None));
            var result = await Evaluate($"window.__shiju.speak({JsonSerializer.Serialize(text)}, {JsonSerializer.Serialize(voice.EdgeName)}, {SpeechVoices.EdgeRate(rate).ToString(System.Globalization.CultureInfo.InvariantCulture)})", token);
            token.ThrowIfCancellationRequested();
            if (result.StartsWith("error", StringComparison.Ordinal)) throw new InvalidDataException("Edge 朗读失败（" + result + "）。");
        }
        finally { KeepAlive(); }
    }

    private void KeepAlive() => idle.Change(TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);

    private async Task EnsureAsync(CancellationToken token)
    {
        if (Executable is null) throw new InvalidOperationException("没有找到 Microsoft Edge。");
        await gate.WaitAsync(token);
        try
        {
            if (socket?.State == WebSocketState.Open) return;
            Directory.CreateDirectory(profile);
            var portFile = Path.Combine(profile, "DevToolsActivePort");
            // A previous run may have left Edge alive with this profile; reuse it rather than fight over the folder.
            if (!await TryConnect(portFile, token))
            {
                try { File.Delete(portFile); } catch (Exception) { }
                Native.EndChildrenWithApp();
                var start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var argument in new[] { "--headless=new", "--remote-debugging-port=0", $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--autoplay-policy=no-user-gesture-required", "about:blank" })
                    start.ArgumentList.Add(argument);
                edge = Process.Start(start);
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!await TryConnect(portFile, token))
                {
                    if (DateTime.UtcNow > deadline || edge?.HasExited == true) throw new InvalidOperationException("Edge 没有启动。");
                    await Task.Delay(150, token);
                }
            }
            var natural = await Evaluate(Bootstrap, token);
            if (natural == "0") throw new InvalidDataException("Edge 里没有可用的自然语音，可能是网络连不上微软语音服务。");
        }
        catch (Exception) { Close(); throw; }
        finally { gate.Release(); }
    }

    private async Task<bool> TryConnect(string portFile, CancellationToken token)
    {
        try
        {
            if (!File.Exists(portFile)) return false;
            var lines = await File.ReadAllLinesAsync(portFile, token);
            var port = int.Parse(lines[0]);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
            using var targets = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", token));
            var page = targets.RootElement.EnumerateArray().FirstOrDefault(t => t.GetProperty("type").GetString() == "page");
            var address = page.ValueKind == JsonValueKind.Object ? page.GetProperty("webSocketDebuggerUrl").GetString()
                : JsonDocument.Parse(await (await http.PutAsync($"http://127.0.0.1:{port}/json/new?about:blank", null, token)).Content.ReadAsStringAsync(token)).RootElement.GetProperty("webSocketDebuggerUrl").GetString();
            var client = new ClientWebSocket();
            client.Options.Proxy = null;
            await client.ConnectAsync(new Uri(address!), token);
            socket = client;
            browserAddress = lines.Length > 1 ? $"ws://127.0.0.1:{port}{lines[1]}" : null;
            _ = Receive(client);
            return true;
        }
        catch (Exception) when (!token.IsCancellationRequested) { return false; }
    }

    private async Task<string> Evaluate(string expression, CancellationToken token)
    {
        var client = socket ?? throw new InvalidOperationException("Edge 连接已断开。");
        var id = Interlocked.Increment(ref nextId);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = reply;
        // userGesture: speechSynthesis only speaks for pages with user activation.
        var message = JsonSerializer.SerializeToUtf8Bytes(new { id, method = "Runtime.evaluate", @params = new { expression, awaitPromise = true, returnByValue = true, userGesture = true } });
        await client.SendAsync(message, WebSocketMessageType.Text, true, token);
        using (token.Register(() => reply.TrySetCanceled(token)))
        {
            var result = await reply.Task;
            if (result.TryGetProperty("exceptionDetails", out _)) throw new InvalidDataException("Edge 执行朗读脚本出错。");
            return result.GetProperty("result").TryGetProperty("value", out var value) ? value.ToString() : "";
        }
    }

    private async Task Receive(ClientWebSocket client)
    {
        var buffer = new byte[1 << 16];
        var message = new MemoryStream();
        try
        {
            while (client.State == WebSocketState.Open)
            {
                var part = await client.ReceiveAsync(buffer, CancellationToken.None);
                if (part.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, part.Count);
                if (!part.EndOfMessage) continue;
                using var json = JsonDocument.Parse(message.ToArray());
                message.SetLength(0);
                if (json.RootElement.TryGetProperty("id", out var id) && pending.TryRemove(id.GetInt32(), out var reply))
                    reply.TrySetResult(json.RootElement.TryGetProperty("result", out var result) ? result.Clone() : default);
            }
        }
        catch (Exception) { }
        foreach (var id in pending.Keys) if (pending.TryRemove(id, out var reply)) reply.TrySetException(new InvalidOperationException("Edge 连接已断开。"));
        if (ReferenceEquals(socket, client)) socket = null;
    }

    private void Close()
    {
        try { socket?.Abort(); socket?.Dispose(); } catch (Exception) { }
        socket = null;
    }

    private void Shutdown()
    {
        if (!gate.Wait(0)) return;
        try { CloseBrowser(); }
        finally { gate.Release(); }
    }

    // Browser.close also ends an Edge left over from an earlier run, which this instance did not launch.
    private void CloseBrowser()
    {
        Close();
        try
        {
            if (browserAddress is not null)
            {
                using var browser = new ClientWebSocket();
                browser.Options.Proxy = null;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                browser.ConnectAsync(new Uri(browserAddress), timeout.Token).Wait(timeout.Token);
                browser.SendAsync(Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Browser.close\"}"), WebSocketMessageType.Text, true, timeout.Token).Wait(timeout.Token);
            }
        }
        catch (Exception) { }
        try { if (edge is { HasExited: false } && !edge.WaitForExit(1500)) edge.Kill(true); } catch (Exception) { }
        edge = null;
        browserAddress = null;
    }

    private static string? FindEdge()
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            if (root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe")?.GetValue(null) is string path && File.Exists(path)) return path;
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var path = Path.Combine(Environment.GetFolderPath(folder), "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public void Dispose()
    {
        idle.Dispose();
        CloseBrowser();
    }
}
