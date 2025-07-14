using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Polly.Retry;
using SearchEngineAgents.Helpers;
using SearchEngineAgents.Models;
using SearchEngineAgents.Settings;
using SearchEngineAgents.State;

namespace SearchEngineAgents.Agents;

public class FileScannerService(
    ILogger<FileScannerService> logger,
    IEnumerable<IFileExtractionAgent> fileAgents,
    IConfiguration configuration,
    IOptions<AgentDelays> delayOptions,
    IOptions<ScanInclusions> inclusionOptions,
    IOptions<ScanExclusions> exclusionOptions,
    IHttpClientFactory http)
    : BackgroundService
{
    private readonly TimeSpan _delay = TimeSpan.FromSeconds(delayOptions.Value.Files);
    private readonly HashSet<string> _includedRoots =
        inclusionOptions.Value.Paths
            .Select(p => Path.GetFullPath(p)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excludedPaths =
        exclusionOptions.Value.Paths
            .Select(p => Path.GetFullPath(p)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _excludedFolderNames =
        exclusionOptions.Value.FolderNames
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    
    private readonly ScanState _state = new();
    private readonly HashSet<Guid> _seen = [];
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Dictionary<string, List<IFileExtractionAgent>> extensionMap = fileAgents
            .SelectMany(a => a.SupportedExtensions.Select(ext => (ext, agent: a)))
            .GroupBy(x => x.ext, x => x.agent, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        logger.LogInformation("FileScannerService starting up.");

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (string root in _includedRoots)
            {
                if (!Directory.Exists(root))
                {
                    logger.LogWarning("Configured IncludedPath does not exist: {Path}", root);
                    continue;
                }

                logger.LogInformation("Scanning root path {Path}...", root);
                await ScanDirectoryAndSendIndexesAsync(root, extensionMap, stoppingToken);
            }

            List<Guid> vanished = _state
                .AllIds()
                .Except(_seen)
                .ToList();
            
            foreach (Guid id in vanished)
                _state.Delete(id);
            
            foreach (Guid id in vanished)
            {
                try
                {
                    await SendDeletionAsync(id, stoppingToken);
                    logger.LogInformation("File vanished – sent /delete for {Id}", id);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Could not notify server about deletion of {Id}; will retry next cycle.",
                        id);
                }
            }
            
            _seen.Clear();

            await Task.Delay(_delay, stoppingToken);
        }

        logger.LogInformation("FileScannerService shutting down.");
    }

    private async Task ScanDirectoryAndSendIndexesAsync(
        string root,
        Dictionary<string, List<IFileExtractionAgent>> extensionMap,
        CancellationToken ct)
    {
        Stack<string> dirs = new();
        dirs.Push(root);

        while (dirs.Count > 0 && !ct.IsCancellationRequested)
        {
            string current = dirs.Pop();
            if (IsExcludedDirectory(current)) continue; 
            
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch (Exception)
            {
                logger.LogWarning("Cannot access folder: {Folder}", current);
                continue;
            }

            foreach (string file in files)
            {
                if (IsExcludedFile(file)) continue;
                Guid id = FileId.FromPath(file);
                DateTime mUtc = File.GetLastWriteTimeUtc(file);

                if (_state.IsUnchanged(id, mUtc) && await ServerHasAsync(id, ct))
                {
                    _seen.Add(id);
                    continue;
                }

                string ext = Path.GetExtension(file).TrimStart('.');
                if (!extensionMap.TryGetValue(ext, out List<IFileExtractionAgent>? agents)) continue;
                foreach (IFileExtractionAgent agent in agents)
                {
                    try
                    {
                        IndexData? index = await agent.ExtractAsync(file, ct);
                        if (index is not null)
                        {
                            await SendIndexAsync(index, ct);
                            _state.Touch(id, mUtc, file);
                            _seen.Add(id);
                        }
                    }
                    catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException)
                    {
                        logger.LogWarning("Cannot extract encrypted file: {File}", file);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error processing file {File}", file);
                    }
                }
            }

            try
            {
                foreach (string sub in Directory.EnumerateDirectories(current))
                    if (!IsExcludedDirectory(sub))
                    {
                        dirs.Push(sub);
                    }
            }
            catch
            {
                logger.LogWarning("Cannot enumerate subfolders of: {Folder}", current);
            }
        }
    }

    private async Task SendIndexAsync(IndexData index, CancellationToken cancellationToken = default)
    {
        string serverAddress = configuration["ServerAddress"]
                               ?? throw new InvalidOperationException("Missing 'ServerAddress' in configuration.");

        if (!Uri.TryCreate(serverAddress, UriKind.Absolute, out Uri? baseUri))
        {
            throw new InvalidOperationException($"'{serverAddress}' is not a valid absolute URI.");
        }

        IEnumerable<TimeSpan> retryDelays = Backoff.ExponentialBackoff(TimeSpan.FromSeconds(1), 3, fastFirst: true);

        AsyncRetryPolicy retryPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .WaitAndRetryAsync(retryDelays);

        await retryPolicy.ExecuteAsync(async ct =>
        {
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri(baseUri, "/upload"));
            request.Content = JsonContent.Create(index);

            using HttpClient client = http.CreateClient();
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
        }, cancellationToken);

        logger.LogDebug("Uploaded index for file '{Path}'", index.FilePath);
    }
    
    private async Task SendDeletionAsync(Guid uid, CancellationToken ct = default)
    {
        string serverAddress = configuration["ServerAddress"]!;
        using HttpClient client = http.CreateClient();
        await client.PostAsync($"{serverAddress}/delete?uid={uid}", content: null, cancellationToken: ct);
    }

    private bool IsExcludedDirectory(string dir)
    {
        string norm = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        
        if (_excludedPaths.Any(p => norm.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return true;
        
        string name = Path.GetFileName(norm.TrimEnd(Path.DirectorySeparatorChar));
        return _excludedFolderNames.Contains(name);
    }
    
    private async Task<bool> ServerHasAsync(Guid uid, CancellationToken ct)
    {
        using HttpClient client = http.CreateClient();
        using HttpRequestMessage req = new(HttpMethod.Head, new Uri(new Uri(configuration["ServerAddress"]), $"/doc/{uid}"));

        try
        {
            using HttpResponseMessage resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return resp.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex,
                "HEAD probe failed for {Uid}; will assume missing and re-upload.",
                uid);
            return false;
        }
    }

    private bool IsExcludedFile(string file)
        => _excludedPaths.Any(p => file.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}