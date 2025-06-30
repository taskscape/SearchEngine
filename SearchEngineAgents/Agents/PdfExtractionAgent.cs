using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.RegularExpressions;
using PdfiumViewer;
using SearchEngineAgents.Helpers;
using SearchEngineAgents.Models;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

using PdfDocument = UglyToad.PdfPig.PdfDocument;

namespace SearchEngineAgents.Agents;

public partial class PdfExtractionAgent(ILogger<PdfExtractionAgent> logger) : IFileExtractionAgent
{
    public IEnumerable<string> SupportedExtensions => ["pdf"];
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public async Task<IndexData?> ExtractAsync(string filePath, CancellationToken ct)
    {
        logger.LogInformation("Extracting PDF: {File}", filePath);
        StringBuilder sb = new();
        try
        {
            using PdfDocument pdf = PdfDocument.Open(filePath);
            foreach (Page page in pdf.GetPages()) sb.Append(page.Text);
        }
        catch (PdfDocumentFormatException ex)
        {
            string cleanMessage = Regex.Replace(ex.Message, @"[\p{Cc}&&[^\u000A\u000D\u0009]]", string.Empty);
            logger.LogWarning("Skipping {File}: not recognised as PDF. Error: {message}", filePath, cleanMessage);
            return null; 
        }
        string text = sb.ToString();
        text = ControlCharactersRegex().Replace(text, string.Empty);
        
        string extractedText = SanitizeText(text, text.Length);

        logger.LogInformation(
            "Extracted PDF text from {File} (Length: {Length} chars)", 
            filePath, 
            text.Length
        );
        
        byte[]? previewBytes = await GeneratePreviewImageAsync(filePath, ct);
        if (previewBytes is not null)
        {
            logger.LogInformation("Generated preview image for {File} (Size: {Size} bytes)", filePath, previewBytes.Length);
        }

        IndexData index = new()
        {
            Uid = FileId.FromPath(filePath),
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            Content = extractedText,
            PreviewIcon = Convert.ToBase64String(previewBytes),
            LastModified = File.GetLastWriteTimeUtc(filePath),
            Created = File.GetCreationTimeUtc(filePath)
        };
        
        return index;
    }
    
    public async Task<byte[]?> GeneratePreviewImageAsync(string pdfPath, CancellationToken ct)
    {
        int targetPixelWidth = 200;
        int renderDpi = 72;
        long qualityStart = 80L;
        int maxBytes = 10 * 1024; 
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                using PdfiumViewer.PdfDocument? pdf  = PdfiumViewer.PdfDocument.Load(pdfPath);
                SizeF pageSize = pdf.PageSizes[0];
                
                int renderW = (int)(pageSize.Width  * renderDpi / 72.0f);
                int renderH = (int)(pageSize.Height * renderDpi / 72.0f);
                using Image? pageBmp = pdf.Render(
                    page: 0,
                    width: renderW,
                    height: renderH,
                    dpiX: renderDpi,
                    dpiY: renderDpi,
                    flags: PdfRenderFlags.Annotations);

                float scale = (float)targetPixelWidth / pageBmp.Width;
                int thumbW = targetPixelWidth;
                int thumbH = (int)(pageBmp.Height * scale);

                using Bitmap thumb = new(thumbW, thumbH);
                using (Graphics g = Graphics.FromImage(thumb))
                {
                    g.InterpolationMode  = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    g.DrawImage(pageBmp, 0, 0, thumbW, thumbH);
                }

                using MemoryStream ms = new();
                using EncoderParameters encParams = new(1);
                long quality = qualityStart;

                do
                {
                    ms.SetLength(0);
                    encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                    thumb.Save(ms, JpegCodec, encParams);

                    if (ms.Length <= maxBytes || quality <= 20L)
                    {
                        break;
                    }

                    quality -= 10L;
                }
                while (true);

                return ms.ToArray();
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to generate preview image for {pdfPath}: {ex.Message}");
            return null;
        }
    }
    
    private static string SanitizeText(string text, int maxLength = 200)
    {
        const int stackLimit = 4096;
        if (maxLength < 0) throw new ArgumentOutOfRangeException(nameof(maxLength));
        maxLength = Math.Min(maxLength, text.Length);
        
        if (maxLength <= stackLimit)
        {
            Span<char> buf = stackalloc char[maxLength];
            int i = 0;

            foreach (char c in text.TakeWhile(c => i != maxLength))
            {
                if (c is '\t' or '\n' or '\r' or >= ' ' and <= '~')
                    buf[i++] = c;
            }
            return new string(buf[..i]);
        }
        
        char[] heapBuf = new char[maxLength];
        int j = 0;

        foreach (char c in text.TakeWhile(c => j != maxLength))
        {
            if (c is '\t' or '\n' or '\r' or >= ' ' and <= '~')
                heapBuf[j++] = c;
        }
        return new string(heapBuf, 0, j);
    }

    [GeneratedRegex(@"[\p{Cc}&&[^\u000A\u000D\u0009]]", RegexOptions.Compiled)]
    private static partial Regex ControlCharactersRegex();
}
