using Dapper;
using Microsoft.Data.Sqlite;

namespace SqlVitals.Engine.History;

/// <summary>
/// A copy of a history file for other PCs to read over a share (#172). SQLite's WAL mode, which
/// the live file uses, doesn't work across a network: a reader on another PC can't share its
/// index. So the collector writes a consistent copy every few minutes with VACUUM INTO, in
/// rollback-journal mode, and swaps it in; the app on another PC reads that copy read-only.
/// </summary>
public static class HistoryShareCopy
{
    /// <summary>The copy's name, in the folder the collector is set to write it to.</summary>
    public const string FileName = "history-share.db";

    /// <summary>
    /// Writes a copy of <paramref name="livePath"/> to <paramref name="targetPath"/>. The copy is
    /// made beside the target and moved over it; a reader that has the old one open for a moment
    /// makes the move wait, and after a few tries this one is skipped (false), for the next.
    /// </summary>
    public static bool Write(string livePath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        var temp = targetPath + ".tmp";
        if (File.Exists(temp))
            File.Delete(temp);

        using (var live = Open(livePath, SqliteOpenMode.ReadWrite))
            live.Execute("VACUUM INTO @temp", new { temp });
        using (var copy = Open(temp, SqliteOpenMode.ReadWrite))
            copy.ExecuteScalar<string>("PRAGMA journal_mode = DELETE;");

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, targetPath, overwrite: true);
                return true;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5)
            {
                Thread.Sleep(200);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                File.Delete(temp);
                return false;
            }
        }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource     = path,
            Mode           = mode,
            // Closed for good on Dispose, so the copy can be moved straight after.
            Pooling        = false,
            DefaultTimeout = 30,
        }.ToString());
        conn.Open();
        conn.Execute("PRAGMA busy_timeout = 10000;");
        return conn;
    }
}
