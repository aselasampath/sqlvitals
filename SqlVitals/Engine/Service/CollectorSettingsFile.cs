using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Engine.Service;

/// <summary>Encrypts the collector's secrets (#172): SQL passwords and chat webhook and ping URLs.</summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    /// <summary>Throws <see cref="CryptographicException"/> when it can't be decrypted here.</summary>
    string Unprotect(string protectedText);
}

/// <summary>
/// DPAPI with the <b>LocalMachine</b> scope (#172): the service's account, the admins and the app
/// handing a server over (#172 part 2) are different Windows users, and all of them must read the
/// same file. Any process on this PC could decrypt it, so the folder's permissions are what keep
/// other users out (install sets them); the encryption keeps the secrets useless on a copy of the
/// file taken off the PC: a backup, a share, a support zip.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MachineSecretProtector : ISecretProtector
{
    // Ties the blobs to SqlVitals: another program's LocalMachine blob doesn't decrypt as one of
    // ours, and ours doesn't as theirs.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SqlVitals.Collector.v1");

    public string Protect(string plainText) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.LocalMachine));

    public string Unprotect(string protectedText) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.LocalMachine));
}

/// <summary>A settings file that couldn't be read at all: its path and why, never its contents.</summary>
public sealed class CollectorSettingsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What <see cref="CollectorSettingsFile.Load"/> read, and what in it couldn't be used.</summary>
/// <param name="Problems">One line per server or channel that lost a secret: say them, they need setting again. Never a secret.</param>
public sealed record CollectorSettingsRead(CollectorSettings Settings, IReadOnlyList<string> Problems);

