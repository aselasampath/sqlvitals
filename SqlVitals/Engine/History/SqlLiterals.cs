using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlVitals.Engine.History;

/// <summary>
/// A statement with its values taken out (#175): every string, number, money and binary literal
/// becomes <c>?</c>, so the history keeps the query's shape without the personal data or the
/// credentials a literal can hold. Identifiers, variables, keywords and comments stay as they are,
/// except a "double-quoted" one: with QUOTED_IDENTIFIER OFF that is a string, and the text alone
/// can't say which, so it goes too.
/// The text inside <c>EXEC('…')</c> and <c>sp_executesql N'…'</c> is dynamic SQL: it is kept the
/// same way, its own literals replaced. Uses the T-SQL lexer of ScriptDom; a text it can't read
/// to the end (cut off at <see cref="HistoryRecorder.MaxQueryTextLength"/> inside a string, say)
/// is cleaned by a simpler pass that replaces more rather than less.
/// </summary>
public static partial class SqlLiterals
{
    public const string Placeholder = "?";

    // Dynamic SQL inside dynamic SQL is kept a few levels deep; deeper, it's just a literal.
    private const int MaxDynamicDepth = 3;

    /// <summary>The statement with every literal replaced by <see cref="Placeholder"/>.</summary>
    public static string Remove(string? sql) =>
        string.IsNullOrEmpty(sql) ? string.Empty : Remove(sql, 0);

    private static string Remove(string sql, int depth)
    {
        IList<TSqlParserToken> tokens;
        IList<ParseError> errors;
        using (var reader = new StringReader(sql))
            tokens = new TSql170Parser(initialQuotedIdentifiers: true).GetTokenStream(reader, out errors);
        if (errors.Count > 0)
            return Fallback(sql);

        var text = new StringBuilder(sql.Length);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.TokenType == TSqlTokenType.EndOfFile)
                continue;
            if (IsString(token) && depth < MaxDynamicDepth && IsDynamicSql(tokens, i))
                text.Append(Quote(Remove(Unquote(token.Text), depth + 1), token.Text));
            else
                text.Append(IsLiteral(token) || IsDoubleQuoted(token) ? Placeholder : token.Text);
        }
        return text.ToString();
    }

    private static bool IsString(TSqlParserToken token) =>
        token.TokenType is TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral;

    // "…": a name with QUOTED_IDENTIFIER ON (the lexer's reading), a string with it OFF.
    private static bool IsDoubleQuoted(TSqlParserToken token) =>
        token.TokenType is TSqlTokenType.QuotedIdentifier or TSqlTokenType.AsciiStringOrQuotedIdentifier
        && token.Text.StartsWith('"');

    private static bool IsLiteral(TSqlParserToken token) =>
        IsString(token) || token.TokenType is TSqlTokenType.Integer or TSqlTokenType.Numeric or TSqlTokenType.Real
                                           or TSqlTokenType.Money or TSqlTokenType.HexLiteral;

    // The statement of EXEC('…') or EXECUTE('…'), and the first argument of sp_executesql, by
    // position or as @stmt = N'…'. Anything else, the parameter list included, is a value.
    private static bool IsDynamicSql(IList<TSqlParserToken> tokens, int index)
    {
        var before = Previous(tokens, index);
        if (before is null)
            return false;
        var token = tokens[before.Value];

        if (token.TokenType == TSqlTokenType.LeftParenthesis)
            return Previous(tokens, before.Value) is { } exec
                   && tokens[exec].TokenType is TSqlTokenType.Exec or TSqlTokenType.Execute;

        if (token.TokenType == TSqlTokenType.EqualsSign
            && Previous(tokens, before.Value) is { } name
            && tokens[name].TokenType == TSqlTokenType.Variable
            && string.Equals(tokens[name].Text, "@stmt", StringComparison.OrdinalIgnoreCase))
            return Previous(tokens, name) is { } procedure && IsExecuteSql(tokens[procedure]);

        return IsExecuteSql(token);
    }

    private static bool IsExecuteSql(TSqlParserToken token) =>
        token.TokenType is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier
        && string.Equals(token.Text.Trim('[', ']', '"'), "sp_executesql", StringComparison.OrdinalIgnoreCase);

    // The token before, skipping white space and comments.
    private static int? Previous(IList<TSqlParserToken> tokens, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (tokens[i].TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment
                                             or TSqlTokenType.MultilineComment))
                return i;
        }
        return null;
    }

    // N'it''s' → it's
    private static string Unquote(string literal)
    {
        var start = literal.StartsWith('N') || literal.StartsWith('n') ? 2 : 1;
        return literal[start..^1].Replace("''", "'", StringComparison.Ordinal);
    }

    // Back into a literal of the same kind: N'…' stays N'…'.
    private static string Quote(string text, string original) =>
        (original.StartsWith('N') || original.StartsWith('n') ? "N'" : "'") +
        text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// For a text the lexer can't read to the end: every quoted string (one left open runs to the
    /// end), binary value and number outside a name becomes <see cref="Placeholder"/>. Comments and
    /// [bracketed names] are read past first, so an apostrophe in one can't hide a string. Dynamic
    /// SQL goes as a whole, and a number in a comment or a name may go too: more, never less.
    /// </summary>
    internal static string Fallback(string sql) =>
        NumberPattern().Replace(
            HexPattern().Replace(
                QuotedPattern().Replace(sql, m => m.Value[0] is '-' or '/' or '[' ? m.Value : Placeholder),
                Placeholder),
            Placeholder);

    // Left to right, the first to start wins: a comment, a [name], 'a string' or "a string".
    [GeneratedRegex(@"--[^\r\n]*|/\*[\s\S]*?(?:\*/|$)|\[(?:[^\]]|\]\])*(?:\]|$)|[Nn]?'(?:[^']|'')*(?:'|$)|""(?:[^""]|"""")*(?:""|$)")]
    private static partial Regex QuotedPattern();

    [GeneratedRegex(@"(?<![\w@#$])0[xX][0-9A-Fa-f]*")]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"(?<![\w@#$])\$?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?")]
    private static partial Regex NumberPattern();
}
