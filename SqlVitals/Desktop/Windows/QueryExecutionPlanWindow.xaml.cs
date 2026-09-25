using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Microsoft.Win32;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Engine.ExecutionPlans;

namespace SqlVitals.Desktop.Windows;

public partial class QueryExecutionPlanWindow : Window
{
    private const string NoPlanMessage =
        "No execution plan available — the plan may have been evicted from cache.";

    private readonly string? _planXml;
    private bool _xmlShown;

    public QueryExecutionPlanWindow(string? planXml, string? queryText)
    {
        InitializeComponent();
        _planXml = planXml;

        QueryTextPreview.Text = queryText?.Trim();

        PlanView.Show(ShowplanParser.Parse(planXml), NoPlanMessage);
        BtnExportPng.IsEnabled = PlanView.HasDiagram;

        if (string.IsNullOrWhiteSpace(planXml))
        {
            NoPlanHint.Visibility = Visibility.Visible;
            BtnCopy.IsEnabled = false;
            BtnSave.IsEnabled = false;
            PlanXmlBox.Text = $"({NoPlanMessage})";
            _xmlShown = true;
        }
    }

    // Formatting a large plan takes a moment, so the XML is only laid out when asked for.
    private void PlanTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, PlanTabs) || _xmlShown || !XmlTab.IsSelected) return;
        PlanXmlBox.Text = FormatXml(_planXml!);
        _xmlShown = true;
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

    private void BtnExportPng_Click(object sender, RoutedEventArgs e)
    {
        if (!PlanView.HasDiagram) return;

        var dlg = new SaveFileDialog
        {
            Title = "Export Execution Plan Diagram",
            Filter = "PNG image (*.png)|*.png",
            FileName = "ExecutionPlan"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            // Rendering needs the diagram laid out, which it isn't while the XML tab shows.
            GraphicalTab.IsSelected = true;
            PlanView.UpdateLayout();
            PlanView.ExportPng(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to export the diagram:\n{ex.Message}",
                "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
                "Plan saved successfully.\nIt can also be opened in SSMS or Azure Data Studio.",
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
