using System.Threading.Channels;
using SearchEngineServer.Models;

namespace SearchEngineServer;

public sealed class IndexChannelQueue : IIndexQueue
{
    private readonly Channel<IndexData> _indexChannel;
    private readonly Channel<EmailData> _emailChannel;

    //public ChannelReader<IndexData> Reader => _indexChannel.Reader;
    public ChannelReader<IndexData> IndexReader  => _indexChannel.Reader;
    public ChannelReader<EmailData> EmailReader  => _emailChannel.Reader;

    public IndexChannelQueue(int capacity = 10_000)
    {
        BoundedChannelOptions options = new(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        };
        _indexChannel = Channel.CreateBounded<IndexData>(options);
        _emailChannel = Channel.CreateBounded<EmailData>(options);
    }

    public async ValueTask EnqueueAsync(IndexData data, CancellationToken ct = default)
        => await _indexChannel.Writer.WriteAsync(data, ct);

    public async ValueTask EnqueueAsync(EmailData data, CancellationToken ct = default)
        => await _emailChannel.Writer.WriteAsync(data, ct);
}
