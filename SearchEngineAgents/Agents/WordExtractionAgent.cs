using System.Text;
using System.Drawing;
using System.Drawing.Imaging;
using DocumentFormat.OpenXml.Packaging;
using NPOI.HWPF;
using SearchEngineAgents.Helpers;
using SearchEngineAgents.Models;
using Range = NPOI.HWPF.UserModel.Range;
using W = DocumentFormat.OpenXml.Wordprocessing;
using SD = Spire.Doc;
using SDD = Spire.Doc.Documents;

namespace SearchEngineAgents.Agents;

public class WordExtractionAgent(ILogger<WordExtractionAgent> logger) : IFileExtractionAgent
{
    public IEnumerable<string> SupportedExtensions => ["docx", "doc"];

    public async Task<IndexData?> ExtractAsync(string filePath, CancellationToken ct)
    {
        logger.LogInformation("Extracting Word doc: {File}", filePath);

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        string extractedText = string.Empty;

        try
        {
            extractedText = await Task.Run(() => ext switch
            {
                ".docx" => ExtractDocx(filePath, ct),
                ".doc"  => ExtractBinaryDoc(filePath, ct),
                _       => throw new NotSupportedException($"Unsupported Word format: {ext}")
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex,
                "Skipping file {File} – unparseable or corrupt. Error: {Message}",
                filePath, ex.Message);
            return null;
        }

        if (string.IsNullOrWhiteSpace(extractedText))
        {
            logger.LogWarning("No content extracted from {File}. Skipping.", filePath);
            return null;
        }

        return new IndexData
        {
            Uid = FileId.FromPath(filePath),
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            Content = extractedText,
            LastModified = File.GetLastWriteTimeUtc(filePath),
            Created = File.GetCreationTimeUtc(filePath)
        };
    }

    private static string ExtractDocx(string path, CancellationToken ct)
    {
        StringBuilder sb = new();

        using WordprocessingDocument doc = WordprocessingDocument.Open(path, false);
        W.Body? body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return string.Empty;

        foreach (W.Text txt in body.Descendants<W.Text>())
        {
            ct.ThrowIfCancellationRequested();
            sb.Append(txt.Text);

            if (txt.Parent is W.Break or W.Paragraph)
                sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string ExtractBinaryDoc(string path, CancellationToken ct)
    {
        StringBuilder sb = new();

        using FileStream fs = File.OpenRead(path);
        HWPFDocument doc = new(fs);
        Range range = doc.GetRange();

        for (int i = 0; i < range.NumParagraphs; i++)
        {
            ct.ThrowIfCancellationRequested();
            string? paraText = range.GetParagraph(i).Text;
            sb.AppendLine(paraText?.TrimEnd('\r', '\u0007'));
        }

        return sb.ToString();
    }
    
    public async Task<byte[]?> GeneratePreviewImageAsync(string wordPath, CancellationToken ct)
    {
        const int dpi = 150;
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                
                using SD.Document spireDoc = new();
                spireDoc.LoadFromFile(wordPath);
                
                using Image img = spireDoc.SaveToImages(0, SDD.ImageType.Bitmap);
                
                if (Math.Abs(img.HorizontalResolution - dpi) > 0.1f || Math.Abs(img.VerticalResolution - dpi) > 0.1f)
                {
                    using Bitmap clone = new(img);
                    clone.SetResolution(dpi, dpi);
                    using MemoryStream cloneStream = new();
                    clone.Save(cloneStream, ImageFormat.Png);
                    return cloneStream.ToArray();
                }
                
                using MemoryStream ms = new();
                img.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to generate preview for {wordPath}: {ex.Message}");
            return null;
        }
    }
    
    private static string SavePreviewToDisk(string originalPath, byte[] pngBytes)
    {
        string directory = Path.GetDirectoryName(originalPath) ?? Path.GetTempPath();
        string fileName   = Path.GetFileNameWithoutExtension(originalPath);
        string previewPath = Path.Combine(directory, $"{fileName}.preview.png");

        File.WriteAllBytes(previewPath, pngBytes);
        return previewPath;
    }
}
