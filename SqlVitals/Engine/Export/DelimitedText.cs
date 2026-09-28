using System.Globalization;
using System.Text;

namespace SqlVitals.Engine.Export;

/// <summary>
/// Turns a header row plus data rows into delimited text: CSV (RFC 4180) for files, and
/// tab-separated text for the clipboard, which Excel and most chat/ticket tools paste as a table.
/// </summary>
public static class DelimitedText
{
    /// <summary>UTF-8 with a byte-order mark, so Excel detects the encoding when it opens the file.</summary>
    public static readonly Encoding CsvEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>A CSV file's text. Text cells a spreadsheet would run as a formula are kept as text (see <see cref="NeutralizeFormula"/>).</summary>
    public static string ToCsv(IReadOnlyList<string>? headers, IEnumerable<IReadOnlyList<object?>> rows) =>
        Build(headers, rows, ',', neutralizeFormulas: true);

    /// <summary>Clipboard text, exactly as shown: query text copied here is often pasted into SSMS to run.</summary>
    public static string ToTsv(IReadOnlyList<string>? headers, IEnumerable<IReadOnlyList<object?>> rows) =>
        Build(headers, rows, '\t', neutralizeFormulas: false);

    /// <summary>
    /// A text cell that a spreadsheet would read as a formula (it starts with = + - @, a tab or a
    /// carriage return), with an apostrophe in front so it opens as text. Server data such as
    /// application names and query text can start like that, and Excel runs formulas when it
    /// opens a CSV file.
    /// </summary>
    public static string NeutralizeFormula(string text) =>
        text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;

    private static string Build(IReadOnlyList<string>? headers, IEnumerable<IReadOnlyList<object?>> rows, char separator, bool neutralizeFormulas)
    {
        var sb = new StringBuilder();
        if (headers is not null) AppendLine(sb, headers, separator, neutralizeFormulas);
        foreach (var row in rows) AppendLine(sb, row, separator, neutralizeFormulas);
        return sb.ToString();
    }

    private static void AppendLine<T>(StringBuilder sb, IReadOnlyList<T> fields, char separator, bool neutralizeFormulas)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(separator);
            var text = Format(fields[i]);
            // Numbers and dates are left alone: a negative number is not a formula.
            if (neutralizeFormulas && fields[i] is string)
                text = NeutralizeFormula(text);
            sb.Append(Quote(text, separator));
        }
        sb.Append("\r\n");
    }

    /// <summary>
    /// Culture-invariant text for a cell value, so a decimal comma never collides with the CSV separator.
    /// </summary>
    public static string Format(object? value) => value switch
    {
        null                 => "",
        string s             => s,
        DateTime d           => d.TimeOfDay == TimeSpan.Zero
                                    ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                                    : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset d     => d.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        IFormattable f       => f.ToString(null, CultureInfo.InvariantCulture),
        _                    => value.ToString() ?? "",
    };

    /// <summary>
    /// Wraps a field in double quotes (doubling any inner quotes) when it contains the separator,
    /// a quote, a line break, or leading/trailing spaces that a reader would otherwise trim.
    /// </summary>
    public static string Quote(string field, char separator)
    {
        bool needsQuotes = field.Length > 0 &&
            (field.IndexOfAny([separator, '"', '\r', '\n']) >= 0 ||
             char.IsWhiteSpace(field[0]) || char.IsWhiteSpace(field[^1]));

        return needsQuotes ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }
}
