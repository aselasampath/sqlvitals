using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>
/// Settings → Health thresholds (#131): the thresholds every connection without its own is graded
/// on, in the Checks table, with an explicit Save so a half-typed threshold never grades a sample.
/// </summary>
internal partial class ThresholdsCategory : UserControl, ISettingsCategory
{
    private readonly SettingsContext _context;

    // The boxes as last saved or loaded.
    private string _saved = string.Empty;

    public ThresholdsCategory(SettingsContext context)
    {
        _context = context;
        InitializeComponent();
        TxtSubtitle.Text = SettingsNav.Info(SettingsCategory.Thresholds).Subtitle;

        GlobalThresholds.ShowsSample   = false;
        GlobalThresholds.ShowsDefaults = false;
        GlobalThresholds.Defaults      = HealthThresholds.Default;
        GlobalThresholds.Changed      += UpdateBar;
        Load();
    }

    // A connection's own thresholds turned on or off meanwhile; what's typed stays.
    public void OnShownAgain()
    {
        if (!IsDirty)
            Load();
    }

    /// <summary>Whether a box differs from the saved thresholds.</summary>
    public bool IsDirty => GlobalThresholds.Snapshot != _saved;

    public Task<bool> TryLeaveAsync()
    {
        if (!IsDirty)
            return Task.FromResult(true);

        var answer = _context.Ask(
            "Save the changes to the default health thresholds?\n\nYes saves them, No discards them, Cancel stays on this page.",
            "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return Task.FromResult(answer switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No  => Discard(),
            _                    => false,
        });
    }

    private void Load()
    {
        var store = _context.Service.Load();
        GlobalThresholds.Show(store.HealthThresholds);
        _saved = GlobalThresholds.Snapshot;

        var own = store.Connections.Count(c => c.HealthThresholds is not null);
        TxtIntro.Text = "When a connection's health dot turns amber or red, and what each check costs its health score. " +
                        (own switch
                        {
                            0     => "Every connection uses these; one can use its own instead, on its Health thresholds tab.",
                            1     => "1 connection uses its own instead, set on its Health thresholds tab.",
                            var n => $"{n} connections use their own instead, set on their Health thresholds tabs.",
                        });
        UpdateBar();
    }

    private void UpdateBar()
    {
        var dirty = IsDirty;
        TxtUnsaved.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        BtnDiscard.IsEnabled  = dirty;
        BtnSave.IsEnabled     = dirty;
        if (dirty)
            SettingsContext.ShowStatus(TxtStatus, string.Empty, null);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void Discard_Click(object sender, RoutedEventArgs e) => Discard();

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        GlobalThresholds.Show(HealthThresholds.Default);
        SettingsContext.ShowStatus(TxtStatus, "Defaults restored in the boxes. Save to use them.", success: null);
    }

    /// <summary>Saves the boxes for every connection without its own. True when it saved.</summary>
    internal bool Save()
    {
        if (!GlobalThresholds.TryRead(out var thresholds, out var error))
        {
            SettingsContext.ShowStatus(TxtStatus, error, success: false);
            return false;
        }

        try
        {
            _context.Service.SetHealthThresholds(thresholds!);
            var store = _context.Service.Load();
            _context.Monitoring.ApplyHealthThresholds(store);
            Load();

            var own = store.Connections.Count(c => c.HealthThresholds is not null);
            SettingsContext.ShowStatus(TxtStatus,
                own == 0
                    ? "Saved. Every connection's health dot now uses these thresholds."
                    : $"Saved. Every connection's health dot now uses these thresholds, except {own} with its own.",
                success: true);
            return true;
        }
        catch (Exception ex)
        {
            SettingsContext.ShowStatus(TxtStatus, $"Could not save the thresholds: {ex.Message}", success: false);
            return false;
        }
    }

    private bool Discard()
    {
        Load();
        SettingsContext.ShowStatus(TxtStatus, string.Empty, null);
        return true;
    }
}
