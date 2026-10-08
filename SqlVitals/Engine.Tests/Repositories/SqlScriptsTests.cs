using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Repositories;

/// <summary>
/// The repositories' SQL lives in <c>Engine/Sql/**/*.sql</c>, embedded (#47): every query the code
/// asks for is there, every file is asked for, and a script reads back as the query it was.
/// </summary>
public partial class SqlScriptsTests
{
    private static string EngineDir([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "Engine"));

    // Every SqlScripts.Get("Area/Name") in the repositories.
    private static HashSet<string> NamesAskedFor() =>
        Directory.GetFiles(Path.Combine(EngineDir(), "Repositories"), "*.cs")
            .SelectMany(file => GetCall().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"SqlScripts\.Get\(""([^""]+)""\)")]
    private static partial Regex GetCall();

    [Fact]
    public void Every_query_the_repositories_ask_for_is_embedded()
    {
        var missing = NamesAskedFor().Except(SqlScripts.Names).Order().ToList();

        Assert.True(missing.Count == 0, "Not embedded: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_embedded_query_is_asked_for()
    {
        var unused = SqlScripts.Names.Except(NamesAskedFor()).Order().ToList();

        Assert.True(unused.Count == 0, "No repository reads: " + string.Join(", ", unused));
    }

    [Fact]
    public void Every_sql_file_is_embedded()
    {
        var sqlDir = Path.Combine(EngineDir(), "Sql");
        var files  = Directory.GetFiles(sqlDir, "*.sql", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(sqlDir, f)[..^".sql".Length].Replace('\\', '/'))
            .Order();

        Assert.Equal(files, SqlScripts.Names.Order());
    }

    [Fact]
    public void A_script_is_its_file_without_the_final_newline()
    {
        var sql = SqlScripts.Get("Server/Edition");

        Assert.Equal("SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition", sql);
    }

    [Fact]
    public void An_unknown_name_says_which_file_to_add()
    {
        var error = Assert.Throws<KeyNotFoundException>(() => SqlScripts.Get("Waits/Nowhere"));

        Assert.Contains("Engine/Sql/Waits/Nowhere.sql", error.Message);
    }

    [Fact]
    public void A_template_keeps_its_markers_for_the_repository_to_fill()
    {
        Assert.Contains("/*DOP*/", SqlScripts.Get("Waits/ActiveWaits"));
        Assert.Contains("r.dop AS DegreeOfParallelism", WaitsRepository.ActiveWaitsSql(hasDop: true));
        Assert.DoesNotContain("/*", BaseRepository.HasColumnSql("sys.dm_exec_requests", "dop"));
    }
}
