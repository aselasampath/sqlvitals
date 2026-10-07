using Microsoft.Win32;

namespace SqlVitals.Desktop.Services.Wpf;

/// <summary><see cref="IFileDialogs"/> with Windows' own dialogs, owned by the window in front.</summary>
internal sealed class WpfFileDialogs : IFileDialogs
{
    public string? Save(FileDialogOptions options) => Show(new SaveFileDialog(), options);

    public string? Open(FileDialogOptions options) => Show(new OpenFileDialog(), options);

    private static string? Show(FileDialog dialog, FileDialogOptions options)
    {
        dialog.Title  = options.Title;
        dialog.Filter = options.Filter;
        if (options.FileName is not null)         dialog.FileName         = options.FileName;
        if (options.DefaultExt is not null)       dialog.DefaultExt       = options.DefaultExt;
        if (options.InitialDirectory is not null) dialog.InitialDirectory = options.InitialDirectory;

        var shown = WpfDialogService.OwnerWindow() is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return shown == true ? dialog.FileName : null;
    }
}
