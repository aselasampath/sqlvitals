using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.History;

/// <summary>
/// How the statement text of the top queries is kept in the history (#175). The numbers (CPU,
/// reads, executions) are kept whatever this says, by the query's hash.
/// </summary>
public enum QueryTextStorage
{
    /// <summary>As SQL Server has it, literals and all: how every version before 0.77 kept it.</summary>
    Full,

    /// <summary>Every literal replaced with <c>?</c> (<see cref="SqlLiterals"/>): the default for a new installation.</summary>
    WithoutLiterals,

    /// <summary>No text: only the query's hash, which the numbers are kept by anyway.</summary>
    Off,
}

/// <summary>The choices of <see cref="QueryTextStorage"/>, in words and on the command line.</summary>
public static class QueryTextStorages
{
    /// <summary>For a new installation. One from before #175 keeps <see cref="QueryTextStorage.Full"/> until its user chooses.</summary>
    public const QueryTextStorage Default = QueryTextStorage.WithoutLiterals;

    /// <summary>Every choice, in the order Settings lists them.</summary>
    public static IReadOnlyList<QueryTextStorage> All { get; } =
        [QueryTextStorage.WithoutLiterals, QueryTextStorage.Full, QueryTextStorage.Off];

    /// <summary>"Without literals": for Settings, the activity log and the collector's list.</summary>
    public static string Describe(QueryTextStorage storage) => storage switch
    {
        QueryTextStorage.Full            => "Full",
        QueryTextStorage.WithoutLiterals => "Without literals",
        QueryTextStorage.Off             => "Off (hash only)",
        _                                => storage.ToString(),
    };

    /// <summary>"without-literals": the collector's command line.</summary>
    public static string Option(QueryTextStorage storage) => storage switch
    {
        QueryTextStorage.WithoutLiterals => "without-literals",
        _                                => storage.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Reads <see cref="Option"/>'s words, or the enum's names, in any case: nothing else, not a
    /// number. False leaves <paramref name="storage"/> at <see cref="Default"/>.
    /// </summary>
    public static bool TryParse(string? text, out QueryTextStorage storage)
    {
        var word = text?.Trim();
        foreach (var choice in All)
        {
            if (string.Equals(word, Option(choice), StringComparison.OrdinalIgnoreCase)
                || string.Equals(word, choice.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                storage = choice;
                return true;
            }
        }
        storage = Default;
        return false;
    }

    /// <summary>
    /// What is kept of texts just read: as they are, without literals, or none at all.
    /// </summary>
    public static IReadOnlyList<QueryTextInfo> Apply(IReadOnlyList<QueryTextInfo> texts, QueryTextStorage storage) => storage switch
    {
        QueryTextStorage.Full            => texts,
        QueryTextStorage.WithoutLiterals => texts.Select(t => t with { QueryText = SqlLiterals.Remove(t.QueryText) }).ToList(),
        _                                => [],
    };
}
