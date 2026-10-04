namespace SqlVitals.Desktop.Services;

/// <summary>
/// Which sections of a page are folded to their header (#146), remembered per page. Settings
/// keeps them (<see cref="ConnectionSettingsService"/>); tests keep them in memory.
/// </summary>
public interface ISectionFolds
{
    /// <summary>The keys of the page's sections that are folded.</summary>
    IReadOnlySet<string> Folded(string page);

    void SetFolded(string page, string section, bool folded);
}
