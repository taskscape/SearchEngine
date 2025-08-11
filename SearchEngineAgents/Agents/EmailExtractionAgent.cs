using System.Net;
using System.Text;
using System.Web;
using CoreHtmlToImage;
using HtmlAgilityPack;
using MimeKit;
using SearchEngineAgents.Helpers;
using SearchEngineAgents.Models;
using SearchEngineAgents.Settings;
using Microsoft.Extensions.Options;

namespace SearchEngineAgents.Agents;

public class EmailExtractionAgent(
    ILogger<EmailExtractionAgent> logger,
    IOptions<AttachmentSettings> attachOptions,
    IOptions<EmailArchiveSettings> emlOptions)
    : IEmailExtractionAgent
{
    private const int PreviewWidth  = 800;
    private const int PreviewHeight = 1200;

    private static readonly HtmlConverter Converter = new();
    private readonly AttachmentSettings _attach = attachOptions.Value;
    private readonly EmailArchiveSettings _eml = emlOptions.Value;

    public async Task<EmailData> ExtractAsync(MimeMessage message, CancellationToken ct)
    {
        logger.LogInformation("Processing email from {From} with subject '{Subject}'", message.From, message.Subject);

        IEnumerable<MimePart> allParts = message.BodyParts.OfType<MimePart>();
        IEnumerable<MimePart> attachments = allParts.Where(IsAttachmentLike);
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
        
        if (_eml.Enabled && !string.IsNullOrWhiteSpace(_eml.RootPath))
        {
            try
            {
                index.DownloadPath = await SaveEmlAsync(message, index.Uid, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Saving EML failed for {Msg}", message.MessageId);
            }
        }
        
        if (_attach.Enabled && !string.IsNullOrWhiteSpace(_attach.RootPath))
        {
            try
            {
                await SaveAttachmentsAsync(attachments, index.Uid, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Saving attachments failed for {Msg}", message.MessageId);
            }
        }
        
        logger.LogInformation("[Email extraction complete for message {MessageId}]", message.MessageId);
        return index;
    }
    
    private async Task<string> SaveEmlAsync(MimeMessage msg, Guid emailId, CancellationToken ct)
    {
        string root = NormalizeDir(_eml.RootPath);
        Directory.CreateDirectory(root);

        string fileName = MakeSafeBaseName(msg.Subject) + "-" + emailId.ToString("N") + ".eml";
        string fullPath = Path.Combine(root, fileName);

        await using FileStream fs = new(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await msg.WriteToAsync(fs, ct);
        return fullPath;
    }
    
    private static string MakeSafeBaseName(string? s)
    {
        s ??= "email";
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Replace("..", "_").Trim();
        if (s.Length == 0) s = "email";
        if (s.Length > 120) s = s[..120];
        return s;
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

    private async Task SaveAttachmentsAsync(IEnumerable<MimePart> parts, Guid emailId, CancellationToken ct)
    {
        string root = NormalizeDir(_attach.RootPath);
        Directory.CreateDirectory(root);

        string emailDir = Path.Combine(root, emailId.ToString("N"));
        Directory.CreateDirectory(emailDir);

        int index = 0;
        foreach (MimePart part in parts)
        {
            ct.ThrowIfCancellationRequested();

            if (!ShouldPersist(part))
                continue;

            string safeName = MakeSafeFileName(part, index++);
            string target   = Path.Combine(emailDir, safeName);
            
            await using (FileStream fs = new(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await part.Content.DecodeToAsync(fs, ct);
            }

            long size = new FileInfo(target).Length;
            long max  = _attach.MaxAttachmentMB * 1024L * 1024L;
            if (size > max)
            {
                try { File.Delete(target); } catch { /* ignore */ }
                logger.LogInformation("Skipped attachment > {Max} MB: {Name}", _attach.MaxAttachmentMB, safeName);
                continue;
            }

            logger.LogInformation("Saved attachment: {Path} ({Bytes} bytes)", target, size);
        }
    }

    private static bool IsAttachmentLike(MimePart part)
    {
        if (part.ContentDisposition != null &&
            (part.ContentDisposition.IsAttachment ||
             part.ContentDisposition.Disposition.Equals("inline", StringComparison.OrdinalIgnoreCase)))
            return true;

        return !string.IsNullOrEmpty(part.FileName);
    }

    private bool ShouldPersist(MimePart part)
    {
        // Inline filtering
        bool isInline = string.Equals(part.ContentDisposition?.Disposition, "inline", StringComparison.OrdinalIgnoreCase);
        if (isInline && !_attach.SaveInline)
        {
            try
            {
                using Stream s = part.Content.Open();
                if (s.CanSeek && s.Length < _attach.MinInlineBytes) return false;
            }
            catch { /* fallthrough */ }
        }

        string ext = GuessExtension(part).ToLowerInvariant();
        if (_attach.AllowedExtensions.Count > 0 && !string.IsNullOrEmpty(ext) && !_attach.AllowedExtensions.Contains(ext))
            return false;

        if (_attach.BlockExecutables && IsExecutableExtension(ext))
            return false;

        return true;
    }

    private static string MakeSafeFileName(MimePart part, int index)
    {
        string name = part.FileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            string ext = GuessExtension(part);
            name = $"attachment-{index}{ext}";
        }

        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Replace("..", "_");
        return name;
    }

    private static string GuessExtension(MimePart part)
    {
        string ext = Path.GetExtension(part.FileName ?? "");
        if (!string.IsNullOrEmpty(ext)) return ext;

        string? sub = part.ContentType?.MediaSubtype?.ToLowerInvariant();
        string? type = part.ContentType?.MediaType?.ToLowerInvariant();

        return (type, sub) switch
        {
            ("application", "pdf") => ".pdf",
            ("text", "plain")      => ".txt",
            ("image", "jpeg")      => ".jpg",
            ("image", "jpg")       => ".jpg",
            ("image", "png")       => ".png",
            ("application", "vnd.openxmlformats-officedocument.wordprocessingml.document") => ".docx",
            ("application", "msword") => ".doc",
            _ => ".bin"
        };
    }

    private static bool IsExecutableExtension(string ext) =>
        new[] { ".exe", ".msi", ".msp", ".ps1", ".cmd", ".bat", ".vbs", ".js", ".jse", ".scr", ".com" }
            .Contains(ext, StringComparer.OrdinalIgnoreCase);

    private static string NormalizeDir(string p) =>
        Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
}
