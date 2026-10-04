namespace SqlVitals.Engine.Verdicts;

/// <summary>How bad a page's verdict is (#146). Its colour follows it, and its words say it too.</summary>
public enum VerdictLevel
{
    /// <summary>Nothing is wrong: "No blocking".</summary>
    Ok,

    /// <summary>Worth knowing, nothing to act on yet: a short block, an overview with nothing to compare.</summary>
    Info,

    Warning,

    Critical,

    /// <summary>The page couldn't tell: it couldn't read, or there is nothing to judge.</summary>
    Unknown,
}

/// <summary>A button on a verdict: what it says, the key the page acts on, and what on (a session, a query).</summary>
public sealed record VerdictAction(string Key, string Text, long? Id = null);

/// <summary>
/// What a page says before its grid (#146): one line saying what is wrong or that nothing is, an
/// optional line of why, and up to <see cref="MaxActions"/> actions. Pure: each page's verdict is
/// worked out by a function in this folder, from what the page read.
/// </summary>
public sealed record PageVerdict
{
    public const int MaxActions = 4;

    public PageVerdict(VerdictLevel level, string headline, string? why = null, IReadOnlyList<VerdictAction>? actions = null)
    {
        if (string.IsNullOrWhiteSpace(headline))
            throw new ArgumentException("A verdict says something.", nameof(headline));
        actions ??= [];
        if (actions.Count > MaxActions)
            throw new ArgumentException($"A verdict has at most {MaxActions} actions.", nameof(actions));

        Level    = level;
        Headline = headline;
        Why      = string.IsNullOrWhiteSpace(why) ? null : why;
        Actions  = actions;
    }

    public VerdictLevel Level { get; }

    public string Headline { get; }

    public string? Why { get; }

    public IReadOnlyList<VerdictAction> Actions { get; }

    /// <summary>"OK", "Warning", …: the level in words, for a screen reader and the icon's tooltip.</summary>
    public string LevelText => Level switch
    {
        VerdictLevel.Ok       => "OK",
        VerdictLevel.Info     => "Note",
        VerdictLevel.Warning  => "Warning",
        VerdictLevel.Critical => "Critical",
        _                     => "Unknown",
    };

    /// <summary>A page that couldn't read what it judges: "Couldn't read the sessions", and why.</summary>
    public static PageVerdict Unreadable(string what, string? error) =>
        new(VerdictLevel.Unknown, $"Couldn't read {what}",
            string.IsNullOrWhiteSpace(error) ? "Refresh to try again." : $"{error.Trim()} Refresh to try again.");

    /// <summary>Records compare by value, actions included.</summary>
    public bool Equals(PageVerdict? other) =>
        other is not null && Level == other.Level && Headline == other.Headline && Why == other.Why
        && Actions.SequenceEqual(other.Actions);

    public override int GetHashCode() => HashCode.Combine(Level, Headline, Why, Actions.Count);
}
