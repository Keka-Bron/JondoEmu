using Jondo.Unity.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// MCP over stdio: stdout is the protocol's, so every log line goes to stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<Emulator>();
builder.Services.AddSingleton<IMcpTestClient>(services => services.GetRequiredService<Emulator>());
builder.Services.AddSingleton<GameClients>();
var mcp = builder.Services
    .AddMcpServer(options => options.ServerInfo = new() { Name = "jondo", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
foreach (string path in builder.Configuration.GetSection("Mcp:ToolProviders").Get<string[]>() ?? [])
    mcp.WithToolsFromAssembly(ToolProviderLoadContext.LoadProvider(Path.GetFullPath(path)));

await builder.Build().RunAsync();
