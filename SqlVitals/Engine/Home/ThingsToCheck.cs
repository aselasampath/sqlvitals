namespace SqlVitals.Engine.Home;

/// <summary>
/// What to check when SqlVitals can't reach a server (#130), for its name as saved: the host and
/// port it connects to, a named instance's SQL Browser, an Azure SQL firewall, a failed login.
/// </summary>
public static class ThingsToCheck
{
    public const string DefaultPort = "1433";

    /// <param name="server">The server name as saved: "host", "host\instance", "host,port", "tcp:host,port".</param>
    /// <param name="error">The last error, to add the checks it points at; may be null.</param>
    public static IReadOnlyList<string> For(string server, string? error)
    {
        var (host, instance, port) = Parse(server);
        var azure = host.EndsWith(".database.windows.net", StringComparison.OrdinalIgnoreCase);
        var items = new List<string>();

        if (error?.Contains("Login failed", StringComparison.OrdinalIgnoreCase) == true)
            items.Add("The login and password are still right, and the login isn't disabled or locked out.");

        if (azure)
            items.Add($"The firewall of the Azure SQL server {host} allows this PC's public IP address.");
        else
            items.Add($"The SQL Server service on {host} is running.");

        items.Add(instance is not null && port is null
            ? $"This PC can reach {host} (firewall, VPN), on the instance's port and on UDP 1434 for the SQL Server Browser."
            : $"This PC can reach {host} on port {port ?? DefaultPort} (firewall, VPN).");

        if (instance is not null && port is null)
            items.Add($"The SQL Server Browser service is running on {host}, so the {instance} instance's port can be found.");

        items.Add("The server name in the connection is still right.");
        return items;
    }

    /// <summary>The host, the instance name if any, and the port if one is given.</summary>
    public static (string Host, string? Instance, string? Port) Parse(string server)
    {
        var name = server.Trim();
        foreach (var prefix in new[] { "tcp:", "np:", "lpc:", "admin:" })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                name = name[prefix.Length..];

        string? port = null;
        var comma = name.LastIndexOf(',');
        if (comma >= 0)
        {
            port = name[(comma + 1)..].Trim();
            name = name[..comma];
            if (port.Length == 0)
                port = null;
        }

        string? instance = null;
        var slash = name.IndexOf('\\');
        if (slash >= 0)
        {
            instance = name[(slash + 1)..].Trim();
            name     = name[..slash];
            if (instance.Length == 0)
                instance = null;
        }

        var host = name.Trim();
        return (host is "." or "(local)" ? "localhost" : host, instance, port);
    }
}
