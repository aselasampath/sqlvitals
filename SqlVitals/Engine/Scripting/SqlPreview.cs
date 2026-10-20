using System.Text;

namespace SqlVitals.Engine.Scripting;

/// <summary>
/// How a grid shows query text (#232): one line, so a row stays one line high whatever the text.
/// A stored procedure's whole batch starts with a comment banner and its change history, so the
/// comments go too: the line starts at <c>CREATE PROCEDURE [wf].[GetEntities…]</c>, not at
/// <c>-- =====</c>. The full text, as the server holds it, is a click away.
/// </summary>
public static class SqlPreview
{
    /// <summary>Longest line a grid cell is given; the cell trims what it can't fit.</summary>
    public const int MaxLineLength = 1000;

    /// <summary>Longest text a tooltip is given, so a huge batch can't fill the screen.</summary>
    public const int MaxTooltipLength = 4000;

    /// <summary>
    /// The text on one line: comments (<c>--</c> to the end of the line, and <c>/* */</c>, which
    /// T-SQL nests) are left out, and line breaks, tabs and runs of spaces become one space. Text
    /// in quotes and brackets is kept, so <c>'--'</c> or <c>[a/*b]</c> is not taken for a comment.
    /// Text that is nothing but comments is shown with them, rather than as an empty cell.
    /// </summary>
    public static string Line(string? text, int maxLength = MaxLineLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var line = new LineBuilder(maxLength);
        var i = 0;
        while (i < text.Length && !line.Full)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '-' && next == '-')
            {
                i = text.IndexOfAny(LineEnds, i + 2) is var end and >= 0 ? end : text.Length;
                line.Space();
            }
            else if (c == '/' && next == '*')
            {
                i = AfterBlockComment(text, i);
                line.Space();
            }
            else if (c is '\'' or '[' or '"')
            {
                var end = AfterQuoted(text, i, c == '[' ? ']' : c);
                for (; i < end && !line.Full; i++)
                    line.Add(text[i]);
                i = end;
            }
            else
            {
                line.Add(c);
                i++;
            }
        }

        return line.IsEmpty ? Collapse(text, maxLength) : line.ToString();
    }

    /// <summary>The full text for a tooltip, line breaks kept, cut short past <see cref="MaxTooltipLength"/>.</summary>
    public static string Tooltip(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        return trimmed.Length <= MaxTooltipLength
            ? trimmed
            : trimmed[..MaxTooltipLength] + "\n… (View shows all of it)";
    }

    // The text with only its whitespace collapsed, comments and all.
    private static string Collapse(string text, int maxLength)
    {
        var line = new LineBuilder(maxLength);
        foreach (var c in text)
        {
            if (line.Full)
                break;
            line.Add(c);
        }
        return line.ToString();
    }

    // Past the */ that closes the /* at start, counting the ones nested in it; the end when it never closes.
    private static int AfterBlockComment(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length - 1; i++)
        {
            if (text[i] == '/' && text[i + 1] == '*')
            {
                depth++;
                i++;
            }
            else if (text[i] == '*' && text[i + 1] == '/')
            {
                i++;
                if (--depth == 0)
                    return i + 1;
            }
        }
        return text.Length;
    }

    // Past the quote or bracket that closes the one at start; a doubled one ('' or ]]) is part of the text.
    private static int AfterQuoted(string text, int start, char close)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] != close)
                continue;
            if (i + 1 < text.Length && text[i + 1] == close)
                i++;
            else
                return i + 1;
        }
        return text.Length;
    }

    // CR, LF, next line (U+0085), line separator (U+2028), paragraph separator (U+2029): where a -- comment ends.
    private static readonly char[] LineEnds = ['\r', '\n', (char)0x85, (char)0x2028, (char)0x2029];

    // The line so far: whitespace becomes one space between words, and it stops past maxLength.
    private sealed class LineBuilder(int maxLength)
    {
        private readonly StringBuilder _sb = new(Math.Min(maxLength + 1, 256));
        private bool _pendingSpace;

        public bool Full => _sb.Length > maxLength;

        public bool IsEmpty => _sb.Length == 0;

        public void Space() => _pendingSpace = _sb.Length > 0;

        public void Add(char c)
        {
            if (char.IsWhiteSpace(c))
            {
                Space();
                return;
            }
            if (_pendingSpace)
            {
                _sb.Append(' ');
                _pendingSpace = false;
            }
            _sb.Append(c);
        }

        public override string ToString() => Full ? _sb.ToString(0, maxLength) + "…" : _sb.ToString();
    }
}
