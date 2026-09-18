using System.IO;
using System.Windows;
using System.Xml.Linq;
using Microsoft.Win32;
using SqlVitals.Desktop.Helpers;

namespace SqlVitals.Desktop.Windows;

public partial class QueryExecutionPlanWindow : Window
{
    private readonly string? _planXml;

    public QueryExecutionPlanWindow(string? planXml, string? queryText)
    {
        InitializeComponent();
        _planXml = planXml;

        QueryTextPreview.Text = queryText?.Trim();

        if (string.IsNullOrWhiteSpace(planXml))
        {
            NoPlanHint.Visibility = Visibility.Visible;
            BtnCopy.IsEnabled = false;
            BtnSave.IsEnabled = false;
            PlanXmlBox.Text = "(No execution plan available — the plan may have been evicted from cache.)";
        }
        else
        {
            PlanXmlBox.Text = FormatXml(planXml);
        }
    }

    private static string FormatXml(string xml)
    {
        try
        {
            return XDocument.Parse(xml).ToString();
        }
        catch
        {
            return xml;
        }
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_planXml))
            ClipboardHelper.SetText(_planXml);
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_planXml)) return;

        var dlg = new SaveFileDialog
        {
            Title = "Save Execution Plan",
            Filter = "SQL Server Execution Plan (*.sqlplan)|*.sqlplan|XML File (*.xml)|*.xml",
            FileName = "ExecutionPlan"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dlg.FileName, _planXml, System.Text.Encoding.UTF8);
            MessageBox.Show(
                "Plan saved successfully.\nOpen it in SSMS or Azure Data Studio for the graphical view.",
                "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
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
