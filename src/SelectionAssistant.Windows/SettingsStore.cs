using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SelectionAssistant.Core;

namespace SelectionAssistant.Windows;

public sealed record UserSettings(string Endpoint = "https://api.deepseek.com/chat/completions", string Model = "deepseek-flash", string ProtectedKey = "", bool SelectionEnabled = true, bool AutoTranslate = false,
    string SpeechRegion = "", string ProtectedSpeechKey = "", string SpeechVoice = SpeechVoices.Default, int SpeechRate = 0);

internal sealed class SettingsStore(string directory)
{
    private readonly string path = Path.Combine(directory, "settings.json");
    internal UserSettings Load()
    {
        var settings = File.Exists(path) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path)) ?? new() : new();
        // v0.1 files have no speech fields; never let them come back as null.
        return settings with { SpeechRegion = settings.SpeechRegion ?? "", ProtectedSpeechKey = settings.ProtectedSpeechKey ?? "", SpeechVoice = SpeechVoices.Find(settings.SpeechVoice).Key };
    }
    internal void Save(UserSettings settings)
    {
        Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
    internal static ApiSettings Api(UserSettings settings) => new(settings.Endpoint, settings.Model, Unprotect(settings.ProtectedKey));
    internal static SpeechSettings Speech(UserSettings settings) => new(settings.SpeechRegion, Unprotect(settings.ProtectedSpeechKey), settings.SpeechVoice, settings.SpeechRate);

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    internal static string Protect(string text) => text.Length == 0 ? "" : Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(text), false));
    internal static string Unprotect(string text) => text.Length == 0 ? "" : Encoding.UTF8.GetString(Transform(Convert.FromBase64String(text), true));
    private static byte[] Transform(byte[] data, bool decrypt)
    {
        var input = new Blob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            bool ok = decrypt ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output) : CryptProtectData(ref input, "SelectionAssistant", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new InvalidOperationException("Windows 无法解密本机密钥，请重新填写 API Key。");
            try { var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); Array.Clear(data); }
    }
}
