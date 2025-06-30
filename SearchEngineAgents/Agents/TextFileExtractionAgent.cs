using SearchEngineAgents.Helpers;
using SearchEngineAgents.Models;
using SkiaSharp;

namespace SearchEngineAgents.Agents;

public class TextFileExtractionAgent(ILogger<TextFileExtractionAgent> logger, IConfiguration configuration) : IFileExtractionAgent
{
    private const int Dpi             = 100;
    private const int PageWidthPt     = 612;
    private const int PageHeightPt    = 792;
    private static readonly int PreviewWidth  = (int)(PageWidthPt  * Dpi / 72.0);
    private static readonly int PreviewHeight = (int)(PageHeightPt * Dpi / 72.0);

    private const int Margin  = 20;
    private const int MaxTxtLines = 200;

    private static readonly SKPaint Paint = new()
    {
        Typeface    = SKTypeface.FromFamilyName("Consolas") ?? SKTypeface.Default,
        TextSize    = 14,
        IsAntialias = true,
        Color       = SKColors.Black
    };

    private static readonly int LineHeight;

    static TextFileExtractionAgent()
    {
        SKFontMetrics m = Paint.FontMetrics;
        LineHeight = (int)Math.Ceiling(m.Descent - m.Ascent + m.Leading);
    }

    public IEnumerable<string> SupportedExtensions => ["txt"];

    public async Task<IndexData?> ExtractAsync(string filePath, CancellationToken ct)
    {
        ValidateFilePath(filePath);

        string content = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        
        logger.LogInformation("Extracted file {filePath}.", filePath);
        byte[]? preview = await GeneratePreviewImageAsync(filePath, ct);
        IndexData index = new()
        {
            Uid = FileId.FromPath(filePath),
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            Content = content,
            PreviewIcon = Convert.ToBase64String(preview),
            LastModified = File.GetLastWriteTimeUtc(filePath),
            Created = File.GetCreationTimeUtc(filePath)
        };
        
        return index;
    }

    public async Task<byte[]?> GeneratePreviewImageAsync(string filePath, CancellationToken ct)
    {
        ValidateFilePath(filePath);
        
        string[] rawLines = await File.ReadAllLinesAsync(filePath, ct).ConfigureAwait(false);
        if (rawLines.Length > MaxTxtLines)
            rawLines = rawLines.Take(MaxTxtLines).Concat(["…"]).ToArray();
        
        int usableWidth = PreviewWidth - Margin * 2;
        List<string> wrapped = WrapLines(rawLines, usableWidth);
        
        int maxRows = (PreviewHeight - Margin * 2) / LineHeight;
        if (wrapped.Count > maxRows)
        {
            wrapped = wrapped
                .Take(maxRows - 1)
                .Append("…")
                .ToList();
        }
        
        SKImageInfo info = new(PreviewWidth, PreviewHeight,
            SKColorType.Gray8, SKAlphaType.Opaque);
        using SKSurface? surface = SKSurface.Create(info);
        SKCanvas? canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        float y = Margin - Paint.FontMetrics.Ascent;
        foreach (string line in wrapped)
        {
            canvas.DrawText(line, Margin, y, Paint);
            y += LineHeight;
        }

        using SKImage? img  = surface.Snapshot();
        using SKData? data = img.Encode(SKEncodedImageFormat.Webp, 1);

        byte[] bytes = data.ToArray();
        return bytes;
    }
    
    private static List<string> WrapLines(IEnumerable<string> lines, int maxWidthPx)
    {
        List<string> result = [];
        foreach (string line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                result.Add(string.Empty);
                continue;
            }

            string current = "";
            foreach (string word in line.Split(' '))
            {
                string test = string.IsNullOrEmpty(current) ? word : $"{current} {word}";
                if (Paint.MeasureText(test) > maxWidthPx)
                {
                    if (!string.IsNullOrEmpty(current))
                        result.Add(current);

                    current = word;
                }
                else
                {
                    current = test;
                }
            }
            result.Add(current);
        }
        return result;
    }

    private void ValidateFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            logger.LogWarning("File path must be a non-empty string.");
            throw new ArgumentException("File path must be a non-empty string.", nameof(filePath));
        }

        if (File.Exists(filePath)) return;
        logger.LogWarning("The specified file does not exist.");
        throw new FileNotFoundException("The specified file does not exist.", filePath);
    }
}
