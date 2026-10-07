using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Media;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Controls;

/// <summary>The process states as the map draws them and the grid names them (#116).</summary>
internal static class ProcessStates
{
    public static readonly string[] All = [BlockingChains.HeadBlocker, BlockingChains.Blocked, BlockingChains.Active, BlockingChains.Sleeping];

    /// <summary>The map's ball outline for the state; the grid's dot next to its name.</summary>
    public static Color ColorOf(string state) => state switch
    {
        BlockingChains.Active      => Color.FromRgb(0xFF, 0x40, 0x40),
        BlockingChains.HeadBlocker => Color.FromRgb(0xFF, 0x80, 0x00),
        BlockingChains.Blocked     => Color.FromRgb(0x60, 0x60, 0xFF),
        _                          => Color.FromRgb(0x44, 0x88, 0xFF),
    };

    public static string Label(string state) => state switch
    {
        BlockingChains.HeadBlocker => "Head blocker",
        BlockingChains.Blocked     => "Blocked",
        BlockingChains.Active      => "Active",
        _                          => "Sleeping",
    };

    /// <summary>Sorting the State column puts problems first.</summary>
    public static int Rank(string state) => state switch
    {
        BlockingChains.HeadBlocker => 0,
        BlockingChains.Blocked     => 1,
        BlockingChains.Active      => 2,
        _                          => 3,
    };

    private static readonly Dictionary<string, Brush> Brushes = All.ToDictionary(s => s, s =>
    {
        var brush = new SolidColorBrush(ColorOf(s));
        brush.Freeze();
        return (Brush)brush;
    });

    public static Brush BrushOf(string state) => Brushes.TryGetValue(state, out var b) ? b : Brushes[BlockingChains.Sleeping];
}

/// <summary>One session in the Processes grid (#116), from the same snapshot the map draws.</summary>
internal sealed class ProcessGridRow
{
    // The whole text in a tooltip; a batch of several MB would make one the size of the screen.
    internal const int MaxTooltipChars = 4_000;
    internal const int MaxFirstLineChars = 300;

    public ProcessGridRow(ProcessNode node, ProcessGroupHeader group, int head, int blocking, bool blockerVisible)
    {
        Node            = node;
        Group           = group;
        Blocking        = blocking > 0 ? blocking : null;
        // Only a session in a chain has a head: one blocked, or one blocking others (it is its own head).
        HeadOfChain     = head != node.SessionId || blocking > 0 ? head : null;
        BlockedByIsLink = node.ParentId > 0 && blockerVisible;
        QueryFirstLine  = FirstLine(node.CommandText);
        QueryTooltip    = string.IsNullOrWhiteSpace(node.CommandText) ? null
                        : node.CommandText.Length > MaxTooltipChars ? node.CommandText[..MaxTooltipChars] + "…"
                        : node.CommandText;
    }

    public ProcessNode Node { get; }
    public ProcessGroupHeader Group { get; }

    public int     SessionId   => Node.SessionId;
    /// <summary>The login, or "(no login)" as its group header says.</summary>
    public string  Login       => string.IsNullOrWhiteSpace(Node.UserName) ? ProcessGroups.NoLogin : Node.UserName;
    public string  State       => ProcessStates.Label(Node.VisualState);
    public int     StateRank   => ProcessStates.Rank(Node.VisualState);
    public Brush   StateBrush  => ProcessStates.BrushOf(Node.VisualState);

    /// <summary>The blocker's SPID, or SQL Server's negative code when it isn't a session.</summary>
    public int?    BlockedBy        => Node.ParentId != 0 ? Node.ParentId : null;
    public bool    BlockedByIsLink  { get; }
    public string? BlockedByTooltip => Node.ParentId != 0 ? $"Blocked by {BlockingChains.DescribeBlocker(Node.ParentId)}" : null;

    /// <summary>Sessions this one blocks directly; empty for none.</summary>
    public int?    Blocking    { get; }
    public int?    HeadOfChain { get; }
    public bool    HeadIsLink  => HeadOfChain is { } head && head != SessionId;

    public string  Status       => Node.Status;
    public string? Host         => Node.HostName;
    public string? Program      => Node.ProgramName;
    public string? Database     => Node.DatabaseName;
    public string? Command      => Node.Command;
    public string? WaitType     => Node.WaitType;
    public double? WaitSec      => Node.WaitType is null && Node.WaitTimeMs == 0 ? null : Node.WaitTimeMs / 1000.0;
    public string? WaitResource => Node.WaitResource;
    public long    CpuMs        => Node.CpuTime;
    public long    PhysicalIo   => Node.PhysicalIo;
    /// <summary>8-KB pages, as memusage in Session Details.</summary>
    public long    Memory       => Node.MemoryUsage;
    public int     OpenTran     => Node.OpenTran;
    public DateTime? LoginTime  => Node.LoginTime == DateTime.MinValue ? null : Node.LoginTime;
    public DateTime? LastBatch  => Node.LastBatch == DateTime.MinValue ? null : Node.LastBatch;

    public string  QueryFirstLine { get; }
    public string? QueryTooltip   { get; }

    /// <summary>The first line of a batch that has text, trimmed; batches often start with a blank line or a comment block's first line.</summary>
    internal static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed.Length > MaxFirstLineChars ? trimmed[..MaxFirstLineChars] + "…" : trimmed;
        }
        return "";
    }
}

/// <summary>
/// A login's group in the Processes grid. Kept across refreshes under its key, so a refresh keeps
/// whether the user expanded or collapsed it (#116).
/// </summary>
internal sealed class ProcessGroupHeader(string key) : INotifyPropertyChanged
{
    public string Key { get; } = key;

    private string _name = "";
    public string Name { get => _name; private set => Set(ref _name, value); }

    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    private bool _hasHeadBlocker;
    public bool HasHeadBlocker { get => _hasHeadBlocker; private set => Set(ref _hasHeadBlocker, value); }

    private bool _hasBlocked;
    public bool HasBlocked { get => _hasBlocked; private set => Set(ref _hasBlocked, value); }

    // Groups start expanded.
    private bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    /// <summary>Where the group sits: problems first (see <see cref="ProcessGroups.By"/>).</summary>
    public int Order { get; set; } = int.MaxValue;

    public void Update(ProcessGroup group)
    {
        Name           = group.Name;
        Summary        = group.Summary(CultureInfo.CurrentCulture);
        HasHeadBlocker = group.HeadBlockers > 0;
        HasBlocked     = group.Blocked > 0;
    }

    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Sorts a grid's login groups by <see cref="Order"/>.</summary>
    public sealed class GroupOrder : IComparer
    {
        public static readonly GroupOrder Instance = new();

        public int Compare(object? x, object? y) => OrderOf(x).CompareTo(OrderOf(y));

        private static int OrderOf(object? group) =>
            group is CollectionViewGroup { Name: ProcessGroupHeader header } ? header.Order : int.MaxValue;
    }
}
