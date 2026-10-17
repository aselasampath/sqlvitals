using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Settings;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// A connection's card in Settings → Connections (#131): its card, a Test result in place of its
/// state for a while, and its buttons' commands. The view colours it from <see cref="Card"/>'s status
/// and role and <see cref="StateStatus"/> (#190).
/// </summary>
public sealed class ConnectionCardItem : ObservableObject
{
    /// <summary>How long a Test result stays in place of the state; the next sample after puts the state back.</summary>
    public static readonly TimeSpan TestShownFor = TimeSpan.FromSeconds(30);

    private string?  _testText;
    private bool?    _testSucceeded;
    private bool     _testing;
    private DateTime _testShownUntil;

    /// <param name="checkPermissions">Opens its Check permissions (#177); null hides the button.</param>
    public ConnectionCardItem(ConnectionCard card, ICommand? edit = null, ICommand? test = null, ICommand? checkPermissions = null)
    {
        Card = card;
        EditCommand = edit;
        TestCommand = test;
        CheckPermissionsCommand = checkPermissions;
    }

    public ConnectionCard Card { get; private set; }

    public ICommand? EditCommand { get; }

    public ICommand? TestCommand { get; }

    public ICommand? CheckPermissionsCommand { get; }

    public bool CanCheckPermissions => CheckPermissionsCommand is not null;

    public string CheckPermissionsName => $"Check the permissions of {Card.Name}";

    private string? TestResult => _testing || DateTime.UtcNow < _testShownUntil ? _testText : null;

    /// <summary>The state, or the result of Test while there is one.</summary>
    public string StateText => TestResult ?? Card.StateText;

    public string StateToolTip => TestResult ?? Card.StateText;

    /// <summary>The state's colour: its status, or green, red or grey (still testing) for a Test result.</summary>
    public TileStatus StateStatus => TestResult is null
        ? Card.Status
        : _testSucceeded switch { true => TileStatus.Healthy, false => TileStatus.Critical, _ => TileStatus.Off };

    public bool CanTest => !_testing;

    public string TestName => $"Test {Card.Name}";
    public string EditName => $"Edit {Card.Name}";

    public void Update(ConnectionCard card)
    {
        Card = card;
        OnPropertyChanged(string.Empty);
    }

    /// <param name="success">Null while the test is running.</param>
    public void ShowTest(string text, bool? success)
    {
        _testText       = text;
        _testSucceeded  = success;
        _testing        = success is null;
        _testShownUntil = DateTime.UtcNow + TestShownFor;
        OnPropertyChanged(string.Empty);
    }
}
