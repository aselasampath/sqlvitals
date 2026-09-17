using System.Text;
using System.Windows;
using System.Windows.Controls;
using SqlPulse.Engine.Models;
using SqlPulse.Engine.Repositories;
using SqlPulse.Desktop.Helpers;
using SqlPulse.Desktop.Windows;

namespace SqlPulse.Desktop.Pages;

public partial class ResourceQueriesPage : Page, IRefreshable
{
    private readonly IWaitStatsRepository _repo;
    private int _topN = 20;

    public ResourceQueriesPage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();
    }

    public async Task RefreshAsync()
    {
        var (byReads, byCpu, byHighestReads) = await _repo.GetResourceIntensiveQueriesAsync(_topN);

        if (ReadsGrid != null)       ReadsGrid.ItemsSource       = byReads.ToList();
        if (CpuGrid != null)         CpuGrid.ItemsSource         = byCpu.ToList();
        if (HighestReadsGrid != null) HighestReadsGrid.ItemsSource = byHighestReads.ToList();
    }

    private async void CmbTopN_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cmb || cmb.SelectedItem is not ComboBoxItem item) return;
        if (!int.TryParse(item.Content.ToString(), out int val)) return;

        _topN = val;
        if (!IsLoaded) return;

        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to reload queries:\n{ex.Message}",
                "Refresh Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ViewPlan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ResourceIntensiveQuery q) return;

        btn.IsEnabled = false;
        btn.Content   = "Loading…";
        try
        {
            string? planXml;
            if (!string.IsNullOrEmpty(q.ObjectName))
            {
                planXml  = await _repo.GetObjectQueryPlanAsync(q.ObjectName);
                planXml ??= q.QueryPlan;
            }
            else
            {
                planXml = q.QueryPlan;
            }

            new QueryExecutionPlanWindow(planXml, q.QueryText) { Owner = Window.GetWindow(this) }.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to retrieve execution plan:\n{ex.Message}",
                "Plan Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            btn.IsEnabled = true;
            btn.Content   = "View Plan";
        }
    }

    private void CopyReads_Click(object sender, RoutedEventArgs e)        => CopyGrid(ReadsGrid);
    private void CopyCpu_Click(object sender, RoutedEventArgs e)          => CopyGrid(CpuGrid);
    private void CopyHighestReads_Click(object sender, RoutedEventArgs e) => CopyGrid(HighestReadsGrid);

    private static void CopyGrid(DataGrid grid)
    {
        if (grid.ItemsSource == null) return;

        try
        {
            var sb = new StringBuilder();

            foreach (var col in grid.Columns)
                sb.Append(col.Header + "\t");
            sb.AppendLine();

            foreach (var item in grid.ItemsSource)
            {
                var props = item.GetType().GetProperties();
                foreach (var col in grid.Columns)
                {
                    if (col is DataGridTextColumn textCol &&
                        textCol.Binding is System.Windows.Data.Binding binding)
                    {
                        var prop = props.FirstOrDefault(p => p.Name == binding.Path.Path);
                        var val  = prop?.GetValue(item)?.ToString() ?? "";
                        sb.Append(val.Replace("\t", " ").Replace("\n", " ") + "\t");
                    }
                    else
                    {
                        sb.Append("\t");
                    }
                }
                sb.AppendLine();
            }

            ClipboardHelper.SetText(sb.ToString());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to copy data:\n{ex.Message}",
                "Copy Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
