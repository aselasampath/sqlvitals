using SqlVitals.Engine.Permissions;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>One area's row: what it needs, whether the login has it, and which pages go without it.</summary>
public sealed record PermissionRow(PermissionResult Result)
{
    public string Title => Result.Title;
    public string Needs => Result.Needs;
    public PermissionState State => Result.State;

    public string StateText => Result.State switch
    {
        PermissionState.Granted => "✓  Granted",
        PermissionState.Missing => "✗  Missing",
        _                       => "Not needed",
    };

    /// <summary>"Overview, Live charts, …; also monitoring and the health score". Empty when not needed.</summary>
    public string Affects => Result.State == PermissionState.NotNeeded
        ? string.Empty
        : string.Join("; ", new[] { string.Join(", ", Result.Pages), Result.Also }.Where(s => s.Length > 0));

    public string Note => Result.Note;

    public string AutomationName => $"{Title}: {StateText.TrimStart('✓', '✗', ' ')}. Needs {Needs}.";
}
