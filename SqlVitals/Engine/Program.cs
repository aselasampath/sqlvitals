using SqlVitals.Engine;

// Standalone REST API entry point (dotnet run / dev server), bound to a fixed port.
// The desktop app does not use this host; it calls the repositories directly.
var app = ApiHost.Build(args, port: 5236);
await app.RunAsync();
