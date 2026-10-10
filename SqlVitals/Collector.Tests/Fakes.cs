using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Service;

namespace SqlVitals.Collector.Tests;

/// <summary>Reverses the text: readable in a test, and not the text itself.</summary>
internal sealed class FakeProtector : ISecretProtector
{
    public string Protect(string plainText) => "fake:" + new string(plainText.Reverse().ToArray());

    public string Unprotect(string protectedText) =>
        protectedText.StartsWith("fake:", StringComparison.Ordinal)
            ? new string(protectedText[5..].Reverse().ToArray())
            : throw new CryptographicException("Not protected on this PC.");
}

/// <summary>A server's live metrics from a function; a history detail that fails, which the recorder only reports.</summary>
internal sealed class FakeServer(Func<LiveMetricSnapshot> answer) : ILiveMetricsRepository, IHistoryRepository
{
    public int Reads;

    public static LiveMetricSnapshot Snapshot(double cpu = 12) =>
        new(DateTime.UtcNow, 100, 0, 0, 10, 0, 0, SqlCpuUtilizationPct: cpu, PageLifeExpectancy: 5_000,
            MemoryGrantsPending: 0, BufferCacheHitRatio: 99, 0, 0, 0, 0, 0, 0, 0);

    public Task<LiveMetricSnapshot> GetLiveMetricsAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Reads);
        return Task.FromResult(answer());
    }

    public Task<IEnumerable<PerfmonCounter>> GetPerfmonCountersAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The collector reads live metrics only.");

    public Task<HistoryDetailSnapshot> GetHistoryDetailAsync(DateTime? queriesExecutedSince, int maxQueries, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No history detail in these tests.");

    public Task<IReadOnlyList<QueryTextInfo>> GetQueryTextsAsync(IReadOnlyCollection<string> queryHashes, int maxLength, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<QueryTextInfo>>([]);
}

/// <summary>Records every post; each one works.</summary>
internal sealed class FakeSender : IWebhookSender
{
    public readonly ConcurrentQueue<(Uri Url, string Json, ChatService Service)> Sent = new();

    public Task<WebhookResult> SendAsync(Uri url, string json, ChatService service, CancellationToken cancel = default)
    {
        Sent.Enqueue((url, json, service));
        return Task.FromResult(new WebhookResult(WebhookOutcome.Posted, 200, null, TimeSpan.FromMilliseconds(5)));
    }

    public Task<WebhookResult> PingAsync(Uri url, CancellationToken cancel = default) =>
        Task.FromResult(new WebhookResult(WebhookOutcome.Posted, 200, null, TimeSpan.FromMilliseconds(5)));

    /// <summary>The title and text of every Slack post, in order.</summary>
    public IReadOnlyList<string> Texts => Sent.Select(s =>
    {
        var blocks = JsonNode.Parse(s.Json)!["blocks"]!.AsArray();
        return string.Join(" | ", blocks.Select(b => (string?)b!["text"]?["text"]).OfType<string>());
    }).ToList();
}

/// <summary>Every log line, with its level.</summary>
internal sealed class ListLogger : ILogger
{
    public readonly ConcurrentQueue<(LogLevel Level, string Text)> Lines = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue((logLevel, formatter(state, exception)));
}

/// <summary>A folder of its own under the temp folder, deleted after the test.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SqlVitals.Collector.Tests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* a SQLite file still closing: the temp folder is cleaned up eventually */ }
    }
}
