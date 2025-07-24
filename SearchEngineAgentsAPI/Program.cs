using System.Reflection;
using SearchEngineAgentsConfiguration;

WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath  = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!
});
string contentRoot = builder.Environment.ContentRootPath;

builder.Configuration.AddJsonFile(
    Path.Combine(contentRoot, "appsettings.json"),
    optional: false,
    reloadOnChange: true);
builder.Services.AddControllers().AddNewtonsoftJson(); 
builder.Services.AddLogging(logging => logging.AddConsole());
builder.Services.AddOptions<ScanInclusions>()
    .BindConfiguration("IncludedPaths");

builder.Services.AddOptions<ScanExclusions>()
    .BindConfiguration("ScanExclusions");
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