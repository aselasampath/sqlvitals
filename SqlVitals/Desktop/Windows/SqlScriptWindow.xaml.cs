using System.IO;
using System.Windows;
using Microsoft.Win32;
using SqlVitals.Desktop.Helpers;

namespace SqlVitals.Desktop.Windows;

/// <summary>Shows a generated T-SQL script for review, with copy and save. Never executes it.</summary>
public partial class SqlScriptWindow : Window
{
    private readonly string _script;
    private readonly string _defaultFileName;

    public SqlScriptWindow(string heading, string notice, string script, string defaultFileName)
    {
        InitializeComponent();
        _script = script;
        _defaultFileName = defaultFileName;

        Title = heading;
        TxtHeading.Text = heading;
        TxtNotice.Text = notice;
        ScriptBox.Text = script;
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e) => ClipboardHelper.SetText(_script);

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save SQL Script",
            Filter = "SQL Script (*.sql)|*.sql|All Files (*.*)|*.*",
            FileName = _defaultFileName
        };

        if (dlg.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dlg.FileName, _script, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to save the file:\n{ex.Message}",
                "Save Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