/// <summary>
/// collector.json (#172): readable JSON, so an admin can see what the service watches and change
/// its sampling by hand, with every secret replaced by its protected form, <c>"dpapi:…"</c>.
/// A secret that isn't protected (typed into the file by hand) is refused, not used: it would
/// sit on disk in clear text. The service only reads it; the command line and the app write it.
/// </summary>
public static class CollectorSettingsFile
{
    /// <summary>What starts a protected secret in the file.</summary>
    public const string ProtectedPrefix = "dpapi:";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented          = true,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters             = { new JsonStringEnumConverter() },
        ReadCommentHandling    = JsonCommentHandling.Skip,
        AllowTrailingCommas    = true,
        // Never shown in a web page: a protected secret's "+" stays "+", not "+".
        Encoder                = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Reads the file; empty settings when there is none. Secrets are decrypted. A server whose
    /// password can't be decrypted on this PC is kept without it (<see cref="CollectorServer.PasswordUnreadable"/>),
    /// and one whose extra parameters aren't a connection string is marked <see cref="CollectorServer.Unusable"/>;
    /// a channel whose URL can't be is set aside (<see cref="CollectorSettings.UnreadableChannels"/>).
    /// Either way <see cref="CollectorSettingsRead.Problems"/> says so, and <see cref="Save"/> writes
    /// them back as they were. Throws <see cref="CollectorSettingsException"/> for a file that can't
    /// be read at all, or two servers or channels with one id (a block copied by hand).
    /// </summary>
    public static CollectorSettingsRead Load(string path, ISecretProtector protector)
    {
        if (!File.Exists(path))
            return new CollectorSettingsRead(new CollectorSettings(), []);

        CollectorSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<CollectorSettings>(ReadWithRetry(path), Json)
                       ?? throw new JsonException("The file is empty.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // JsonException's message quotes the line and position, never the value.
            throw new CollectorSettingsException($"The collector settings {path} couldn't be read: {ex.Message}", ex);
        }

        settings.Normalize();
        if (settings.Servers.GroupBy(s => s.Id).FirstOrDefault(g => g.Count() > 1) is { } servers)
            throw new CollectorSettingsException(
                $"The collector settings {path} have {servers.Count()} servers with the id {servers.Key}: " +
                "give each its own, or delete the copy's \"id\" line and one is made for it.");
        if (settings.ChatChannels.GroupBy(c => c.Id).FirstOrDefault(g => g.Count() > 1) is { } channels)
            throw new CollectorSettingsException(
                $"The collector settings {path} have {channels.Count()} chat channels with the id {channels.Key}: " +
                "give each its own, or delete the copy's \"id\" line and one is made for it.");

        var problems = new List<string>();

        foreach (var server in settings.Servers)
        {
            if (server.Authentication != CollectorAuth.SqlLogin || server.Password.Length == 0)
            {
                server.Password = string.Empty;
                continue;
            }
            if (TryUnprotect(server.Password, protector, out var password))
            {
                server.Password = password;
            }
            else
            {
                server.KeptPassword       = server.Password;
                server.Password           = string.Empty;
                server.PasswordUnreadable = true;
                problems.Add($"{server.DisplayName}: the saved password can't be used on this PC, so it isn't watched. " +
                             $"Set it again: SqlVitals.Collector set-password \"{server.DisplayName}\"");
            }
        }

        foreach (var server in settings.Servers)
        {
            try
            {
                _ = server.BuildConnectionString();
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
            {
                // The builder's message names the keyword, never its value.
                server.Unusable = $"its additionalParameters aren't a connection string ({ex.Message})";
                problems.Add($"{server.DisplayName}: {server.Unusable}, so it isn't watched. Fix them in {path}.");
            }
        }

        var readable = new List<ChatChannel>();
        foreach (var channel in settings.ChatChannels)
        {
            var ping = string.Empty;
            if (!TryUnprotect(channel.WebhookUrl, protector, out var url) ||
                (channel.HeartbeatPingUrl.Length > 0 && !TryUnprotect(channel.HeartbeatPingUrl, protector, out ping)))
            {
                problems.Add($"Chat \"{channel.Name}\": its webhook or ping URL can't be used on this PC, so nothing is posted to it. " +
                             $"Add it again: SqlVitals.Collector remove-channel \"{channel.Name}\", then add-channel");
                settings.UnreadableChannels.Add(channel);
                continue;
            }
            channel.WebhookUrl       = url;
            channel.HeartbeatPingUrl = ping;
            readable.Add(channel);
        }
        settings.ChatChannels = readable;

        return new CollectorSettingsRead(settings, problems);
    }

    /// <summary>
    /// Writes the settings with every secret protected, beside the file and swapped in, so a
    /// crash mid-write can't leave half a file for the service to read.
    /// </summary>
    public static void Save(string path, CollectorSettings settings, ISecretProtector protector)
    {
        var copy = new CollectorSettings
        {
            Version                = settings.Version,
            SampleSeconds          = settings.SampleSeconds,
            CommandTimeoutSeconds  = settings.CommandTimeoutSeconds,
            HistoryIntervalMinutes = settings.HistoryIntervalMinutes,
            HistoryRetentionDays   = settings.HistoryRetentionDays,
            AlertSamples           = settings.AlertSamples,
            HealthThresholds       = settings.HealthThresholds,
            ShareCopyMinutes       = settings.ShareCopyMinutes,
            ShareCopyFolder        = settings.ShareCopyFolder,
            Servers                = settings.Servers.Select(s =>
            {
                var server = s.Clone();
                server.Password = server.Authentication != CollectorAuth.SqlLogin ? string.Empty
                    : server.Password.Length > 0 ? ProtectedPrefix + protector.Protect(server.Password)
                    : server.KeptPassword ?? string.Empty;
                return server;
            }).ToList(),
            ChatChannels           = settings.ChatChannels.Select(c =>
            {
                var channel = c.Clone();
                channel.WebhookUrl       = ProtectedPrefix + protector.Protect(channel.WebhookUrl.Trim());
                channel.HeartbeatPingUrl = string.IsNullOrWhiteSpace(channel.HeartbeatPingUrl)
                    ? string.Empty
                    : ProtectedPrefix + protector.Protect(channel.HeartbeatPingUrl.Trim());
                return channel;
            }).Concat(settings.UnreadableChannels).ToList(),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(copy, Json));
        File.Move(temp, path, overwrite: true);
    }

    // Only the protected form counts: a clear-text secret in the file is refused.
    private static bool TryUnprotect(string value, ISecretProtector protector, out string plain)
    {
        plain = string.Empty;
        if (!value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            return false;
        try
        {
            plain = protector.Unprotect(value[ProtectedPrefix.Length..]);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    // The command line or the app may be swapping the file in just now.
    private static string ReadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }
}
