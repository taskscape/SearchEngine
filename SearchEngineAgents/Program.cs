using System.Text;
using SearchEngineAgents.Agents;
using SearchEngineAgents.Settings;
using SearchEngineAgents.State;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((ctx, cfg) =>
    {
        cfg.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
    })
    .ConfigureServices((ctx, services) =>
    {
        services.AddHttpClient();
        services.Configure<EmailSettings>(ctx.Configuration.GetSection("EmailSettings"));
        services.Configure<AgentDelays>(ctx.Configuration.GetSection("AgentDelaysSeconds"));
        services.Configure<ScanInclusions>(ctx.Configuration.GetSection("IncludedPaths"));
        services.Configure<ScanExclusions>(ctx.Configuration.GetSection("ScanExclusions"));
        
        services.AddSingleton<EmailState>();
        services.AddSingleton<IFileExtractionAgent, PdfExtractionAgent>();
        services.AddSingleton<IFileExtractionAgent, WordExtractionAgent>();
        services.AddSingleton<IFileExtractionAgent, TextFileExtractionAgent>();
        services.AddSingleton<IEmailExtractionAgent, EmailExtractionAgent>();
        services.AddHostedService<FileScannerService>();
        services.AddHostedService<EmailScannerService>();
    })
    .ConfigureLogging(logging => logging.AddConsole())
    .Build();

await host.RunAsync();