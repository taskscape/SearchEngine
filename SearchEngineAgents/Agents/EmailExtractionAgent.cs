using System.Net;
using System.Text;
using System.Web;
using CoreHtmlToImage;
using HtmlAgilityPack;
using MimeKit;
using SearchEngineAgents.Helpers;
using SearchEngineAgents.Models;

namespace SearchEngineAgents.Agents;

public class EmailExtractionAgent(
    ILogger<EmailExtractionAgent> logger)
    : IEmailExtractionAgent
{
    private const int PreviewWidth  = 800;
    private const int PreviewHeight = 1200;

    private static readonly HtmlConverter Converter = new();
    
    public async Task<EmailData> ExtractAsync(MimeMessage message, CancellationToken ct)
    {
        logger.LogInformation("Processing email from {From} with subject '{Subject}'", message.From, message.Subject);
        
        IEnumerable<MimePart> allParts = message.BodyParts.OfType<MimePart>();
        IEnumerable<MimePart> attachments = allParts.Where(part => 
            (part.ContentDisposition != null && 
             (part.ContentDisposition.IsAttachment || 
              part.ContentDisposition.Disposition.Equals("inline", StringComparison.OrdinalIgnoreCase)))
            || !string.IsNullOrEmpty(part.FileName)
        ); 
        //TODO for now we don't do anything with the attachments
        
        logger.LogInformation("[Email extraction complete for message {MessageId}]", message.MessageId);
        byte[]? preview = await GeneratePreviewImageAsync(message, ct);

        EmailData index = new()
        {
            Uid = EmailId.FromMessageId(message.MessageId),
            Subject = message.Subject,
            From = string.Join(", ", message.From),
            Body = StripHtmlCssAndCollapseLines(message.HtmlBody),
            Sent = message.Date,
            PreviewIcon = preview is null ? null : Convert.ToBase64String(preview)
        };

        return index;
    }
    
    private static string StripHtmlCssAndCollapseLines(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;
        
        HtmlDocument doc = new();
        doc.LoadHtml(html);
        
        foreach (HtmlNode style in doc.DocumentNode.Descendants("style").ToArray())
            style.Remove();
        
        foreach (HtmlNode node in doc.DocumentNode
                     .Descendants()
                     .Where(n => n.NodeType == HtmlNodeType.Element)
                     .ToArray())
        {
            node.ParentNode.ReplaceChild(
                HtmlNode.CreateNode(node.InnerText),
                node
            );
        }
        
        string text = HttpUtility.HtmlDecode(doc.DocumentNode.InnerText);
        
        string normalized = text
            .Replace("\r\n", "\n")
            .Replace("\n\r", "\n")
            .Replace("\r",   "\n");
        
        string[] lines = normalized.Split('\n');
        StringBuilder sb    = new();
        int blankCount = 0;

        foreach (string rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                blankCount++;
                if (blankCount <= 1)
                    sb.AppendLine();
            }
            else
            {
                blankCount = 0;
                sb.AppendLine(rawLine.TrimEnd());
            }
        }

        string collapsed = sb
            .ToString()
            .Trim('\r', '\n');
        
        string result = collapsed.Replace("\n", Environment.NewLine);

        return result;
    }
    
    public async Task<byte[]?> GeneratePreviewImageAsync(
        MimeMessage message,
        CancellationToken ct = default)
    {
        string? html = GetHtml(message);
        if (string.IsNullOrWhiteSpace(html))
            return null;
        
        html = $$"""
                 <!DOCTYPE html>
                 <html>
                 <head>
                     <meta charset="utf-8">
                     <style>
                         body {
                             margin: 0;
                             padding: 24px;
                             font-family: -apple-system, BlinkMacSystemFont,
                                          "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
                             overflow-wrap: anywhere;
                             background: #ffffff;
                             color: #000000;
                             width: {{PreviewWidth - 48}}px;
                             min-height: {{PreviewHeight - 48}}px;
                             box-sizing: border-box;
                         }
                     </style>
                 </head>
                 <body>{{html}}</body>
                 </html>
                 """;
        
        return await Task.Run(
            () => Converter.FromHtmlString(html, PreviewWidth, ImageFormat.Png, quality: 20),
            ct);
    }

    private static string? GetHtml(MimeMessage msg)
    {
        if (!string.IsNullOrWhiteSpace(msg.HtmlBody))
        {
            return msg.HtmlBody; 
        }
        
        TextPart? htmlPart = msg.BodyParts.OfType<TextPart>().FirstOrDefault(p => p.IsHtml);
        if (htmlPart != null)
        {
            return htmlPart.Text;
        }
        
        string? plain = msg.TextBody ?? msg.BodyParts.OfType<TextPart>().FirstOrDefault(p => p.IsPlain)?.Text;
        return plain is null
            ? null
            : $"<pre style=\"white-space:pre-wrap\">{WebUtility.HtmlEncode(plain)}</pre>";
    }
}