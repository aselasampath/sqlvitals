using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Engine.Models;

namespace SqlVitals.Desktop.ViewModels.Windows;

/// <summary>
/// One plan in a Query Store query's window (#191): its figures, and its own View Plan, so only
/// the button pressed is busy while its plan is read and the other plans can still be opened.
/// </summary>
/// <param name="open">Reads the plan and opens it in the plan window, saying why when it can't.</param>
public sealed partial class QueryStorePlanRow(QueryStorePlanSummary plan, Func<long, Task> open) : ObservableObject
{
    public QueryStorePlanSummary Plan => plan;

    public long      PlanId                 => plan.PlanId;
    public DateTime? FirstUsedLocal         => plan.FirstUsedLocal;
    public DateTime? LastUsedLocal          => plan.LastUsedLocal;
    public long      Executions             => plan.Executions;
    public double?   AvgDurationMs          => plan.AvgDurationMs;
    public double?   AvgCpuMs               => plan.AvgCpuMs;
    public double?   AvgReads               => plan.AvgReads;
    public long      Failed                 => plan.Failed;
    public bool      IsForced               => plan.IsForced;
    public string    ForcedText             => plan.ForcedText;
    public long      ForceFailures          => plan.ForceFailures;
    public string?   LastForceFailureReason => plan.LastForceFailureReason;

    /// <summary>View Plan: the plan drawn, as SSMS does; greyed, with "…", while it is read.</summary>
    [RelayCommand]
    private Task ViewPlan() => open(plan.PlanId);
}
