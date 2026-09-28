using System.Text;

namespace SqlVitals.Engine.Scripting;

/// <summary>
/// Puts text from the server into the comments of a generated script. Object, user, login and
/// database names can hold line breaks, and a line break inside a -- comment ends it: the rest of
/// the name would run as T-SQL when the DBA runs the script.
/// </summary>
public static class ScriptText
{
    /// <summary>"-- " and the text, kept to one line.</summary>
    public static string Comment(string text) => "-- " + OneLine(text);

    /// <summary>The text with every line break (CR, LF, NEL, line and paragraph separators) as a space.</summary>
    public static string OneLine(string text)
    {
        if (text.IndexOfAny(LineBreaks) < 0)
            return text;

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(Array.IndexOf(LineBreaks, c) >= 0 ? ' ' : c);
        return sb.ToString();
    }

    // CR, LF, next line (U+0085), line separator (U+2028), paragraph separator (U+2029).
    private static readonly char[] LineBreaks = ['\r', '\n', (char)0x85, (char)0x2028, (char)0x2029];
}
