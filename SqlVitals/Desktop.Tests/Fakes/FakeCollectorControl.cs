using System.Security.Cryptography;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Service;

namespace SqlVitals.Desktop.Tests.Fakes;

/// <summary>
/// The collector service as a test sets it (#172): its state, and collector.json in a temp folder,
/// with a protector that reverses the text. Not installed until told otherwise.
/// </summary>
internal sealed class FakeCollectorControl(string dataDirectory) : ICollectorControl
{
    private sealed class Reverser : ISecretProtector
    {
        public string Protect(string plainText) => "r:" + new string(plainText.Reverse().ToArray());

        public string Unprotect(string protectedText) =>
            protectedText.StartsWith("r:", StringComparison.Ordinal)
                ? new string(protectedText[2..].Reverse().ToArray())
                : throw new CryptographicException("Not this PC's.");
    }

    private readonly Reverser _protector = new();

    public string DataDirectory { get; } = dataDirectory;

    public CollectorServiceState State { get; set; } = CollectorServiceState.NotInstalled;

    /// <summary>The settings can't be read by this user, as when another installed it.</summary>
    public bool Denied { get; set; }

    public List<string> Calls { get; } = [];

    /// <summary>Start fails, as when the admin prompt is cancelled or the service won't run.</summary>
    public Exception? StartFails { get; set; }

    public Task StartAsync(CancellationToken cancel = default)
    {
        Calls.Add("start");
        if (StartFails is { } failure)
            return Task.FromException(failure);
        State = CollectorServiceState.Running;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancel = default)
    {
        Calls.Add("stop");
        State = CollectorServiceState.Stopped;
        return Task.CompletedTask;
    }

    public CollectorSettingsRead Read() =>
        Denied ? throw new UnauthorizedAccessException("Access to the path is denied.")
               : CollectorSettingsFile.Load(CollectorPaths.SettingsFile(DataDirectory), _protector);

    public void Save(CollectorSettings settings)
    {
        Calls.Add("save");
        CollectorSettingsFile.Save(CollectorPaths.SettingsFile(DataDirectory), settings, _protector);
    }

    /// <summary>collector.json as saved, for assertions.</summary>
    public CollectorSettings Saved() => Read().Settings;
}
