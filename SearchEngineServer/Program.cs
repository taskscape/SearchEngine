using Microsoft.Extensions.Primitives;
using SearchEngineServer;
using SearchEngineServer.Models;
using WeaviateNET;
using Results = Microsoft.AspNetCore.Http.Results;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors();
builder.Services.AddHttpClient("AgentsAPI", (sp, client) =>
{
    IConfiguration cfg = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(cfg["AgentsAPI:BaseUrl"]!);
    client.Timeout = TimeSpan.FromMinutes(10);
});

builder.Services.AddSingleton<IIndexQueue, IndexChannelQueue>();
builder.Services.AddSingleton<IDatabaseRepository, WeaviateRepository>();

builder.Services.AddHostedService<IndexBatchingWorker>();
builder.Services.AddHostedService<EmailBatchingWorker>();
builder.Logging.Services.AddLogging(bld => bld.AddConsole());

WebApplication app = builder.Build();
await EnsureWeaviateSchemaAsync(app.Services);
app.UseSwagger();
app.UseSwaggerUI();
app.UseCors(p => p.WithOrigins(builder.Configuration["Client:BaseUrl"] ?? string.Empty).AllowAnyMethod().AllowAnyHeader());

app.MapGet("/search/{query}", async (string query, int hitsPerPage, IDatabaseRepository repository, int page = 0) =>
    {
        IEnumerable<SearchHit> results = await repository.FindMatches(query, hitsPerPage, page);
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

app.MapGet("/download/{*path}", async (
    string path,
    IHttpClientFactory http,
    HttpContext ctx,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest("Missing path.");
    
    HttpRequestMessage req = new(HttpMethod.Get, $"download/{Uri.EscapeDataString(path)}");
    
    if (ctx.Request.Headers.TryGetValue("Range", out StringValues range))
    {
        req.Headers.TryAddWithoutValidation("Range", (string)range);
    }
    
    HttpClient agent = http.CreateClient("AgentsAPI");
    HttpResponseMessage upstream = await agent.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

    if (!upstream.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)upstream.StatusCode);
    }
    
    ctx.Response.RegisterForDispose(upstream);
    Stream body = await upstream.Content.ReadAsStreamAsync(ct);
    ctx.Response.RegisterForDispose(body);
    
    string fileName = Path.GetFileName(Uri.UnescapeDataString(path));
    string contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

    if (upstream.Content.Headers.ContentLength is long len)
    {
        ctx.Response.ContentLength = len;
    }
    
    return Results.File(body, contentType, fileName, enableRangeProcessing: true);
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