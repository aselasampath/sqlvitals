using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.SqlClient;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Settings;

namespace SqlVitals.Desktop.ViewModels.Settings;

/// <summary>
/// A connection's page in Settings → Connections (#131), or a new connection's: tabs Connection,
/// Monitoring &amp; alerts, Health thresholds and Advanced, and one bar that saves them all. Moving
/// between tabs keeps what's typed; leaving with unsaved changes asks first.
/// </summary>
/// <remarks>
/// Its rules are validation attributes on the fields (#190): an error is shown under a field once
/// it has been typed in, the first one is said in the save bar, and Save is disabled while there is
/// any. Saving anything that changes how SqlVitals connects signs in first.
/// </remarks>
public sealed partial class ConnectionEditorViewModel : ObservableValidator
{
    private readonly SettingsContext _context;

    // The settings as of the last load or save, and the page as it was then.
    private ConnectionStore _store;
    private ConnectionDraft _saved;

    // Set while the fields are filled in code, so that isn't a change.
    private bool _loading;
    private bool _active;

    // The fields typed in since the page was filled: only their errors are shown under them.
    private readonly HashSet<string> _touched = [];

    // Connection string the database list was last loaded with, so it's only queried again for another server.
    private string? _databaseListSource;

    // The fields in the order their errors are told, as the save checked them before #190.
    private static readonly string[] ErrorOrder =
        [nameof(Server), nameof(UserName), nameof(Password), nameof(ConnectTimeout), nameof(AdditionalParameters), nameof(OwnThresholds), nameof(Name)];

    // The fields Test connection needs.
    private static readonly string[] ConnectionFields =
        [nameof(Server), nameof(UserName), nameof(Password), nameof(ConnectTimeout), nameof(AdditionalParameters)];

    internal ConnectionEditorViewModel(SettingsContext context, Guid? connectionId)
    {
        _context = context;
        SaveCommand           = new AsyncRelayCommand(() => SaveAsync(connect: false), () => CanSave);
        SaveAndConnectCommand = new AsyncRelayCommand(() => SaveAsync(connect: true), () => !IsBusy && !HasErrors);
        TestCommand           = new AsyncRelayCommand(TestAsync, () => !IsBusy);
        BackCommand           = new AsyncRelayCommand(BackAsync);
        RemoveCommand         = new AsyncRelayCommand(RemoveAsync, () => !IsBusy);

        Thresholds.Changed += OnThresholdsChanged;
        ErrorsChanged      += OnErrorsChanged;

        _store = context.Service.Load();
        ConnectionId = _store.Connections.Any(c => c.Id == connectionId) ? connectionId : null;
        _saved = Load(Saved());
    }

    /// <summary>"← Connections", with nothing left unsaved.</summary>
    public event Action? Closed;

    /// <summary>The connection was removed, with what to say about it on the cards.</summary>
    public event Action<string>? Removed;

    /// <summary>Saved, and the app has picked it up: the first-run flow's cue to check it (#177).</summary>
    public event Action<Guid>? ConnectionSaved;

    /// <summary>A field to put the keyboard in, by its property's name: the one a failed Save names.</summary>
    public event Action<string>? FocusRequested;

    /// <summary>⋯ was pressed: the view opens its menu (Remove connection…).</summary>
    public event Action? MenuRequested;

    /// <summary>The connection on the page; null for a new one not saved yet.</summary>
    public Guid? ConnectionId { get; private set; }

    public bool IsNew => ConnectionId is null;

    /// <summary>
    /// The first-run flow's Connect step (#177): no "← Connections", no Save &amp; make active (the
    /// first connection saved is the active one anyway), and Save is the step's main button.
    /// </summary>
    public bool IsFirstRun { get; private set; }

    public void UseForFirstRun()
    {
        IsFirstRun = true;
        OnPropertyChanged(nameof(IsFirstRun));
        RenderHeader();
        UpdateBar();
    }

    // ── The fields ────────────────────────────────────────────────────────────

    /// <summary>The name shown on its tile and in the sidebar; blank for "server / database".</summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(ConnectionEditorViewModel), nameof(ValidateName))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Server name is required.")]
    private string _server = string.Empty;

