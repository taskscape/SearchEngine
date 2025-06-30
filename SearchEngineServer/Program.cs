using SearchEngineServer;
using SearchEngineServer.Models;
using WeaviateNET;
using Results = Microsoft.AspNetCore.Http.Results;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<IIndexQueue, IndexChannelQueue>();
builder.Services.AddSingleton<IDatabaseRepository, WeaviateRepository>();

builder.Services.AddHostedService<IndexBatchingWorker>();
builder.Services.AddHostedService<EmailBatchingWorker>();
builder.Logging.Services.AddLogging(bld => bld.AddConsole());

WebApplication app = builder.Build();
await EnsureWeaviateSchemaAsync(app.Services);
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/search/{query}", async (string query, int limit, IDatabaseRepository repository) =>
    {
        IEnumerable<SearchHit> results = await repository.FindMatches(query, limit);
        return Results.Ok(new { Results = results });
    }
);

app.MapPost("/upload", async (
    IndexData? index,
    IIndexQueue queue,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (index is null)
    {
        return Results.BadRequest();
    }
    
    await queue.EnqueueAsync(index, ct);

    logger.LogInformation("Accepted index for background batching: {FileName}", index.FileName);
    return Results.Accepted();
});

app.MapPost("/upload-email", async (
    EmailData? index,
    IIndexQueue queue,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (index is null)
    {
        return Results.BadRequest();
    }
    
    await queue.EnqueueAsync(index, ct);

    logger.LogInformation("Accepted index for background batching: {Subject}", index.Subject);
    return Results.Accepted();
});

app.MapPost("/delete", async (
    Guid uid,
    IDatabaseRepository repo,
    ILogger<Program> log) =>
{
    bool ok = await repo.DeleteByUidAsync(uid);
    if (!ok) return Results.StatusCode(500);
    log.LogInformation("Deleted {Id}", uid); 
    return Results.Ok();
});

app.MapMethods("/doc/{uid:guid}", ["HEAD"], async (Guid uid, IDatabaseRepository repository) =>
{
    bool exists = await repository.CheckIfIndexExists(uid);
    return exists
        ? Results.Ok()
        : Results.NotFound();
});

app.Run();
return;

static async Task EnsureWeaviateSchemaAsync(IServiceProvider services)
{
    using IServiceScope scope = services.CreateScope();
    IConfiguration cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();

    string? endpoint = cfg["Weaviate:Endpoint"];
    if (string.IsNullOrWhiteSpace(endpoint))
        throw new InvalidOperationException("Weaviate:Endpoint is missing");

    string? apiKey = cfg["Weaviate:ApiKey"];
    WeaviateDB db = string.IsNullOrWhiteSpace(apiKey)
        ? new WeaviateDB(endpoint)
        : new WeaviateDB(endpoint, apiKey);

    await db.Schema.Update();
    
    if (db.Schema.GetClass<IndexData>("Index_data") is null)
    {
        await db.Schema.NewClass<IndexData>("Index_data");
    }
    if (db.Schema.GetClass<EmailData>("Email_data") is null)
    {
        await db.Schema.NewClass<EmailData>("Email_data");
    }
}