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
        }
        AddRow(header: true, null, "Score", "", "", "", HealthScore.Max.ToString(), score.Value.ToString());
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
