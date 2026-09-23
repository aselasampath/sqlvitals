using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

public partial class QueryStorePage : System.Windows.Controls.Page, IRefreshable
{
    private readonly IWaitStatsRepository _repo;

    public QueryStorePage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        var (health, topQueries) = await _repo.GetQueryStoreAsync();

        // ── Health status card ─────────────────────────────────────────
        HealthPanel.Children.Clear();
        if (health is null)
        {
            AddHealthCard("Query Store", "Not Enabled", "#94A3B8");
        }
        else
        {
            AddHealthCard("State",         health.ActualState,                  health.IsEnabled ? "#22C55E" : "#EF4444");
            AddHealthCard("Storage Used",  $"{health.CurrentStorageSizeMB:N1} MB", health.StorageUsedPct > 90 ? "#EF4444" : health.StorageUsedPct > 75 ? "#FBBF24" : "#22C55E");
            AddHealthCard("Storage %",     $"{health.StorageUsedPct:N1}%",      health.StorageUsedPct > 90 ? "#EF4444" : "#7C3AED");
            AddHealthCard("Forced Plans",  health.ForcedPlans.ToString(),       health.ForcedPlans > 0 ? "#FBBF24" : "#94A3B8");
            AddHealthCard("Status",        health.Severity,                     health.Severity == "Critical" ? "#EF4444" : health.Severity == "Warning" ? "#FBBF24" : "#22C55E");
        }

        // ── Top queries DataGrid ───────────────────────────────────────
        DataGridRefresh.SetItemsSource(QueriesGrid, topQueries.ToList());
    }

    private void AddHealthCard(string label, string value, string hexColor, string? tooltip = null)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));
        var card  = new Border
        {
            Style  = (Style)Application.Current.FindResource("KpiCard"),
            Margin = new Thickness(0, 0, 10, 10),
            Width  = 200,
        };
        if (tooltip is not null)
            ToolTipService.SetToolTip(card, MakeTooltip(label, tooltip));
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = value, Style = (Style)Application.Current.FindResource("KpiValue"), Foreground = brush, FontSize = 18 });
        sp.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("KpiLabel") });
        card.Child = sp;
        HealthPanel.Children.Add(card);
    }

    private static ToolTip MakeTooltip(string title, string description)
    {
        var sp = new StackPanel { MaxWidth = 340 };
        sp.Children.Add(new TextBlock { Text = title,       FontWeight = FontWeights.Bold,   TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,4) });
        sp.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        return new ToolTip { Content = sp };
    }
}
