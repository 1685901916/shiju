using System.Windows;
using System.Windows.Automation;

namespace SelectionAssistant.Windows;

internal sealed record SelectedText(string Text, Rect? LastBounds);

internal static class SelectionTextReader
{
    internal static SelectedText? Read(AutomationElement element)
    {
        if (element.Current.IsPassword || !element.TryGetCurrentPattern(TextPattern.Pattern, out var value)) return null;
        var ranges = ((TextPattern)value).GetSelection();
        if (ranges is null || ranges.Length == 0) return null;
        var text = string.Join("\n", ranges.Select(r => r.GetText(12001))).Trim();
        if (text.Length == 0 || text.Length > 12000) return null;
        var rects = ranges[^1].GetBoundingRectangles();
        return new(text, rects.Length > 0 && !rects[^1].IsEmpty ? rects[^1] : null);
    }
}
