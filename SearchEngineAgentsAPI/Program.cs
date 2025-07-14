using SearchEngineAgents.Settings;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

string sharedConfig = Path.Combine(
    builder.Environment.ContentRootPath,
    "..", "SearchEngineAgents", "appsettings.json");

builder.Configuration
    .AddJsonFile(sharedConfig,
        optional: false,
        reloadOnChange: true);

builder.Services.AddControllers().AddNewtonsoftJson(); 
builder.Services.AddLogging(logging => logging.AddConsole());
builder.Services.Configure<ScanInclusions>(builder.Configuration.GetSection("IncludedPaths"));
builder.Services.Configure<ScanExclusions>(builder.Configuration.GetSection("ScanExclusions"));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

WebApplication app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

await app.RunAsync();