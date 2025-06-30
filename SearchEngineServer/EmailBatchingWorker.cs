using System.Threading.Channels;
using SearchEngineServer.Models;

namespace SearchEngineServer;

public sealed class EmailBatchingWorker(
    ILogger<EmailBatchingWorker> logger,
    IIndexQueue queue,
    IDatabaseRepository repository,
    IConfiguration config) : BackgroundService
{
    private readonly TimeSpan _flushInterval =
        TimeSpan.FromSeconds(config.GetValue("Batching:FlushSeconds", 30));

    private readonly int _maxBatch =
        config.GetValue("Batching:MaxBatchSize", 256);
    
    private readonly ChannelReader<EmailData> _reader = ((IndexChannelQueue)queue).EmailReader;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(_flushInterval);
        List<EmailData> buffer = new(_maxBatch);

        Task<bool> tickTask = timer.WaitForNextTickAsync(stoppingToken).AsTask();

        try
        {
            while (await _reader.WaitToReadAsync(stoppingToken))
            {
                while (buffer.Count < _maxBatch &&
                       _reader.TryRead(out EmailData? item))
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
                
                if (completed == tickTask && tickTask.IsCompletedSuccessfully)
                {
                    if (buffer.Count > 0)
                    {
                        await FlushAsync(buffer, stoppingToken);
                        buffer.Clear();
                    }
                    tickTask = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                }
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

    private async Task FlushAsync(List<EmailData> batch, CancellationToken ct)
    {
        try
        {
            logger.LogInformation("Flushing {Count} e-mails to Weaviate...", batch.Count);
            bool ok = await repository.BatchAddEmailsAsync(batch, ct);
            if (!ok)
            {
                logger.LogWarning("Flush failed; will retry on next cycle");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error while flushing e-mails");
        }
    }
}