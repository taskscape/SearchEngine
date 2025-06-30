using System.Threading.Channels;
using SearchEngineServer.Models;

namespace SearchEngineServer;

public sealed class IndexBatchingWorker(
        ILogger<IndexBatchingWorker> logger,
        IIndexQueue queue,
        IDatabaseRepository repository,
        IConfiguration config)
    : BackgroundService
{
    private readonly TimeSpan _flushInterval =
        TimeSpan.FromSeconds(config.GetValue("Batching:FlushSeconds", 30));

    private readonly int _maxBatch =
        config.GetValue("Batching:MaxBatchSize", 256);

    private readonly ChannelReader<IndexData> _reader =
        ((IndexChannelQueue)queue).IndexReader;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(_flushInterval);
        List<IndexData> buffer = new(_maxBatch);
        
        Task<bool> tickTask = timer.WaitForNextTickAsync(stoppingToken).AsTask();

        try
        {
            while (await _reader.WaitToReadAsync(stoppingToken))
            {
                while (buffer.Count < _maxBatch &&
                       _reader.TryRead(out IndexData? item))
                {
                    buffer.Add(item);
                }
                
                if (buffer.Count >= _maxBatch)
                {
                    await FlushAsync(buffer, stoppingToken);
                    buffer.Clear();
                }
                
                Task<bool> readTask = _reader.WaitToReadAsync(stoppingToken).AsTask();
                Task<bool> completed = await Task.WhenAny(readTask, tickTask);

                if (completed != tickTask || !tickTask.IsCompletedSuccessfully) continue;
                if (buffer.Count > 0)
                {
                    await FlushAsync(buffer, stoppingToken);
                    buffer.Clear();
                }
                    
                tickTask = timer.WaitForNextTickAsync(stoppingToken).AsTask();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (buffer.Count > 0)
            {
                await FlushAsync(buffer, CancellationToken.None);
            }
        }
    }

    private async Task FlushAsync(
        List<IndexData> batch,
        CancellationToken ct)
    {
        try
        {
            logger.LogInformation("Flushing {Count} indices to Weaviate...", batch.Count);
            bool ok = await repository.BatchAddIndexesAsync(batch, ct);
            if (!ok) logger.LogWarning("Flush failed; will retry on next cycle");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error while flushing");
        }
    }
}
