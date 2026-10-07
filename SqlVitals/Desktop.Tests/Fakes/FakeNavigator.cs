using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Alerts;

namespace SqlVitals.Desktop.Tests.Fakes;

/// <summary>An <see cref="INavigator"/> that writes down where it was asked to go (#182).</summary>
internal sealed class FakeNavigator : INavigator
{
    /// <summary>Each move, as "Processes", "QueryStore ⟵ Query Regressions: Query 42", "Back".</summary>
    public List<string> Moves { get; } = [];

    /// <summary>The alerts investigated, with the activity log's source.</summary>
    public List<(Alert Alert, string Source)> Investigated { get; } = [];

    public Task GoToAsync(string tag, Guid? serverId = null)
    {
        Moves.Add(serverId is { } id ? $"{tag} on {id}" : tag);
        return Task.CompletedTask;
    }

    public Task GoToAsync(string tag, FocusFilter filter, string fromPage)
    {
        Moves.Add($"{tag} ⟵ {fromPage}: {filter.Kind}{(filter.Id is { } id ? $" {id}" : "")}");
        return Task.CompletedTask;
    }

    public Task InvestigateAsync(Alert alert, string source)
    {
        Investigated.Add((alert, source));
        Moves.Add($"Investigate {alert.Id}");
        return Task.CompletedTask;
    }

    public Task BackAsync()
    {
        Moves.Add("Back");
        return Task.CompletedTask;
    }

    public Task ForwardAsync()
    {
        Moves.Add("Forward");
        return Task.CompletedTask;
    }
}
