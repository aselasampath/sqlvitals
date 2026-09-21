using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlVitals.Installer.Core;

namespace SqlVitals.Installer.Views;

public partial class RequirementsView : UserControl
{
    private readonly SetupSession _session;
    private bool _checking;

    public RequirementsView(SetupSession session)
    {
        InitializeComponent();
        _session = session;
    }

    public event EventHandler? StateChanged;
    public event EventHandler? RestartElevatedRequested;

    public bool CanContinue { get; private set; }

    public async Task RunChecksAsync()
    {
        if (_checking) return;
        _checking = true;
        CanContinue = false;
        BtnCheckAgain.IsEnabled = false;
        Summary.Text = "Making sure this PC is ready for SqlVitals…";
        StateChanged?.Invoke(this, EventArgs.Empty);

        var input = new RequirementInput
        {
            InstallDir    = _session.InstallDir,
            RequiredBytes = _session.RequiredBytes,
            Manifest      = _session.Payload?.Manifest,
            PayloadError  = _session.PayloadError,
            IsElevated    = Elevation.IsElevated,
            Existing      = _session.Existing,
        };

        IReadOnlyList<RequirementResult> results;
        try
        {
            results = await Task.Run(() => SystemRequirements.Evaluate(input));
        }
        finally
        {
            _checking = false;
            BtnCheckAgain.IsEnabled = true;
        }

        foreach (var r in results)
            SetupLog.Info($"Check '{r.Name}': {r.Status} — {r.Detail}");

        Results.ItemsSource = results.Select(r => new Row(r)).ToList();
        CanContinue = SystemRequirements.CanContinue(results);

        var failed   = results.Count(r => r.Status == CheckStatus.Failed);
        var warnings = results.Count(r => r.Status == CheckStatus.Warning);
        Summary.Text = failed > 0
            ? $"{failed} {(failed == 1 ? "item needs" : "items need")} attention before Setup can continue. Follow the \"How to fix\" steps, then click Check again."
            : warnings > 0
                ? "Your PC can run SqlVitals. Review the notes below, then click Next."
                : "Your PC is ready for SqlVitals. Click Next to continue.";

        BtnElevate.Visibility = results.Any(r => r.NeedsElevation && r.Status != CheckStatus.Passed) && !Elevation.IsElevated
            ? Visibility.Visible
            : Visibility.Collapsed;

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CheckAgain_Click(object sender, RoutedEventArgs e) => _ = RunChecksAsync();

    private void Elevate_Click(object sender, RoutedEventArgs e) => RestartElevatedRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Display wrapper for one check result.</summary>
    public sealed class Row
    {
        private readonly RequirementResult _r;
        public Row(RequirementResult r) => _r = r;

        public string  Name       => _r.Name;
        public string  Detail     => _r.Detail;
        public string? Resolution => _r.Resolution;

        public string Icon => _r.Status switch
        {
            CheckStatus.Passed  => "✓",
            CheckStatus.Warning => "!",
            _                   => "✕",
        };

        public Brush IconBrush => Resource(_r.Status switch
        {
            CheckStatus.Passed  => "Success",
            CheckStatus.Warning => "Warning",
            _                   => "Danger",
        });

        public Brush ResolutionBackground => Resource(_r.Status == CheckStatus.Failed ? "DangerSoft" : "WarningSoft");

        public Visibility ResolutionVisibility =>
            _r.Status != CheckStatus.Passed && !string.IsNullOrEmpty(_r.Resolution) ? Visibility.Visible : Visibility.Collapsed;

        private static Brush Resource(string key) => (Brush)Application.Current.FindResource(key);
    }
}
