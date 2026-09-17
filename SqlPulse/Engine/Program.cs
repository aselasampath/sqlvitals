using SqlPulse.Engine;

// When run directly (dotnet run / dev server), bind to a fixed port.
// When hosted in-process by SqlPulse.Desktop, ApiHost.Build is called from there
// with an explicit free port chosen by PortHelper.
var app = ApiHost.Build(args, port: 5236);
await app.RunAsync();
