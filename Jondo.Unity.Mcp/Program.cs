using Jondo.Unity.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// MCP over stdio: stdout is the protocol's, so every log line goes to stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<Emulator>();
builder.Services.AddSingleton<GameClients>();
builder.Services
    .AddMcpServer(options => options.ServerInfo = new() { Name = "jondo", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
await builder.Build().RunAsync();
