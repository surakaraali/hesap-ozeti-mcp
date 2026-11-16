using HesapOzeti.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP channel. A log line there would corrupt the protocol, so every level goes to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
// HttpClient logs every request URL at Information. The URLs hold no secrets, but logs are not the place for
// what an accountant asked about, so only warnings and errors are kept.
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

var settings = Settings.FromEnvironment(Environment.GetEnvironmentVariable);
if (settings.Error is { } error)
{
    // Fail at startup: without a token every tool call would fail anyway. The message never contains the token.
    await Console.Error.WriteLineAsync(error);
    return 1;
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<HesapOzetiApi>(http =>
{
    http.BaseAddress = settings.ApiUrl;
    http.DefaultRequestHeaders.Authorization = new("Bearer", settings.Token);
    http.Timeout = HesapOzetiApi.Timeout;
});

builder.Services
    .AddMcpServer(options => options.ServerInstructions = Prompts.ServerInstructions)
    .WithStdioServerTransport()
    .WithTools([typeof(Tools)])
    .WithPrompts([typeof(Prompts)]);

await builder.Build().RunAsync();
return 0;
