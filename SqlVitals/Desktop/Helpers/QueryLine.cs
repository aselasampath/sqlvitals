using System.Globalization;
using System.Windows.Data;
using SqlVitals.Engine.Scripting;

namespace SqlVitals.Desktop.Helpers;

/// <summary>
/// Query text as a grid cell shows it (#232): one line, without its comments, so a stored
/// procedure's whole batch keeps its row one line high. "Tip" as the parameter gives the tooltip
/// instead: the text with its line breaks, cut short when huge. The column's
/// ClipboardContentBinding stays on the full text, so the filter, Copy All and Export see all of it.
/// </summary>
public sealed class QueryLine : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string;
        if (parameter is not "Tip")
            return SqlPreview.Line(text);
        // No tooltip at all for a row with no text, rather than an empty box.
        var tip = SqlPreview.Tooltip(text);
        return tip.Length > 0 ? tip : null!;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
