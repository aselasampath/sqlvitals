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

    public static string ToCsv(IReadOnlyList<string>? headers, IEnumerable<IReadOnlyList<object?>> rows) =>
        Build(headers, rows, ',');

    public static string ToTsv(IReadOnlyList<string>? headers, IEnumerable<IReadOnlyList<object?>> rows) =>
        Build(headers, rows, '\t');

    private static string Build(IReadOnlyList<string>? headers, IEnumerable<IReadOnlyList<object?>> rows, char separator)
    {
        var sb = new StringBuilder();
        if (headers is not null) AppendLine(sb, headers, separator);
        foreach (var row in rows) AppendLine(sb, row, separator);
        return sb.ToString();
    }

    private static void AppendLine<T>(StringBuilder sb, IReadOnlyList<T> fields, char separator)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(separator);
            sb.Append(Quote(Format(fields[i]), separator));
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