    [ObservableProperty]
    private SqlAuthMode _authentication = SqlAuthMode.SqlServer;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(ConnectionEditorViewModel), nameof(ValidateUserName))]
    private string _userName = string.Empty;

    /// <summary>Typed in a PasswordBox, whose code-behind copies it here: a password isn't bound.</summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(ConnectionEditorViewModel), nameof(ValidatePassword))]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _savePassword;

    [ObservableProperty]
    private string _database = string.Empty;

    [ObservableProperty]
    private EncryptMode _encrypt = EncryptMode.Mandatory;

    [ObservableProperty]
    private bool _trustServerCertificate;

    /// <summary>Seconds, as typed (Advanced).</summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(ConnectionEditorViewModel), nameof(ValidateConnectTimeout))]
    private string _connectTimeout = string.Empty;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(ConnectionEditorViewModel), nameof(ValidateAdditionalParameters))]
    private string _additionalParameters = string.Empty;

    [ObservableProperty]
    private bool _monitorInBackground;

    /// <summary>Windows notifications for its alerts.</summary>
    [ObservableProperty]
    private bool _notifies = true;

    /// <summary>Use its own health thresholds; its rule is that the boxes can be read.</summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(ConnectionEditorViewModel), nameof(ValidateOwnThresholds))]
    private bool _ownThresholds;

    /// <summary>The Health thresholds tab's table: the defaults read-only, or its own.</summary>
    public HealthThresholdsViewModel Thresholds { get; } = new();

    public IReadOnlyList<Choice<SqlAuthMode>> AuthenticationChoices { get; } =
    [
        new(SqlAuthMode.SqlServer, "SQL Server Authentication"),
        new(SqlAuthMode.EntraMfa, "Microsoft Entra MFA"),
        new(SqlAuthMode.Windows, "Windows Authentication"),
    ];

    public IReadOnlyList<Choice<EncryptMode>> EncryptChoices { get; } =
    [
        new(EncryptMode.Mandatory, "Mandatory"),
        new(EncryptMode.Optional, "Optional"),
        new(EncryptMode.Strict, "Strict (SQL Server 2022 and Azure SQL)"),
    ];

    // ── The rules ─────────────────────────────────────────────────────────────

    public static ValidationResult? ValidateName(string _, ValidationContext context)
    {
        var editor  = (ConnectionEditorViewModel)context.ObjectInstance;
        var display = editor.BuildSettings().DisplayName;
        var id      = editor.ConnectionId;
        return editor._store.Connections.Any(c => c.Id != id && string.Equals(c.DisplayName, display, StringComparison.OrdinalIgnoreCase))
            ? new ValidationResult($"A connection named \"{display}\" already exists. Enter a different connection name.")
            : ValidationResult.Success;
    }

    public static ValidationResult? ValidateUserName(string userName, ValidationContext context) =>
        ((ConnectionEditorViewModel)context.ObjectInstance).Authentication == SqlAuthMode.SqlServer && string.IsNullOrWhiteSpace(userName)
            ? new ValidationResult("Login is required for SQL Server Authentication.")
            : ValidationResult.Success;

    // Never the password itself in the message.
    public static ValidationResult? ValidatePassword(string password, ValidationContext context) =>
        ((ConnectionEditorViewModel)context.ObjectInstance).Authentication == SqlAuthMode.SqlServer && string.IsNullOrEmpty(password)
            ? new ValidationResult("Password is required for SQL Server Authentication.")
            : ValidationResult.Success;

    public static ValidationResult? ValidateConnectTimeout(string timeout, ValidationContext _) =>
        int.TryParse(timeout.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var seconds) && seconds > 0
            ? ValidationResult.Success
            : new ValidationResult("Connection timeout (Advanced) must be a positive integer.");

    public static ValidationResult? ValidateAdditionalParameters(string parameters, ValidationContext context)
    {
        try
        {
            // Credentials typed here would sit in plain text in a visible field, and the form's own
            // fields would silently override them anyway.
            var extra = new SqlConnectionStringBuilder(parameters.Trim());
            if (!string.IsNullOrEmpty(extra.Password) || !string.IsNullOrEmpty(extra.UserID))
                return new ValidationResult("Additional parameters (Advanced) must not contain a user name or password. Use the Login and Password fields instead.");

            _ = ((ConnectionEditorViewModel)context.ObjectInstance).BuildSettings().BuildConnectionString();
            return ValidationResult.Success;
        }
        catch (Exception ex)
        {
            return new ValidationResult($"Additional parameters (Advanced) are not valid: {SettingsContext.Redact(ex.Message)}");
        }
    }

    public static ValidationResult? ValidateOwnThresholds(bool own, ValidationContext context) =>
        own && !((ConnectionEditorViewModel)context.ObjectInstance).Thresholds.TryRead(out _, out var error, focus: false)
            ? new ValidationResult($"Health thresholds — {error}")
            : ValidationResult.Success;

    // ── Errors, as bound ──────────────────────────────────────────────────────

    /// <summary>Under each field, once it has been typed in.</summary>
    public string? ServerError               => Shown(nameof(Server));
    public string? UserNameError             => Shown(nameof(UserName));
    public string? PasswordError             => Shown(nameof(Password));
    public string? NameError                 => Shown(nameof(Name));
    public string? ConnectTimeoutError       => Shown(nameof(ConnectTimeout));
    public string? AdditionalParametersError => Shown(nameof(AdditionalParameters));

    /// <summary>On the Health thresholds tab, as soon as a box can't be read.</summary>
    public string? ThresholdsError => ErrorOf(nameof(OwnThresholds));

    /// <summary>The first error, in the save bar: why Save is disabled, typed in or not.</summary>
    public string? Problem => FirstError(ErrorOrder);

    // Where a field's error is bound: ServerError for Server, ThresholdsError for the own thresholds.
    private static string ErrorPropertyOf(string property) =>
        property == nameof(OwnThresholds) ? nameof(ThresholdsError) : property + "Error";

    private string? Shown(string property) => _touched.Contains(property) ? ErrorOf(property) : null;

    private string? ErrorOf(string property) =>
        GetErrors(property).OfType<ValidationResult>().FirstOrDefault()?.ErrorMessage;

    private string? FirstError(IEnumerable<string> properties) =>
        properties.Select(ErrorOf).FirstOrDefault(e => e is not null);

    private void OnErrorsChanged(object? sender, DataErrorsChangedEventArgs e)
    {
        if (e.PropertyName is { } property)
            OnPropertyChanged(ErrorPropertyOf(property));
        OnPropertyChanged(nameof(Problem));
        NotifyCommands();
    }

    // Every rule again: another connection renamed meanwhile, or the store re-read.
    private void ValidateEverything() => ValidateAllProperties();

    // ── Changes ───────────────────────────────────────────────────────────────

    partial void OnNameChanged(string value) => FieldChanged(nameof(Name));

    partial void OnServerChanged(string value)
    {
        FieldChanged(nameof(Server));
        // Blank names show as "server / database".
        ValidateProperty(Name, nameof(Name));
    }

    partial void OnAuthenticationChanged(SqlAuthMode value)
    {
        ValidateProperty(UserName, nameof(UserName));
        ValidateProperty(Password, nameof(Password));
        OnPropertyChanged(nameof(ShowsUserName));
        OnPropertyChanged(nameof(UserNameLabel));
        OnPropertyChanged(nameof(UserNameRequiredMark));
        OnPropertyChanged(nameof(ShowsUserNameHint));
        OnPropertyChanged(nameof(ShowsPassword));
        FieldChanged(null);
    }

    partial void OnUserNameChanged(string value) => FieldChanged(nameof(UserName));
    partial void OnPasswordChanged(string value) => FieldChanged(nameof(Password));
    partial void OnSavePasswordChanged(bool value) => FieldChanged(null);

    partial void OnDatabaseChanged(string value)
    {
        FieldChanged(null);
        ValidateProperty(Name, nameof(Name));
    }

    partial void OnEncryptChanged(EncryptMode value) => FieldChanged(null);
    partial void OnTrustServerCertificateChanged(bool value) => FieldChanged(null);
    partial void OnConnectTimeoutChanged(string value) => FieldChanged(nameof(ConnectTimeout));
    partial void OnAdditionalParametersChanged(string value) => FieldChanged(nameof(AdditionalParameters));
    partial void OnMonitorInBackgroundChanged(bool value) => FieldChanged(null);
    partial void OnNotifiesChanged(bool value) => FieldChanged(null);

    partial void OnOwnThresholdsChanged(bool value)
    {
        // Off shows the defaults again, so turning it back on starts from them.
        if (!_loading && !value)
        {
            _loading = true;
            try { Thresholds.Show(_store.HealthThresholds); }
            finally { _loading = false; }
            ValidateProperty(OwnThresholds, nameof(OwnThresholds));
        }
        ApplyThresholdsMode();
        FieldChanged(null);
    }

    private void OnThresholdsChanged()
    {
        if (_loading)
            return;
        ValidateProperty(OwnThresholds, nameof(OwnThresholds));
        FieldChanged(null);
    }

    private void FieldChanged(string? property)
    {
        if (_loading)
            return;
        if (property is not null && _touched.Add(property))
            OnPropertyChanged(ErrorPropertyOf(property));
        // Its rule builds the whole connection string, so another field can make or mend its error.
        if (property != nameof(AdditionalParameters))
            ValidateProperty(AdditionalParameters, nameof(AdditionalParameters));
        UpdatePreview();
        UpdateBar();
    }

    // ── Loading ───────────────────────────────────────────────────────────────

    private ConnectionSettings Saved() =>
        _store.Connections.FirstOrDefault(c => c.Id == ConnectionId)?.Clone() ?? new ConnectionSettings();

    // Fills every tab from the settings, and returns the page as it now stands: what's saved.
    private ConnectionDraft Load(ConnectionSettings settings)
    {
        _loading = true;
        try
        {
            Name                   = settings.Name;
            Server                 = settings.Server;
            Authentication         = settings.Authentication;
            UserName               = settings.UserName;
            Password               = settings.Password;
            SavePassword           = settings.SavePassword;
            Databases              = [];
            Database               = settings.Database;
            Encrypt                = settings.Encrypt;
            TrustServerCertificate = settings.TrustServerCertificate;
            ConnectTimeout         = settings.ConnectTimeoutSeconds.ToString(CultureInfo.CurrentCulture);
            AdditionalParameters   = settings.AdditionalParameters;
            MonitorInBackground    = settings.MonitorInBackground;
            Notifies               = ConnectionId is not { } id || _store.NotifiesFor(id);

            // Starts from the shared thresholds, so turning the switch on begins from what the dot uses now.
            Thresholds.Defaults = _store.HealthThresholds;
            Thresholds.Show(settings.HealthThresholds ?? _store.HealthThresholds);
            OwnThresholds = settings.HealthThresholds is not null;

            _databaseListSource = null;
            ApplyThresholdsMode();
            ShowChannels();
        }
        finally
        {
            _loading = false;
        }

        _touched.Clear();
        ValidateEverything();
        foreach (var property in ErrorOrder)
            OnPropertyChanged(ErrorPropertyOf(property));
        RenderHeader();
        UpdatePreview();
        var draft = Draft();
        _saved = draft;
        UpdateBar();
        return draft;
    }

    private ConnectionDraft Draft() => new(
        Name:                   Name.Trim(),
        Server:                 Server.Trim(),
        Authentication:         Authentication,
        UserName:               UserName.Trim(),
        Password:               Password,
        SavePassword:           SavePassword,
        Database:               Database.Trim(),
        Encrypt:                Encrypt,
        TrustServerCertificate: TrustServerCertificate,
        ConnectTimeout:         ConnectTimeout.Trim(),
        AdditionalParameters:   AdditionalParameters.Trim(),
        MonitorInBackground:    MonitorInBackground,
        Notifies:               Notifies,
        OwnThresholds:          OwnThresholds,
        Thresholds:             OwnThresholds ? Thresholds.Snapshot : string.Empty);

    // Reads the Connection and Advanced tabs, and Monitor in background, as they would be saved.
    private ConnectionSettings BuildSettings()
    {
        var auth = Authentication;
        return new ConnectionSettings
        {
            Id                     = ConnectionId ?? Guid.Empty,
            Name                   = Name.Trim(),
            Server                 = Server.Trim(),
            Authentication         = auth,
            UserName               = auth == SqlAuthMode.Windows ? string.Empty : UserName.Trim(),
            Password               = auth == SqlAuthMode.SqlServer ? Password : string.Empty,
            SavePassword           = auth == SqlAuthMode.SqlServer && SavePassword,
            Database               = Database.Trim(),
            Encrypt                = Encrypt,
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeoutSeconds  = int.TryParse(ConnectTimeout.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var seconds) && seconds > 0
                                     ? seconds : 30,
            AdditionalParameters   = AdditionalParameters.Trim(),
            MonitorInBackground    = MonitorInBackground,
            // For every connection, in Monitoring & history: kept as it is.
            CommandTimeoutSeconds  = _store.CommandTimeoutSeconds,
        };
    }

    // ── The save bar ──────────────────────────────────────────────────────────

    /// <summary>Whether anything on any tab differs from what's saved.</summary>
    public bool IsDirty => _isDirty;
    private bool _isDirty;

    /// <summary>"● Unsaved changes on Connection and Health thresholds"; empty with none.</summary>
    [ObservableProperty]
    private string _unsavedText = string.Empty;

    /// <summary>The OWN tag on the Health thresholds tab.</summary>
    [ObservableProperty]
    private bool _showsOwnTag;

    /// <summary>Saving, or removing: the buttons wait.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(SaveAndConnectCommand), nameof(DiscardCommand),
                                nameof(TestCommand), nameof(MoreCommand), nameof(RemoveCommand))]
    private bool _isBusy;

    /// <summary>What Save, Test or Remove said last.</summary>
    public StatusLine Status { get; } = new();

    private bool CanSave => !IsBusy && !HasErrors && (IsDirty || IsNew);

    private void UpdateBar()
    {
        var draft = Draft();
        _isDirty = !draft.Equals(_saved);
        var tabs = draft.ChangedTabs(_saved);
        UnsavedText = tabs.Count > 0 ? "●  Unsaved changes on " + JoinAnd(tabs.Select(SettingsNav.Title).ToList()) : string.Empty;
        ShowsOwnTag = OwnThresholds;
        OnPropertyChanged(nameof(IsDirty));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        SaveCommand.NotifyCanExecuteChanged();
        SaveAndConnectCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }

    private static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    /// <summary>Save, as the button reads: "Save & continue" on the first run.</summary>
    public string SaveText => IsFirstRun ? "Save & continue" : "Save";

    public string SaveToolTip => IsFirstRun ? "Sign in with these details, save them, and go on to check the login's permissions"
                               : IsNew      ? "Save this connection without switching to it"
                               :              "Save every tab of this connection without switching to it";

    /// <summary>Save &amp; make active: not for the active connection, nor on the first run.</summary>
    public bool ShowsSaveAndConnect => !IsFirstRun && !(ConnectionId is { } id && _store.ActiveConnectionId == id);

    // ── Header ────────────────────────────────────────────────────────────────

    /// <summary>The name as saved, not as typed: "New connection" until it is.</summary>
    [ObservableProperty]
    private string _title = "New connection";

    /// <summary>Its status chip and dot: shown once it is saved.</summary>
    [ObservableProperty]
    private bool _hasStatus;

    [ObservableProperty]
    private TileStatus _tileStatus = TileStatus.Off;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _statusName = string.Empty;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>Where it points, or what to do for a new one.</summary>
    [ObservableProperty]
    private string _line = string.Empty;

    // The name, its status and score, and where it points: as saved, not as typed.
    private void RenderHeader()
    {
        var conn = _store.Connections.FirstOrDefault(c => c.Id == ConnectionId);
        if (conn is null)
        {
            Title     = "New connection";
            HasStatus = false;
            IsActive  = false;
            Line      = IsFirstRun
                ? "Enter the server's details, then Save & continue. SqlVitals signs in to check them first."
                : "Enter the server's details, then Save. SqlVitals signs in to check them first.";
        }
        else
        {
            var tile = ServerTile.From(HomeViewModel.InputFor(conn, _store, _context.Monitoring), DateTime.Now);
            Title      = conn.DisplayName;
            HasStatus  = true;
            TileStatus = tile.Status;
            StatusText = tile.Status is TileStatus.Off or TileStatus.Waiting or TileStatus.Unreachable
                ? tile.StatusLabel
                : $"{tile.StatusLabel} · {tile.ScoreText}";
            StatusName = tile.AutomationName;
            IsActive   = tile.IsActive;
            Line       = $"{conn.Server.Trim()} / {conn.DatabaseLabel} · {conn.AuthenticationLabel}";
        }
        OnPropertyChanged(nameof(SaveText));
        OnPropertyChanged(nameof(SaveToolTip));
        OnPropertyChanged(nameof(ShowsSaveAndConnect));
        OnPropertyChanged(nameof(IsNew));
    }

    // ── Connection fields ─────────────────────────────────────────────────────

    // Mirrors SSMS: the credential fields shown depend on the authentication type.
    public bool   ShowsUserName        => Authentication != SqlAuthMode.Windows;
    public string UserNameLabel        => Authentication == SqlAuthMode.EntraMfa ? "User name" : "Login";
    public string UserNameRequiredMark => Authentication == SqlAuthMode.SqlServer ? " *" : string.Empty;
    public bool   ShowsUserNameHint    => Authentication == SqlAuthMode.EntraMfa;
    public bool   ShowsPassword        => Authentication == SqlAuthMode.SqlServer;

    /// <summary>The databases listed in the Database box once it is opened.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _databases = [];

    [ObservableProperty]
    private string _databaseHint = "Leave blank for the login's default database. Open the list to browse the server.";

    /// <summary>The Database box's list is open: opening it lists the server's databases.</summary>
    [ObservableProperty]
    private bool _isDatabaseListOpen;

    private bool _isListingDatabases;

    partial void OnIsDatabaseListOpenChanged(bool value)
    {
        if (value)
            _ = ListDatabasesAsync();
    }

    /// <summary>The databases on the server, listed from master as the login sees them; said in the hint when it can't.</summary>
    [ReportsItsErrors]
    internal async Task ListDatabasesAsync()
    {
        if (_isListingDatabases || FirstError(ConnectionFields) is not null)
            return;

        // Browse from master: the typed database may not exist, or the login may lack access to it.
        var settings = BuildSettings();
        var connStr  = settings.BuildConnectionString(databaseOverride: string.Empty);
        if (connStr == _databaseListSource)
            return;

        _isListingDatabases = true;
        var typed = Database;
        DatabaseHint = settings.Authentication == SqlAuthMode.EntraMfa
            ? "Loading databases… complete the Microsoft sign-in if prompted."
            : "Loading databases…";
        try
        {
            var names = await _context.ListDatabases(connStr);

            // Replacing the list clears an editable box's text: put back what was typed.
            _loading = true;
            try
            {
                Databases = names;
                Database  = typed;
            }
            finally
            {
                _loading = false;
            }
            _databaseListSource = connStr;
            DatabaseHint = $"{names.Count} database(s) on {settings.Server}.";
        }
        catch (Exception ex)
        {
            IsDatabaseListOpen = false;
            DatabaseHint = $"Could not list databases: {SettingsContext.Redact(ex.Message)} You can still type a name.";
        }
        finally
        {
            _isListingDatabases = false;
        }
    }

    // ── Monitoring & alerts ───────────────────────────────────────────────────

    /// <summary>The channels that would post its alerts: on, and for every connection or this one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoChannels))]
    private IReadOnlyList<string> _channels = [];

    public bool HasNoChannels => Channels.Count == 0;

    private void ShowChannels() =>
        Channels = _store.ChatChannels
            .Where(c => ConnectionId is { } id ? c.Includes(id) : c.AllConnections)
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => $"{c.Name} · {ChatServices.Name(c.Service)} · " +
                         (c.CriticalOnly ? "critical only" : "warnings and critical") +
                         (c.Enabled ? string.Empty : " · off"))
            .ToList();

    [RelayCommand]
    private void AlertsAndChat() => _context.Open(new SettingsLocation(SettingsCategory.Alerts));

    // ── Health thresholds ─────────────────────────────────────────────────────

    /// <summary>"Grade this connection's health dot on its own thresholds… Off: UAT uses the".</summary>
    [ObservableProperty]
    private string _ownHelp = string.Empty;

    [ObservableProperty]
    private string _thresholdsNote = string.Empty;

    /// <summary>"On the latest sample, UAT would score 70 → 80": with its own thresholds and a sample.</summary>
    [ObservableProperty]
    private string? _preview;

    private void ApplyThresholdsMode()
    {
        var own  = OwnThresholds;
        var name = _store.Connections.FirstOrDefault(c => c.Id == ConnectionId)?.DisplayName ?? "This connection";
        Thresholds.IsReadOnly    = !own;
        Thresholds.ShowsDefaults = own;
        OwnHelp = $"For a server that always runs hot. Off: {name} uses the ";
        ThresholdsNote = own
            ? "Edited fields have an accent border. Leave a threshold blank to turn that level off. Weights are relative, and 0 leaves " +
              "a check out of the score. Past warning a check loses half its points; past critical, all of them."
            : $"{name} uses the default thresholds, shown here read-only. Turn on Use its own health thresholds to change them for it alone.";
    }

    [RelayCommand]
    private void ResetThresholds() => Thresholds.Show(_store.HealthThresholds);

    [RelayCommand]
    private void DefaultThresholds() => _context.Open(new SettingsLocation(SettingsCategory.Thresholds));

    // Grades the latest sample on what's in the boxes: "On the latest sample, UAT would score 70 → 80".
    private void UpdatePreview()
    {
        var session = _context.Monitoring.Get(ConnectionId);
        var latest  = session is { IsRunning: true } ? session.Score : null;

        HealthThresholds? thresholds = _store.HealthThresholds;
        if (OwnThresholds && !Thresholds.TryRead(out thresholds, out _, focus: false))
        {
            // A box can't be read yet: keep the last grades, without a score that doesn't add up.
            Preview = null;
            return;
        }

        var preview = ThresholdsPreview.For(latest, thresholds!);
        Thresholds.ShowSample(latest, preview?.Regraded);

        var name = _store.Connections.FirstOrDefault(c => c.Id == ConnectionId)?.DisplayName;
        Preview = OwnThresholds && preview is not null && name is not null ? preview.Text(name) : null;
    }

    // ── Tabs ──────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTab))]
    private int _selectedTabIndex;

    public ConnectionTab SelectedTab => (ConnectionTab)Math.Max(0, SelectedTabIndex);

    public void SelectTab(ConnectionTab tab) => SelectedTabIndex = (int)tab;

    // ── Following what changes ────────────────────────────────────────────────

    /// <summary>The page is on screen: its header follows the session, and the defaults the settings.</summary>
    public void Activate()
    {
        if (_active)
            return;
        _active = true;
        _context.Monitoring.SessionStateChanged += OnSessionStateChanged;
        _context.Service.Changed                += OnSettingsChanged;
        RenderHeader();
        UpdatePreview();
    }

    public void Deactivate()
    {
        if (!_active)
            return;
        _active = false;
        _context.Monitoring.SessionStateChanged -= OnSessionStateChanged;
        _context.Service.Changed                -= OnSettingsChanged;
    }

    private void OnSessionStateChanged(Guid connectionId)
    {
        if (connectionId != ConnectionId)
            return;
        RenderHeader();
        UpdatePreview();
    }

    // Renamed, made active, or another connection's change. The header and the defaults follow;
    // what's typed stays.
    private void OnSettingsChanged(IReadOnlyList<ActivityEntry> rows) => _context.Ui.Post(SettingsChanged);

    internal void SettingsChanged()
    {
        _store = _context.Service.Load();
        _loading = true;
        try
        {
            Thresholds.Defaults = _store.HealthThresholds;
            // Showing the defaults, which may have just changed: what's saved changes with them.
            if (!OwnThresholds)
                Thresholds.Show(_store.HealthThresholds);
            ShowChannels();
        }
        finally
        {
            _loading = false;
        }
        ValidateProperty(Name, nameof(Name));
        RenderHeader();
        UpdatePreview();
        UpdateBar();
    }

    // ── Save, discard, leave ──────────────────────────────────────────────────

    public IAsyncRelayCommand SaveCommand { get; }

    public IAsyncRelayCommand SaveAndConnectCommand { get; }

    /// <summary>Puts back what's saved, on every tab.</summary>
    [RelayCommand(CanExecute = nameof(CanDiscard))]
    public void Discard()
    {
        _store = _context.Service.Load();
        _saved = Load(Saved());
        Status.Clear();
    }

    private bool CanDiscard => IsDirty && !IsBusy;

    /// <summary>
    /// Saves every tab in one write. Anything that changes how SqlVitals connects is checked
    /// against the server first, and nothing is saved when it can't sign in, so the list only
    /// ever holds working connections. True when it saved.
    /// </summary>
    /// <param name="connect">Also make it the active connection, and open its Overview.</param>
    [ReportsItsErrors]
    public async Task<bool> SaveAsync(bool connect)
    {
        if (IsBusy)
            return false;

        // Another connection may have taken the name meanwhile.
        _store = _context.Service.Load();
        ValidateEverything();
        if (Problem is { } problem)
        {
            // On its tab, with the keyboard in the field to put right.
            if (ErrorOf(nameof(OwnThresholds)) is not null)
            {
                SelectTab(ConnectionTab.Thresholds);
                Thresholds.TryRead(out _, out _, focus: true);
            }
            else if (ErrorOf(nameof(Name)) is not null)
            {
                SelectTab(ConnectionTab.Connection);
                FocusRequested?.Invoke(nameof(Name));
            }
            Status.Show(problem, success: false);
            return false;
        }

        var settings = BuildSettings();
        settings.Id = ConnectionId ?? Guid.NewGuid();
        if (OwnThresholds && Thresholds.TryRead(out var thresholds, out _, focus: false))
            settings.HealthThresholds = thresholds;

        var store = _store;
        var draft = Draft();
        IsBusy = true;
        try
        {
            if (IsNew || draft.NeedsServerCheck(_saved))
            {
                Status.Show(ConnectingMessage(settings, "Validating connection…"), success: null);
                var failure = await _context.TestConnection(settings.ConnectionString);
                if (failure is not null)
                {
                    Status.Show($"Connection failed — not saved: {failure}", success: false);
                    return false;
                }
            }

            // Only when it was changed here: a mute from the tray menu meanwhile stays.
            _context.Service.Upsert(settings, notifications: draft.Notifies != _saved.Notifies ? draft.Notifies : null);

            // The first connection saved becomes active, so the dashboard has something to show.
            var makeActive = connect || store.ActiveConnectionId is null;
            if (makeActive)
                _context.Service.SetActive(settings.Id, ActivitySources.Settings);

            ConnectionId = settings.Id;
            _store = _context.Service.Load();
            _saved = draft;
            ApplyThresholdsMode();
            RenderHeader();
            UpdatePreview();
            UpdateBar();
            Status.Show(makeActive
                ? $"Saved \"{settings.DisplayName}\", and it's now the active connection."
                : $"Saved \"{settings.DisplayName}\". Switch to it from the sidebar selector.",
                success: true);

            // Not busy any more: making it active opens its Overview, which asks this page whether it can be left.
            IsBusy = false;
            await _context.ApplyConnections(connect);
            ConnectionSaved?.Invoke(settings.Id);
            return true;
        }
        catch (Exception ex)
        {
            Status.Show($"Failed to save: {ex.Message}", success: false);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Whether the page can be left. With unsaved changes it asks: Yes saves them (and stays
    /// when that fails), No discards them, Cancel stays.
    /// </summary>
    [ReportsItsErrors]
    public async Task<bool> TryCloseAsync()
    {
        if (IsBusy)
            return false;
        if (!IsDirty)
            return true;

        var name = IsNew ? "the new connection" : $"\"{Title}\"";
        switch (_context.Dialogs.AskToSave(
                    $"Save the changes to {name}?\n\nYes saves them, No discards them, Cancel stays on this page.", "Unsaved changes"))
        {
            case DialogAnswer.Yes:
                return await SaveAsync(connect: false);
            case DialogAnswer.No:
                Discard();
                return true;
            default:
                return false;
        }
    }

    /// <summary>"← Connections": asks about what's unsaved first.</summary>
    public IAsyncRelayCommand BackCommand { get; }

    private async Task BackAsync()
    {
        if (await TryCloseAsync())
            Closed?.Invoke();
    }

    // The leave prompt runs as the app leaves Settings for the Overview.
    [RelayCommand]
    private void ViewOnHome()
    {
        if (ConnectionId is { } id)
            _context.OpenServer(id);
    }

    // ── Test and remove ───────────────────────────────────────────────────────

    /// <summary>Signs in with what's typed, saved or not.</summary>
    public IAsyncRelayCommand TestCommand { get; }

    [ReportsItsErrors]
    private async Task TestAsync()
    {
        if (FirstError(ConnectionFields) is { } error)
        {
            Status.Show(error, success: false);
            return;
        }

        var settings = BuildSettings();
        Status.Show(ConnectingMessage(settings, "Testing connection…"), success: null);
        var failure = await _context.TestConnection(settings.ConnectionString);
        Status.Show(failure is null ? "Connection successful!" : $"Connection failed: {failure}", success: failure is null);
    }

    [RelayCommand(CanExecute = nameof(CanOpenMenu))]
    private void More() => MenuRequested?.Invoke();

    private bool CanOpenMenu => !IsBusy;

    /// <summary>Remove connection… in ⋯.</summary>
    public IAsyncRelayCommand RemoveCommand { get; }

    /// <summary>Asks, then removes the connection. True when it was removed.</summary>
    [ReportsItsErrors]
    public async Task<bool> RemoveAsync()
    {
        var conn = _store.Connections.FirstOrDefault(c => c.Id == ConnectionId);
        if (conn is null)
            return false;

        if (!_context.Dialogs.Confirm($"Remove the connection \"{conn.DisplayName}\" ({conn.Server})?\n\nThis cannot be undone.", "Remove Connection"))
            return false;

        var wasActive = _store.ActiveConnectionId == conn.Id;
        try
        {
            _context.Service.Remove(conn.Id);
            await _context.ApplyConnections(false);
        }
        catch (Exception ex)
        {
            Status.Show($"Failed to remove the connection: {ex.Message}", success: false);
            return false;
        }

        var left = _context.Service.Load().Connections.Count;
        Removed?.Invoke(!wasActive
            ? $"Removed \"{conn.DisplayName}\"."
            : left > 0
                ? $"Removed \"{conn.DisplayName}\", which was the active connection. Choose another in the sidebar selector, or add a new one."
                : $"Removed \"{conn.DisplayName}\". No connections are left: add a new one to continue.");
        return true;
    }

    private static string ConnectingMessage(ConnectionSettings settings, string message) =>
        settings.Authentication == SqlAuthMode.EntraMfa
            ? message + " Complete the Microsoft sign-in in the window that opens."
            : message;
}
