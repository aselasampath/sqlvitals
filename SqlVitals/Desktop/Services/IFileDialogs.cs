namespace SqlVitals.Desktop.Services;

/// <summary>Asks where to save a file, or which file to open (#182).</summary>
public interface IFileDialogs
{
    /// <summary>The path to save to; null when the user cancelled.</summary>
    string? Save(FileDialogOptions options);

    /// <summary>The file to open; null when the user cancelled.</summary>
    string? Open(FileDialogOptions options);
}

/// <summary>What a file dialog shows.</summary>
/// <param name="Title">"Save Execution Plan".</param>
/// <param name="Filter">As Windows has it: "SQL Script (*.sql)|*.sql|All Files (*.*)|*.*".</param>
public sealed record FileDialogOptions(string Title, string Filter)
{
    /// <summary>The name suggested.</summary>
    public string? FileName { get; init; }

    /// <summary>Added to a name typed without one: ".csv".</summary>
    public string? DefaultExt { get; init; }

    /// <summary>The folder it opens in; Windows' choice when null.</summary>
    public string? InitialDirectory { get; init; }
}
