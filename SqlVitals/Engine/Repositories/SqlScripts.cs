using System.Collections.Frozen;
using System.Reflection;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// The repositories' SQL (#47): one <c>.sql</c> file per query under <c>Engine/Sql/&lt;Area&gt;/</c>,
/// embedded in the assembly. A query a repository changes as it runs has <c>/*NAME*/</c> markers
/// that it replaces (a column the server's version lacks, the period to read); everything else
/// is the file as it is.
/// </summary>
internal static class SqlScripts
{
    private const string Prefix = "SqlVitals.Engine.Sql.";

    // "Waits/ActiveWaits" → the file's text, read once.
    private static readonly Lazy<FrozenDictionary<string, string>> Scripts = new(Load);

    /// <summary>The query in <c>Engine/Sql/&lt;name&gt;.sql</c>, e.g. <c>Get("Waits/ActiveWaits")</c>.</summary>
    /// <exception cref="KeyNotFoundException">No such file is embedded.</exception>
    public static string Get(string name) =>
        Scripts.Value.TryGetValue(name, out var sql)
            ? sql
            : throw new KeyNotFoundException($"No embedded SQL named \"{name}\": add Engine/Sql/{name}.sql.");

    /// <summary>Every embedded query's name, for the tests.</summary>
    public static IEnumerable<string> Names => Scripts.Value.Keys;

    private static FrozenDictionary<string, string> Load()
    {
        var assembly = typeof(SqlScripts).Assembly;
        var scripts  = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Where(r => r.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            // The resource keeps the file's folder, with either separator: "SqlVitals.Engine.Sql.Waits\ActiveWaits.sql".
            var name = resource[Prefix.Length..^".sql".Length].Replace('\\', '/');
            scripts[name] = Read(assembly, resource);
        }
        return scripts.ToFrozenDictionary(StringComparer.Ordinal);
    }

    // The file's text without the newline that ends it, so it's the string the query used to be.
    private static string Read(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        return text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2]
             : text.EndsWith('\n') ? text[..^1]
             : text;
    }
}
