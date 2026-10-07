using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Tests.Fakes;

/// <summary>An <see cref="IFileDialogs"/> that picks the path it is given, or cancels (#182).</summary>
internal sealed class FakeFileDialogs : IFileDialogs
{
    /// <summary>What both dialogs return; null cancels.</summary>
    public string? Path { get; set; }

    /// <summary>The dialogs shown, in order.</summary>
    public List<FileDialogOptions> Asked { get; } = [];

    public string? Save(FileDialogOptions options)
    {
        Asked.Add(options);
        return Path;
    }

    public string? Open(FileDialogOptions options)
    {
        Asked.Add(options);
        return Path;
    }
}
