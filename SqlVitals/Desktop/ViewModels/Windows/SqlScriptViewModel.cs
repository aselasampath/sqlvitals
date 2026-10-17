using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.ViewModels.Windows;

/// <summary>
/// A generated T-SQL script to review, copy or save (#191); SqlVitals never runs it.
/// <c>SqlScriptWindow</c> shows it, opened by <see cref="IDialogService.ShowSqlScript"/>.
/// </summary>
public sealed partial class SqlScriptViewModel(SqlScript script, IClipboard clipboard, IFileDialogs files, IDialogService dialogs) : ObservableObject
{
    /// <summary>The window's title and heading: "Update Statistics".</summary>
    public string Heading => script.Heading;

    /// <summary>What to check before running it.</summary>
    public string Notice => script.Notice;

    /// <summary>The script.</summary>
    public string Text => script.Text;

    /// <summary>Close was pressed.</summary>
    public event Action? CloseRequested;

    [RelayCommand]
    private void Copy() => clipboard.SetText(script.Text);

    /// <summary>Saves it as a .sql file where the user picks; a failure says why.</summary>
    [RelayCommand]
    private void Save()
    {
        var path = files.Save(new FileDialogOptions("Save SQL Script", "SQL Script (*.sql)|*.sql|All Files (*.*)|*.*") { FileName = script.FileName });
        if (path is null)
            return;

        try
        {
            File.WriteAllText(path, script.Text, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            dialogs.Error($"Failed to save the file:\n{ex.Message}", "Save Failed");
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
