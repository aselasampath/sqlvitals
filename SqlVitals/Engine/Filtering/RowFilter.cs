using System.Globalization;
using System.Text;

namespace SqlVitals.Engine.Filtering;

/// <summary>
/// Text matching behind the grid filter boxes. A filter is split into terms on whitespace, with
/// "double quotes" keeping a phrase together, and a row matches when every term appears somewhere
/// in it, ignoring case. Cells are matched as displayed and as raw values, so "40,000" and "40000"
/// both find a row that shows 40,000.
/// </summary>
public static class RowFilter
{
    // Joins cells into one searchable string. A term never contains it, so no match can span two cells.
    private const char CellSeparator = '\u001F';

    public static IReadOnlyList<string> ParseTerms(string? filter)
    {
        var terms = new List<string>();
        if (string.IsNullOrWhiteSpace(filter)) return terms;

        var current = new StringBuilder();
        bool inQuotes = false;

        foreach (char c in filter)
        {
            if (c == '"')
            {
                Flush();
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                Flush();
            }
            else
            {
                current.Append(c);
            }
        }
        Flush();
        return terms;

        void Flush()
        {
            // Inside quotes the spaces belong to the phrase; only the phrase's own ends are trimmed.
            var term = current.ToString().Trim();
            if (term.Length > 0 && !terms.Contains(term, StringComparer.OrdinalIgnoreCase)) terms.Add(term);
            current.Clear();
        }
    }

    public static bool Matches(string rowText, IReadOnlyList<string> terms)
    {
        foreach (var term in terms)
            if (rowText.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        return true;
    }

    /// <summary>Joins the text of every cell in a row, as built by <see cref="CellText"/>.</summary>
    public static string RowText(IEnumerable<string> cells) => string.Join(CellSeparator, cells);

    /// <summary>
    /// The searchable text of one cell: the value formatted the way the grid shows it (a WPF
    /// binding StringFormat such as "N0" or "{0:yyyy-MM-dd}"), plus the raw value when it differs.
    /// </summary>
    public static string CellText(object? value, string? stringFormat, CultureInfo culture)
    {
        if (value is null) return "";

        string raw = value switch
        {
            string s       => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _              => value.ToString() ?? "",
        };

        string shown = Display(value, stringFormat, culture);
        return shown == raw ? raw : shown + CellSeparator + raw;
    }

    // Mirrors how a WPF binding applies StringFormat: a format with a placeholder is used as a
    // composite format, anything else as the format of the single value.
    private static string Display(object value, string? stringFormat, CultureInfo culture)
    {
        if (string.IsNullOrEmpty(stringFormat)) return Convert.ToString(value, culture) ?? "";

        try
        {
            return stringFormat.Contains('{')
                ? string.Format(culture, stringFormat, value)
                : string.Format(culture, "{0:" + stringFormat + "}", value);
        }
        catch (FormatException)
        {
            return Convert.ToString(value, culture) ?? "";
        }
    }
}
