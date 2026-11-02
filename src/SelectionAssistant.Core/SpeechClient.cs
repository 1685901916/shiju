using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace SelectionAssistant.Core;

public sealed record SpeechSettings(string Region = "", string Key = "", string Voice = SpeechVoices.Default, int Rate = 0)
{
    public bool Configured => !string.IsNullOrWhiteSpace(Region) && !string.IsNullOrWhiteSpace(Key);

    public Uri GetUri()
    {
        var region = Region.Trim().ToLowerInvariant();
        if (region.Length is 0 or > 40 || !region.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9'))
            throw new ArgumentException("Azure 区域只填英文代号，例如 eastasia、southeastasia、eastus。");
        return new Uri($"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1");
    }
}

public sealed record SpeechVoice(string Key, string Label, string EdgeName, string AzureName, string GoogleLanguage, int YoudaoType, string BaiduLanguage);

// Each voice maps onto Edge's Natural voice of the same name, Azure, and the fallback services' accent parameters.
public static class SpeechVoices
{
    public const string Default = "us";
    public static readonly SpeechVoice[] All =
    {
        new(Default, "Ava · 美音女声", "Microsoft Ava Online (Natural) - English (United States)", "en-US-AvaMultilingualNeural", "en-US", 2, "en"),
        new("us-andrew", "Andrew · 美音男声", "Microsoft Andrew Online (Natural) - English (United States)", "en-US-AndrewMultilingualNeural", "en-US", 2, "en"),
        new("us-emma", "Emma · 美音女声", "Microsoft Emma Online (Natural) - English (United States)", "en-US-EmmaMultilingualNeural", "en-US", 2, "en"),
        new("us-brian", "Brian · 美音男声", "Microsoft Brian Online (Natural) - English (United States)", "en-US-BrianMultilingualNeural", "en-US", 2, "en"),
        new("uk", "Sonia · 英音女声", "Microsoft Sonia Online (Natural) - English (United Kingdom)", "en-GB-SoniaNeural", "en-GB", 1, "uk"),
        new("uk-ryan", "Ryan · 英音男声", "Microsoft Ryan Online (Natural) - English (United Kingdom)", "en-GB-RyanNeural", "en-GB", 1, "uk"),
    };
    public static SpeechVoice Find(string? key) => All.FirstOrDefault(v => v.Key == key) ?? All[0];
    public static string Azure(string? key) => key is not null && key.EndsWith("Neural", StringComparison.Ordinal) ? key : Find(key).AzureName;
    // Edge and Azure take any rate; the keyless fallbacks only have normal and slow.
    public static double EdgeRate(int rate) => Math.Clamp(1 + rate / 100.0, 0.5, 1.5);
    public static bool IsSlow(int rate) => rate <= -20;
}

// The keyless web pronunciation endpoints that desktop dictionaries such as Easydict use. Unofficial: they
// can change, so callers fall back from one to the next.
public static class OnlineSpeech
{
    public const int ChunkLength = 180;

    // Youdao has real recordings for dictionary entries (words and set phrases) but no sentence audio.
    public static bool IsWordOrPhrase(string text) =>
        text.Length <= 40 && Regex.IsMatch(text, @"^[A-Za-z][A-Za-z'\- ]*$") && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4;

    public static Uri Youdao(string text, SpeechVoice voice) => new($"https://dict.youdao.com/dictvoice?audio={Uri.EscapeDataString(text)}&type={voice.YoudaoType}");
    public static Uri Google(string text, SpeechVoice voice, bool slow) => new($"https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob&tl={voice.GoogleLanguage}{(slow ? "&ttsspeed=0.24" : "")}&q={Uri.EscapeDataString(text)}");
    public static Uri Baidu(string text, SpeechVoice voice, bool slow) => new($"https://fanyi.baidu.com/gettts?lan={voice.BaiduLanguage}&text={Uri.EscapeDataString(text)}&spd={(slow ? 3 : 5)}&source=web");

    // Sentence-sized pieces under the per-request limit, split at sentence ends, then commas, then spaces.
    public static List<string> Chunks(string text, int max = ChunkLength)
    {
        var chunks = new List<string>();
        foreach (var sentence in Regex.Split(text.Trim(), @"(?<=[.!?;:])\s+|\n+"))
        {
            var rest = sentence.Trim();
            while (rest.Length > max)
            {
                var cut = rest.LastIndexOfAny(new[] { ',', ' ' }, max);
                if (cut <= 0) cut = max;
                chunks.Add(rest[..(cut + 1)].Trim());
                rest = rest[(cut + 1)..].Trim();
            }
            if (rest.Length > 0) chunks.Add(rest);
        }
        return chunks;
    }
}

// Azure AI Speech text to speech over REST; the free F0 tier covers 0.5M neural characters a month.
public sealed class SpeechClient(HttpClient client)
{
    public const int MaxLength = 3000;

    public static string Ssml(string text, string voice, int rate) =>
        $"<speak version='1.0' xml:lang='en-US'><voice name='{SecurityElement.Escape(voice)}'><prosody rate='{rate:+0;-0;0}%'>{SecurityElement.Escape(text)}</prosody></voice></speak>";

    public async Task<byte[]> SynthesizeAsync(SpeechSettings settings, string text, CancellationToken cancellationToken = default)
    {
        var uri = settings.GetUri();
        if (string.IsNullOrWhiteSpace(settings.Key)) throw new ArgumentException("请先在设置中填写 Azure 语音密钥。");
        if (text.Length > MaxLength) throw new ArgumentException($"一次最多朗读 {MaxLength:N0} 个字符。");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("Ocp-Apim-Subscription-Key", settings.Key.Trim());
        request.Headers.Add("X-Microsoft-OutputFormat", "audio-24khz-48kbitrate-mono-mp3");
        request.Headers.UserAgent.ParseAdd("Shiju/0.2");
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Ssml(text, SpeechVoices.Azure(settings.Voice), settings.Rate)));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/ssml+xml");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            var detail = code switch { 400 => "朗读请求被拒绝，所选声音可能在这个区域不可用", 401 or 403 => "Azure 语音密钥无效，或与区域不匹配", 429 => "Azure 免费额度或请求频率已用尽，稍后再试", _ => "Azure 语音服务暂时不可用" };
            throw new HttpRequestException($"{detail}（HTTP {code}）。", null, response.StatusCode);
        }
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (audio.Length <= 100) throw new InvalidDataException("Azure 返回了空音频，请重试。");
        return audio;
    }
}
