using System.IO;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.ExecutionPlans;

namespace SqlVitals.Desktop.ViewModels.Windows;

/// <summary>
/// An execution plan (#191): drawn on its Graphical tab, its XML on the other, with the query it
/// belongs to; copied, saved as .sqlplan, or its diagram exported as a PNG. <c>QueryExecutionPlanWindow</c>
/// shows it, opened by <see cref="IDialogService.ShowQueryPlan"/>.
/// </summary>
public sealed partial class QueryPlanViewModel : ObservableObject
{
    /// <summary>What the diagram says when there is no plan.</summary>
    public const string NoPlanMessage = "No execution plan available — the plan may have been evicted from cache.";

    /// <summary>The Graphical tab; the XML tab is 1.</summary>
    public const int GraphicalTab = 0;

    /// <summary>The XML tab.</summary>
    public const int XmlTab = 1;

    private readonly string? _planXml;
    private readonly IClipboard _clipboard;
    private readonly IFileDialogs _files;
    private readonly IDialogService _dialogs;
    private bool _xmlShown;

    /// <param name="planXml">The showplan XML; null or blank when there is none.</param>
    /// <param name="title">The window's title; null for its own.</param>
    public QueryPlanViewModel(string? planXml, string? queryText, string? title, IClipboard clipboard, IFileDialogs files, IDialogService dialogs)
    {
        _planXml   = planXml;
        _clipboard = clipboard;
        _files     = files;
        _dialogs   = dialogs;
        Title      = title ?? "Query Execution Plan";
        QueryText  = queryText?.Trim();
        Plan       = ShowplanParser.Parse(planXml);

        // No plan: the XML tab says so, rather than formatting nothing.
        if (!HasPlanXml)
        {
            _xmlText  = $"({NoPlanMessage})";
            _xmlShown = true;
        }
    }

    /// <summary>The window's title.</summary>
    public string Title { get; }

    /// <summary>The query, as far as it fits.</summary>
    public string? QueryText { get; }

    /// <summary>The plan, parsed for the diagram.</summary>
    public ExecutionPlan Plan { get; }

    /// <summary>There is plan XML to copy and save.</summary>
    public bool HasPlanXml => !string.IsNullOrWhiteSpace(_planXml);

    /// <summary>The XML tab's text: formatted the first time the tab is shown, which takes a moment for a large plan.</summary>
    [ObservableProperty]
    private string _xmlText = string.Empty;

    /// <summary>The tab on show: <see cref="GraphicalTab"/> or <see cref="XmlTab"/>.</summary>
    [ObservableProperty]
    private int _selectedTab = GraphicalTab;

    partial void OnSelectedTabChanged(int value)
    {
        if (value != XmlTab || _xmlShown)
            return;
        XmlText = FormatXml(_planXml!);
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

    /// <summary>The diagram has operators to export; the window says once it has drawn it.</summary>
    public bool HasDiagram
    {
        get => _hasDiagram;
        set
        {
            if (SetProperty(ref _hasDiagram, value))
                ExportDiagramCommand.NotifyCanExecuteChanged();
        }
    }

    private bool _hasDiagram;

    /// <summary>Draws the diagram to a PNG at the path given; the window's, since only it has the diagram.</summary>
    public Action<string>? RenderDiagram { get; set; }

    /// <summary>Close was pressed.</summary>
    public event Action? CloseRequested;

    /// <summary>Exports the diagram as a PNG where the user picks; a failure says why.</summary>
    [RelayCommand(CanExecute = nameof(HasDiagram))]
    private void ExportDiagram()
    {
        var path = _files.Save(new FileDialogOptions("Export Execution Plan Diagram", "PNG image (*.png)|*.png") { FileName = "ExecutionPlan" });
        if (path is null)
            return;

        try
        {
            // Rendering needs the diagram laid out, which it isn't while the XML tab shows.
            SelectedTab = GraphicalTab;
            RenderDiagram?.Invoke(path);
        }
        catch (Exception ex)
        {
            _dialogs.Error($"Failed to export the diagram:\n{ex.Message}", "Export Failed");
        }
    }

    [RelayCommand(CanExecute = nameof(HasPlanXml))]
    private void Copy() => _clipboard.SetText(_planXml!);

    /// <summary>Saves the plan as .sqlplan (or .xml) where the user picks; it opens in SSMS too.</summary>
    [RelayCommand(CanExecute = nameof(HasPlanXml))]
    private void Save()
    {
        var path = _files.Save(new FileDialogOptions(
            "Save Execution Plan", "SQL Server Execution Plan (*.sqlplan)|*.sqlplan|XML File (*.xml)|*.xml") { FileName = "ExecutionPlan" });
        if (path is null)
            return;

        try
        {
            File.WriteAllText(path, _planXml, System.Text.Encoding.UTF8);
            _dialogs.Inform("Plan saved successfully.\nIt can also be opened in SSMS or Azure Data Studio.", "Saved");
        }
        catch (Exception ex)
        {
            _dialogs.Error($"Failed to save the file:\n{ex.Message}", "Save Failed");
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
