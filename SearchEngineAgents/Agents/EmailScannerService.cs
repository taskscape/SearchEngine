using System.Net;
using System.Net.Http.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Options;
using MimeKit;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Polly.Retry;
using SearchEngineAgents.Helpers;
using SearchEngineAgents.Models;
using SearchEngineAgents.Settings;
using SearchEngineAgents.State;

namespace SearchEngineAgents.Agents;

public class EmailScannerService(
    ILogger<EmailScannerService> logger,
    IEmailExtractionAgent emailAgent,
    IOptions<EmailSettings> emailOptions,
    IOptions<AgentDelays> delayOptions,
    EmailState state,
    IConfiguration configuration,
    IHttpClientFactory http)
    : BackgroundService
{
    private readonly EmailSettings _settings = emailOptions.Value;
    private readonly TimeSpan _delay = TimeSpan.FromSeconds(delayOptions.Value.Email);
    private readonly HashSet<Guid> _seen = [];
    private readonly string _server = configuration["ServerAddress"]!;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Connecting to IMAP {Host}:{Port} (SSL={UseSsl})", _settings.ImapHost, _settings.ImapPort, _settings.UseSsl);

            using ImapClient client = new();
            try
            {
                await client.ConnectAsync(
                    _settings.ImapHost, _settings.ImapPort,
                    _settings.UseSsl, stoppingToken);

                await client.AuthenticateAsync(
                    _settings.Username, _settings.Password, stoppingToken);

                IMailFolder inbox = client.Inbox;
                await inbox.OpenAsync(FolderAccess.ReadOnly, stoppingToken);

                IList<UniqueId> uids = await inbox.SearchAsync(
                    SearchQuery.All, stoppingToken);

                foreach (UniqueId uid in uids)
                {
                    MimeMessage msg = await inbox.GetMessageAsync(uid, stoppingToken);
                    Guid id   = EmailId.FromMessageId(msg.MessageId);
                    DateTimeOffset? sent = msg.Date;
 
                    if (state.IsUnchanged(id, sent) && await ServerHasAsync(id, stoppingToken))
                    {
                        _seen.Add(id);
                        continue;
                    }
                    try
                    {
                        EmailData? index = await emailAgent.ExtractAsync(msg, stoppingToken);
                        await SendIndexAsync(index, stoppingToken);
                        state.Touch(id, sent);
                        _seen.Add(id);
                        logger.LogDebug("Uploaded index for message-id {Id}", id);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error extracting or uploading email UID {Uid}", uid);
                    }
                }

                await client.DisconnectAsync(true, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "EmailScannerService error");
            }

            List<Guid> vanished = state.AllIds().Except(_seen).ToList();
            foreach (Guid v in vanished)
            {
                try
                {
                    await SendDeletionAsync(v, stoppingToken);
                    state.Delete(v);
                    logger.LogInformation("E-mail vanished – sent /delete for {Id}", v);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Could not notify server about deletion of {Id}; will retry next cycle.", v);
                }
            }
            _seen.Clear();
            
            await Task.Delay(_delay, stoppingToken);
        }
    }
    
    private async Task SendIndexAsync(EmailData index, CancellationToken ct = default)
    {
        string serverAddress = configuration["ServerAddress"] ?? throw new InvalidOperationException("Missing 'ServerAddress' in configuration.");

        if (!Uri.TryCreate(serverAddress, UriKind.Absolute, out Uri? baseUri))
        {
            throw new InvalidOperationException($"'{serverAddress}' is not a valid absolute URI.");
        }

        IEnumerable<TimeSpan> retryDelays =
            Backoff.ExponentialBackoff(TimeSpan.FromSeconds(1), retryCount: 3, fastFirst: true);

        AsyncRetryPolicy? retryPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .WaitAndRetryAsync(retryDelays);

        await retryPolicy.ExecuteAsync(async token =>
        {
            using HttpRequestMessage req = new(HttpMethod.Post, new Uri(baseUri, "/upload-email"));
            req.Content = JsonContent.Create(index);

            using HttpClient client = http.CreateClient();
            using HttpResponseMessage resp = await client.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, token);

            resp.EnsureSuccessStatusCode();
        }, ct);
    }
    
    private async Task<bool> ServerHasAsync(Guid id, CancellationToken ct)
    {
        using HttpClient client = http.CreateClient();
        using HttpRequestMessage req =
            new(HttpMethod.Head, $"{_server}/doc/{id}");

        try
        {
            using HttpResponseMessage resp =
                await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return resp.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "HEAD probe failed for e-mail {Id}; will re-upload.", id);
            return false;
        }
    }
    
    private async Task SendDeletionAsync(Guid uid, CancellationToken ct = default)
    {
        using HttpClient client = http.CreateClient();
        await client.PostAsync($"{_server}/delete?uid={uid}", content: null, cancellationToken: ct);
    }
}
