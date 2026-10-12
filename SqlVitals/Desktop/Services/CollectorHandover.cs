using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Service;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Hands a saved connection to the collector service and takes it back (#172), as changes to the
/// service's settings. The server keeps the connection's id, so the app and the service record
/// its history under one id, and the app reads both as one.
///
/// Its chat channels go with it: each of the app's channels that posts for it is given to the
/// service, with its choices and heartbeat, so its alerts keep reaching the same channels.
/// </summary>
public static class CollectorHandover
{
    /// <summary>Why a connection can't be handed over; null when it can.</summary>
    public static string? WhyNot(ConnectionSettings connection) => connection switch
    {
        { Authentication: SqlAuthMode.EntraMfa } =>
            "It signs in with Entra MFA, and the service has nobody to sign in. Use Windows authentication or a SQL login for it.",
        { NeedsPassword: true } =>
            "Its password isn't saved or typed in this run: connect to it once, so SqlVitals has it to give to the service.",
        { Authentication: SqlAuthMode.SqlServer, SavePassword: false } =>
            "Remember password is off for it, and the service would have to save the password. Turn Remember password on, or use Windows authentication.",
        { IsConfigured: false } => "It has no server.",
        _ => null,
    };

    /// <summary>
    /// The service's settings with the connection added (or replaced) and its channels. Throws
    /// <see cref="InvalidOperationException"/> with <see cref="WhyNot"/> when it can't be.
    /// </summary>
    public static CollectorSettings HandOver(CollectorSettings service, ConnectionStore app, ConnectionSettings connection)
    {
        if (WhyNot(connection) is { } why)
            throw new InvalidOperationException($"{connection.DisplayName} can't be handed to the collector service: {why}");

        var server = new CollectorServer
        {
            Id                     = connection.Id,
            Name                   = connection.DisplayName,
            Server                 = connection.Server.Trim(),
            Database               = connection.Database.Trim(),
            Authentication         = connection.Authentication == SqlAuthMode.Windows ? CollectorAuth.Windows : CollectorAuth.SqlLogin,
            UserName               = connection.Authentication == SqlAuthMode.Windows ? string.Empty : connection.UserName.Trim(),
            Password               = connection.Authentication == SqlAuthMode.Windows ? string.Empty : connection.Password,
            Encrypt                = connection.Encrypt switch
            {
                EncryptMode.Optional => CollectorEncrypt.Optional,
                EncryptMode.Strict   => CollectorEncrypt.Strict,
                _                    => CollectorEncrypt.Mandatory,
            },
            TrustServerCertificate = connection.TrustServerCertificate,
            ConnectTimeoutSeconds  = connection.ConnectTimeoutSeconds,
            AdditionalParameters   = connection.AdditionalParameters.Trim(),
            HealthThresholds       = app.ThresholdsFor(connection),
        };
        service.Servers.RemoveAll(s => s.Id == server.Id);
        service.Servers.Add(server);
        // The service has one RPO for every server, as the app does (#174): the app's.
        service.BackupRpo = app.BackupRpo;
        // And keeps the query text as the app does (#175).
        service.QueryText = app.QueryTextStorage;

        foreach (var channel in app.ChatChannels.Where(c => c.Enabled && c.Includes(connection.Id)))
        {
            if (service.ChatChannels.FirstOrDefault(c => c.Id == channel.Id) is { } given)
            {
                if (!given.AllConnections && !given.ConnectionIds.Contains(connection.Id))
                    given.ConnectionIds.Add(connection.Id);
                continue;
            }
            // Only this server: the app's "all connections" is the app's, not the service's.
            var copy = channel.Clone();
            copy.AllConnections = false;
            copy.ConnectionIds  = [connection.Id];
            service.ChatChannels.Add(copy);
        }
        return service;
    }

    /// <summary>
    /// The service's settings without the connection; a channel left posting for no server is
    /// removed with it. True when the service had it.
    /// </summary>
    public static bool TakeBack(CollectorSettings service, Guid connectionId)
    {
        if (service.Servers.RemoveAll(s => s.Id == connectionId) == 0)
            return false;
        foreach (var channel in service.ChatChannels.ToList())
        {
            channel.ConnectionIds.Remove(connectionId);
            if (!channel.AllConnections && channel.ConnectionIds.Count == 0)
                service.ChatChannels.Remove(channel);
        }
        return true;
    }
}
