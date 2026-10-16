using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Settings;

namespace SqlVitals.Desktop.ViewModels.Settings;

/// <summary>
/// Settings → Health thresholds (#131): the thresholds and weights (#173) every connection without
/// its own is graded on, in the Checks table, with an explicit Save so a half-typed threshold never
/// grades a sample, and what each monitored connection on them would score. Save is disabled while
/// a box can't be read, and the first such box is said under the table (#190).
/// </summary>
public sealed partial class ThresholdsCategoryViewModel : SettingsCategoryViewModel
{
    private readonly SettingsContext _context;

    // The boxes as last saved or loaded.
    private string _saved = string.Empty;
    private bool _loading;

    internal ThresholdsCategoryViewModel(SettingsContext context)
    {
        _context = context;
        Thresholds.ShowsSample   = false;
        Thresholds.ShowsDefaults = false;
        Thresholds.Defaults      = HealthThresholds.Default;
        Thresholds.Changed      += OnThresholdsChanged;
        Load();
    }

    public override string Subtitle => SettingsNav.Info(SettingsCategory.Thresholds).Subtitle;

    /// <summary>The Checks table.</summary>
    public HealthThresholdsViewModel Thresholds { get; } = new();

    /// <summary>"When a connection's health dot turns amber or red…", with how many use their own.</summary>
    [ObservableProperty]
    private string _intro = string.Empty;

    /// <summary>"On the latest sample, UAT would score 70 → 80", a line per monitored connection on these.</summary>
    [ObservableProperty]
    private string? _preview;

    /// <summary>The first box that can't be read; null when they all can.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveThresholdsCommand))]
    private string? _problem;

    /// <summary>Whether a box differs from the saved thresholds.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveThresholdsCommand), nameof(DiscardThresholdsCommand))]
    private bool _isDirty;

    public StatusLine Status { get; } = new();

    // A connection's own thresholds turned on or off meanwhile; what's typed stays.
    public override void OnShownAgain()
    {
        if (!IsDirty)
            Load();
    }

    public override Task<bool> TryLeaveAsync()
    {
        if (!IsDirty)
            return Task.FromResult(true);

        return Task.FromResult(_context.Dialogs.AskToSave(
                "Save the changes to the default health thresholds?\n\nYes saves them, No discards them, Cancel stays on this page.",
                "Unsaved changes") switch
        {
            DialogAnswer.Yes => Save(),
            DialogAnswer.No  => Discard(),
            _                => false,
        });
    }

    private void Load()
    {
        var store = _context.Service.Load();
        _loading = true;
        try { Thresholds.Show(store.HealthThresholds); }
        finally { _loading = false; }
        _saved = Thresholds.Snapshot;

        var own = store.Connections.Count(c => c.HealthThresholds is not null);
        Intro = "When a connection's health dot turns amber or red, and what each check costs its health score. " +
                (own switch
                {
                    0     => "Every connection uses these; one can use its own instead, on its Health thresholds tab.",
                    1     => "1 connection uses its own instead, set on its Health thresholds tab.",
                    var n => $"{n} connections use their own instead, set on their Health thresholds tabs.",
                });
        Update();
    }

    private void OnThresholdsChanged()
    {
        if (_loading)
            return;
        Update();
        if (IsDirty)
            Status.Clear();
    }

    private void Update()
    {
        IsDirty = Thresholds.Snapshot != _saved;
        Problem = Thresholds.TryRead(out var thresholds, out var error, focus: false) ? null : error;
        UpdatePreview(thresholds);
    }

    // "On the latest sample, UAT would score 70 → 80": each monitored connection on the default
    // thresholds, graded on what's in the boxes (#173).
    private void UpdatePreview(HealthThresholds? thresholds)
    {
        if (thresholds is null)
        {
            Preview = null;
            return;
        }

        var lines = _context.Service.Load().Connections
            .Where(c => c.HealthThresholds is null)
            .Select(c => (c.DisplayName, Session: _context.Monitoring.Get(c.Id)))
            .Where(c => c.Session is { IsRunning: true })
            .Select(c => ThresholdsPreview.For(c.Session!.Score, thresholds)?.Text(c.DisplayName))
            .OfType<string>()
            .OrderBy(line => line, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Preview = lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>Grades every connection without its own thresholds on these; disabled while a box can't be read.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveThresholds() => Save();

    private bool CanSave => IsDirty && Problem is null;

    /// <summary>Saves the boxes for every connection without its own. True when it saved.</summary>
    internal bool Save()
    {
        if (!Thresholds.TryRead(out var thresholds, out var error))
        {
            Status.Show(error, success: false);
            return false;
        }

        try
        {
            _context.Service.SetHealthThresholds(thresholds!);
            var store = _context.Service.Load();
            _context.Monitoring.ApplyHealthThresholds(store);
            Load();

            var own = store.Connections.Count(c => c.HealthThresholds is not null);
            Status.Show(own == 0
                    ? "Saved. Every connection's health dot now uses these thresholds."
                    : $"Saved. Every connection's health dot now uses these thresholds, except {own} with its own.",
                success: true);
            return true;
        }
        catch (Exception ex)
        {
            Status.Show($"Could not save the thresholds: {ex.Message}", success: false);
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void DiscardThresholds() => Discard();

    private bool Discard()
    {
        Load();
        Status.Clear();
        return true;
    }

    /// <summary>SqlVitals' own defaults back in the boxes; Save uses them.</summary>
    [RelayCommand]
    private void Reset()
    {
        Thresholds.Show(HealthThresholds.Default);
        Status.Show("Defaults restored in the boxes. Save to use them.", success: null);
    }
}
