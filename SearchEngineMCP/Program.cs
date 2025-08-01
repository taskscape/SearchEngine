using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using SearchEngineMCP;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory,
    Args = args
});
void AddJsonAt(string baseDir)
{
    if (string.IsNullOrWhiteSpace(baseDir)) return;
    string main = Path.Combine(baseDir, "appsettings.json");
    if (File.Exists(main)) builder.Configuration.AddJsonFile(main, optional: true, reloadOnChange: true);
}
AddJsonAt(AppContext.BaseDirectory);
AddJsonAt(Directory.GetCurrentDirectory());

builder.Services.Configure<SearchEngineServerOptions>(builder.Configuration.GetSection("SearchEngineServer"));
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddHttpClient<IVectorSearchClient, VectorSearchClient>(
    (sp, http) =>
    {
        SearchEngineServerOptions opts = sp.GetRequiredService<IOptions<SearchEngineServerOptions>>().Value;
        
        if (string.IsNullOrWhiteSpace(opts.BaseAddress))
            throw new InvalidOperationException(
                "SearchEngineServer:BaseAddress is missing from configuration.");

        http.BaseAddress = new Uri(opts.BaseAddress, UriKind.Absolute);
    });

builder.Services
    .AddMcpServer(opts =>
    {
        opts.ServerInfo = new Implementation
        {
            Name = "SearchEngineMCP",
            Version = "1.0.0",
            Title = "SearchEngineMCP"
        };
    })
    .WithStdioServerTransport()
    .WithTools<FileSearchTools>();

await builder.Build().RunAsync();