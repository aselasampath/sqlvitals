using SqlPulse.Engine.Repositories;

namespace SqlPulse.Engine;

/// <summary>
/// Exposes the ASP.NET Core host as a reusable factory so the WPF desktop
/// shell can start it in-process on a dynamic port.
/// </summary>
public static class ApiHost
{
    /// <summary>
    /// Builds (but does not start) the web application.
    /// </summary>
    /// <param name="args">Command-line arguments passed through.</param>
    /// <param name="port">
    /// TCP port for Kestrel. Pass 0 to let the OS choose a free port.
    /// The actual bound port is resolved via <see cref="GetBoundPort"/> after
    /// <see cref="Microsoft.AspNetCore.Builder.WebApplication.StartAsync"/> completes.
    /// </param>
    /// <param name="extraConfig">Optional extra in-memory config (e.g. to override connection string).</param>
    public static WebApplication Build(
        string[] args,
        int port = 0,
        Dictionary<string, string?>? extraConfig = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Ensure static files are served from wwwroot beside the exe,
        // regardless of the current working directory.
        var exeDir = AppContext.BaseDirectory;
        builder.Environment.WebRootPath = Path.Combine(exeDir, "wwwroot");
        builder.Environment.ContentRootPath = exeDir;

        // ── Optional config overrides (e.g. connection string from beside exe) ──
        if (extraConfig is { Count: > 0 })
            builder.Configuration.AddInMemoryCollection(extraConfig);

        // ── Services ───────────────────────────────────────────────────────────
        // Explicitly register this assembly as an application part: controller
        // discovery otherwise defaults to the entry assembly, which is wrong when
        // this host is started in-process by a different app (e.g. the WPF shell).
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(ApiHost).Assembly)
            .AddJsonOptions(opt =>
                opt.JsonSerializerOptions.PropertyNamingPolicy =
                    System.Text.Json.JsonNamingPolicy.CamelCase);

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
            c.SwaggerDoc("v1", new() { Title = "Wait Stats Dashboard API", Version = "v1" }));

        builder.Services.AddResponseCompression();

        builder.Services.AddScoped<IWaitStatsRepository, WaitStatsRepository>();
        builder.Services.AddScoped<ITempDbRepository, TempDbRepository>();

        // No CORS needed — React is served from the same Kestrel origin

        // ── Kestrel: bind to the requested port ─────────────────────────────
        // Use 127.0.0.1 (not localhost) because Kestrel does not support
        // dynamic port 0 on the "localhost" hostname.
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        // ── Pipeline ───────────────────────────────────────────────────────────
        var app = builder.Build();

        app.UseResponseCompression();
        app.UseSwagger();
        app.UseSwaggerUI(c =>
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "Wait Stats API v1"));

        app.UseDefaultFiles();   // serves index.html for "/"
        app.UseStaticFiles();    // serves React dist/ from wwwroot/

        app.MapControllers();

        // SPA fallback — unknown paths return index.html so React Router works
        app.MapFallbackToFile("index.html");

        return app;
    }

    /// <summary>
    /// Returns the actual port Kestrel bound to after the host has started.
    /// Useful when port 0 was passed and the OS assigned a random port.
    /// </summary>
    public static int GetBoundPort(WebApplication app)
    {
        var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        var features = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        // Kestrel may report 0.0.0.0:{port} after binding — parse the port from any address
        var address = features?.Addresses.FirstOrDefault()
                      ?? throw new InvalidOperationException("Could not determine bound port.");
        // Handles http://127.0.0.1:PORT, http://0.0.0.0:PORT, http://[::]:PORT
        var uri = new Uri(address.Replace("[::]:", "http://localhost:").Replace("0.0.0.0:", "http://localhost:"));
        return uri.Port;
    }
}
