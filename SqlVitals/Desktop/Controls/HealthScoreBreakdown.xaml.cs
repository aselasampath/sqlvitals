using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// What a health score is made of (#36): each check's figure, thresholds, weight and the points
/// it cost, with the ones that lowered the score highlighted.
/// </summary>
public partial class HealthScoreBreakdown : UserControl
{
    public HealthScoreBreakdown() => InitializeComponent();

    /// <summary>
    /// Shows only the checks and the footnote, without its own frame and score, for a page that
    /// shows the score itself.
    /// </summary>
    public void ShowInline()
    {
        Frame.Background      = null;
        Frame.BorderThickness = new Thickness(0);
        Frame.Padding         = new Thickness(0);
        Frame.MinWidth        = 0;
        Frame.MaxWidth        = double.PositiveInfinity;
        Header.Visibility     = Visibility.Collapsed;
        TxtLoweredBy.Visibility = Visibility.Collapsed;
        GridChecks.Margin     = new Thickness(0);
    }

    /// <summary>
    /// Opens the page that explains a check (#130). When set, each check that cost points gets a
    /// link under its name, worded as <see cref="Services.EvidenceRoutes"/> says.
    /// </summary>
    public Action<HealthIndicator>? OpenCheck { get; set; }

    /// <summary>The footnote under the checks, e.g. which thresholds the connection uses.</summary>
    public string Footnote
    {
        get => TxtFootnote.Text;
        set => TxtFootnote.Text = value;
    }

    /// <param name="connectionName">Named in the subtitle.</param>
    /// <param name="score">Null when there is no score yet; <paramref name="noScore"/> then says why.</param>
    /// <param name="paused">The connection's monitoring is paused, so the score is the last one taken.</param>
    public void Show(string connectionName, HealthScore? score, string noScore, bool paused)
    {
        GridChecks.Children.Clear();
        GridChecks.RowDefinitions.Clear();

        if (score is null)
        {
            RunScore.Text       = "—";
            RunScore.Foreground = HealthBrushes.Grey;
            TxtSubtitle.Text    = connectionName;
            TxtLoweredBy.Text   = noScore;
            GridChecks.Visibility = Visibility.Collapsed;
            return;
        }

        RunScore.Text       = score.Value.ToString();
        RunScore.Foreground = HealthBrushes.For(score.Level);
        TxtSubtitle.Text    = $"{connectionName} · latest sample at {score.Time:HH:mm:ss}" +
                              (paused ? " · monitoring paused" : string.Empty);

        if (score.Level == HealthLevel.Unavailable)
        {
            TxtLoweredBy.Text     = score.Problem ?? "The server couldn't be reached or read.";
            GridChecks.Visibility = Visibility.Collapsed;
            return;
        }

        TxtLoweredBy.Text = score.Lowering.Count == 0
            ? "Nothing lowered it: every check is within its thresholds."
            : $"Lowered by {score.LoweredBy()}.";
        GridChecks.Visibility = Visibility.Visible;

        AddRow(header: true, null, "Check", "Now", "Warning", "Critical", "Weight", "Points");
        foreach (var check in score.Checks)
        {
            var lost = check.PointsLost > 0;
            var row  = AddRow(header: false, check.Level, check.Name, check.ValueText, check.WarningText, check.CriticalText,
                              check.Weight.ToString(), lost ? $"−{check.PointsLost}" : "0");
            foreach (var cell in row)
            {
                cell.ToolTip = check.Reading.Reason ?? HealthThresholds.Info(check.Indicator).Hint;
                if (lost)
                    cell.FontWeight = FontWeights.SemiBold;
            }
            if (lost)
                row[^1].Foreground = HealthBrushes.For(check.Level);
            if (lost && OpenCheck is { } open)
                AddLink(row[0], check.Indicator, open);
        }
        AddRow(header: true, null, "Score", "", "", "", HealthScore.Max.ToString(), score.Value.ToString());
    }

    // "Blocking chain →" on a line of its own under the check's name. The name cell's tooltip
    // explains the figure; the link's says where it goes.
    private static void AddLink(TextBlock nameCell, HealthIndicator indicator, Action<HealthIndicator> open)
    {
        var link = Services.EvidenceRoutes.For(indicator);
        var hyperlink = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run($"{link.Text} →"))
        {
            FontWeight = FontWeights.SemiBold,
            ToolTip    = $"Open {link.Text} for this connection",
        };
        hyperlink.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Accent");
        System.Windows.Automation.AutomationProperties.SetName(hyperlink, link.Text);
        hyperlink.Click += (_, _) => open(indicator);

        nameCell.Inlines.Add(new System.Windows.Documents.LineBreak());
        nameCell.Inlines.Add(new System.Windows.Documents.Run("    "));
        nameCell.Inlines.Add(hyperlink);
    }

    // Adds a row of six cells; a header row is muted and bold. Returns the cells.
    private TextBlock[] AddRow(bool header, HealthLevel? level, params string[] texts)
    {
        var rowIndex = GridChecks.RowDefinitions.Count;
        GridChecks.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var cells = new TextBlock[texts.Length];
        for (var column = 0; column < texts.Length; column++)
        {
            var cell = new TextBlock
            {
                Text                = texts[column],
                FontSize            = 12,
                Margin              = new Thickness(column == 0 ? 0 : 14, 3, 0, 3),
                HorizontalAlignment = column >= 4 ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            cell.SetResourceReference(TextBlock.ForegroundProperty, header ? "TextMuted" : "TextPrimary");
            if (header)
                cell.FontWeight = FontWeights.SemiBold;

            // The check's name gets the dot's colour for its level.
            if (column == 0 && level is { } l)
            {
                cell.Inlines.Clear();
                cell.Inlines.Add(new System.Windows.Documents.Run("● ") { Foreground = HealthBrushes.For(l) });
                cell.Inlines.Add(new System.Windows.Documents.Run(texts[0]));
            }

            Grid.SetRow(cell, rowIndex);
            Grid.SetColumn(cell, column);
            GridChecks.Children.Add(cell);
            cells[column] = cell;
        }
        return cells;
    }
}
