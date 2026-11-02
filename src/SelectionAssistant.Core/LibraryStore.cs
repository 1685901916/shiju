using System.Text.Encodings.Web;
using System.Text.Json;

namespace SelectionAssistant.Core;

public sealed class LibraryStore(string path)
{
    public List<LearningCard> Load()
    {
        if (!File.Exists(path)) return new();
        try
        {
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new JsonException();
            var cards = JsonSerializer.Deserialize<List<LearningCard>>(File.ReadAllText(path)) ?? throw new JsonException();
            if (cards.Any(c => c is null || c.Original is null || c.Translation is null || c.Messages is null || c.Source is null || c.Messages.Any(m => m is null || m.Content is null || m.Role is null))) throw new JsonException();
            return cards;
        }
        catch (JsonException ex) { throw new InvalidDataException("收藏文件无法读取，已保留原文件。请备份数据后修复 cards.json。", ex); }
    }

    public void Save(IReadOnlyList<LearningCard> cards)
    {
        // Never overwrite a damaged library with an empty collection after a failed load.
        if (File.Exists(path)) _ = Load();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, cards, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
