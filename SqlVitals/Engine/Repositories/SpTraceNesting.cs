using System.Globalization;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// What called each captured call, and how deep it ran (#238). Live Calls keeps a nested procedure
/// as its own row; this says which row called it, so its time isn't counted twice.
/// <para>
/// The session runs with TRACK_CAUSALITY = ON, so every event carries <c>attach_activity_id</c>,
/// <c>GUID-sequence</c>: one GUID for every event of a request, and a sequence that counts up as
/// they fire. A procedure ends before the one that called it, so within a request each call is
/// the parent of the calls that ended before it, since its last child, and began after it began.
/// </para>
/// <para>Pure and static, so it is unit-testable without a server; worked out again over every
/// call kept, as a caller's event comes in after its children's.</para>
/// </summary>
public static class SpTraceNesting
{
    /// <summary>
    /// XE timestamps are in whole milliseconds, so a call that began as its caller did can read as
    /// beginning up to a millisecond before it.
    /// </summary>
    internal static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(1);

    /// <summary>The caller and level of each event, in the order given.</summary>
    public static IReadOnlyList<CallNesting> Resolve(IReadOnlyList<SpTraceEvent> events)
    {
        var nesting  = new CallNesting[events.Count];
        var requests = new Dictionary<string, List<Call>>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            var isRpc = IsRpc(ev);
            if (!isRpc && !IsModule(ev))
                continue;

            // No activity id: nothing ties it to its request. An RPC is the client's all the same.
            if (ReadActivity(ev.ActivityId) is not var (request, sequence))
            {
                nesting[i] = isRpc ? new(CallNesting.Client, 1) : default;
                continue;
            }

            if (!requests.TryGetValue(request, out var calls))
                requests[request] = calls = [];
            calls.Add(new Call(i, ev, sequence, isRpc));
        }

        foreach (var calls in requests.Values)
            Resolve(calls, nesting);
        return nesting;
    }

    // One request's calls: each one ends after the calls it made, so in the order they ended, a
    // call is the parent of the latest calls without one that began after it began.
    private static void Resolve(List<Call> calls, CallNesting[] nesting)
    {
        calls.Sort(calls.TrueForAll(c => c.Sequence is not null) ? BySequence : ByTime);

        var unparented = new List<Call>();
        foreach (var call in calls)
        {
            while (unparented.Count > 0 && Inside(unparented[^1], call))
            {
                unparented[^1].Parent = call;
                call.Children.Add(unparented[^1]);
                unparented.RemoveAt(unparented.Count - 1);
            }
            unparented.Add(call);
        }

        // The rpc_completed row and the module_end of the procedure it ran are one call: the
        // longest module_end of that name it holds (a recursive call inside is the shorter one),
        // or the longest it holds when the rpc_completed row named no procedure.
        foreach (var rpc in calls.Where(c => c.IsRpc))
        {
            var own = rpc.Children.Where(c => !c.IsRpc && (rpc.Event.ObjectName is null || SameProcedure(c.Event.ObjectName, rpc.Event.ObjectName)))
                                  .MaxBy(c => c.Event.EventTime - c.Event.StartTime);
            own?.SameCallAs = rpc;
        }

        foreach (var call in calls)
            nesting[call.Index] = new(CalledBy(call), Level(call), RepeatsRpc: call.SameCallAs is not null);
    }

    // Began after the caller began, give or take the timestamps' millisecond, and ended after it
    // began. The timestamps are whole milliseconds, so a call under a millisecond that ended just
    // before the next began can still read as inside it: reading it as nested keeps it out of
    // Top-level only, where reading a real child as its caller's sibling would count it twice.
    // An rpc_completed row is never inside another call: the client made it. sp_reset_connection
    // comes in the same request, just before the call it resets for, and would otherwise read as
    // called by it (seen on SQL Server 2019-2025).
    private static bool Inside(Call call, Call caller) =>
        !call.IsRpc
        && call.Event.StartTime >= caller.Event.StartTime - Slack && call.Event.EventTime >= caller.Event.StartTime;

    private static string? CalledBy(Call call)
    {
        if (call.SameCallAs is { } rpc)
            return CalledBy(rpc);
        if (call.Parent is not { } parent)
            // An RPC is the client's. A module_end with no caller may be a batch's (the session
            // doesn't capture batches) or its caller hasn't ended yet: not known.
            return call.IsRpc ? CallNesting.Client : null;
        // An rpc_completed row that named no procedure: its own module_end may have.
        return parent.Event.ObjectName ?? parent.Children.FirstOrDefault(c => c.SameCallAs == parent)?.Event.ObjectName;
    }

    // The callers captured above it, plus one: a call whose caller isn't known counts as the first.
    private static int Level(Call call) =>
        call.SameCallAs is { } rpc ? Level(rpc)
        : call.Parent is { } parent ? Level(parent) + 1
        : 1;

    // In the order they ended: by sequence where every activity id has one, which also orders
    // calls that ended in the same millisecond; otherwise by time, the inner (shorter) first.
    private static int BySequence(Call a, Call b) => a.Sequence!.Value.CompareTo(b.Sequence!.Value);

    private static int ByTime(Call a, Call b)
    {
        var byEnd = a.Event.EventTime.CompareTo(b.Event.EventTime);
        if (byEnd != 0)
            return byEnd;
        var byStart = b.Event.StartTime.CompareTo(a.Event.StartTime);
        return byStart != 0 ? byStart : a.IsRpc.CompareTo(b.IsRpc);
    }

    private static bool IsRpc(SpTraceEvent ev) => ev.EventName.Equals("rpc_completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsModule(SpTraceEvent ev) => ev.EventName.Equals("module_end", StringComparison.OrdinalIgnoreCase);

    // dbo.usp_A, usp_A and [dbo].[usp_A] are one procedure: rpc_completed and module_end name it differently.
    private static bool SameProcedure(string? a, string? b) =>
        a is not null && b is not null && string.Equals(LastPart(a), LastPart(b), StringComparison.OrdinalIgnoreCase);

    private static string LastPart(string name) => name[(name.LastIndexOf('.') + 1)..].Trim('[', ']', '"', ' ');

    /// <summary>
    /// The request's GUID and the event's sequence from <c>attach_activity_id</c>: <c>GUID-seq</c>
    /// as the ring buffer writes it, or <c>GUID:seq</c>. A bare GUID has no sequence. Null when
    /// there is no activity id.
    /// </summary>
    public static (string Request, long? Sequence)? ReadActivity(string? activityId)
    {
        if (string.IsNullOrWhiteSpace(activityId))
            return null;
        var id = activityId.Trim();
        if (Guid.TryParse(id, out _))
            return (id.Trim('{', '}'), null);

        var at = Math.Max(id.LastIndexOf(':'), id.LastIndexOf('-'));
        return at > 0 && long.TryParse(id.AsSpan(at + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            ? (id[..at].Trim('{', '}'), sequence)
            : (id.Trim('{', '}'), null);
    }

    // A call of the request, and what the walk found for it.
    private sealed class Call(int index, SpTraceEvent ev, long? sequence, bool isRpc)
    {
        public int Index { get; } = index;
        public SpTraceEvent Event { get; } = ev;
        public long? Sequence { get; } = sequence;
        public bool IsRpc { get; } = isRpc;

        /// <summary>The call that made it.</summary>
        public Call? Parent { get; set; }

        /// <summary>The calls it made.</summary>
        public List<Call> Children { get; } = [];

        /// <summary>A module_end's rpc_completed: the same call, read twice.</summary>
        public Call? SameCallAs { get; set; }
    }
}
