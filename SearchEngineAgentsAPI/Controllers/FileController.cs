using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SearchEngineAgentsConfiguration;

namespace SearchEngineAgentsAPI.Controllers;

[ApiController]
public class FileController(
    ILogger<FileController> logger,
    IOptions<ScanInclusions> includeOptions,
    IOptions<ScanExclusions> excludeOptions,
    IOptions<AttachmentSettings> attachOptions,
    IOptions<EmailArchiveSettings> emlOptions
) : ControllerBase
{
    private static string NormalizeDir(string p) =>
        Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private readonly HashSet<string> _monitored =
        includeOptions.Value.Paths
            .Select(NormalizeDir)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _excludedPaths =
        excludeOptions.Value.Paths
            .Select(NormalizeDir)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _excludedFolderNames =
        excludeOptions.Value.FolderNames
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    
    private readonly string? _attachmentsRoot = string.IsNullOrWhiteSpace(attachOptions.Value.RootPath)
        ? null
        : NormalizeDir(attachOptions.Value.RootPath);

    private readonly string? _emlRoot = string.IsNullOrWhiteSpace(emlOptions.Value.RootPath)
        ? null
        : NormalizeDir(emlOptions.Value.RootPath);

    [HttpGet("download/{path}")]
    public async Task<IActionResult> DownloadFile(string path)
    {
        try
        {
            path = CanonicalPath(HttpUtility.UrlDecode(path));
            string normDir = NormalizeDir(Path.GetDirectoryName(path) ?? path);

            bool insideIncluded   = _monitored.Any(m => normDir.StartsWith(m, StringComparison.OrdinalIgnoreCase));
            bool insideExclPath   = _excludedPaths.Any(e => normDir.StartsWith(e, StringComparison.OrdinalIgnoreCase));
            bool hasExclFolder    = path.TrimEnd(Path.DirectorySeparatorChar)
                .Split(Path.DirectorySeparatorChar)
                .Any(part => _excludedFolderNames.Contains(part));
            bool insideAttachments = _attachmentsRoot is not null && normDir.StartsWith(_attachmentsRoot, StringComparison.OrdinalIgnoreCase);
            bool insideEml         = _emlRoot is not null && normDir.StartsWith(_emlRoot, StringComparison.OrdinalIgnoreCase);

            bool allowed = (insideIncluded && !(insideExclPath || hasExclFolder)) || insideAttachments || insideEml;
            if (!allowed) return StatusCode(StatusCodes.Status403Forbidden, "This file is outside the monitored folders.");

            if (!System.IO.File.Exists(path)) return NotFound($"File {path} does not exist.");

            FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            string contentType = GetContentType(path);
            string downloadName = Path.GetFileName(path);
            logger.LogInformation("Serving file {FileName}.", downloadName);
            return File(fs, contentType, downloadName, enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error downloading file with path {Path}.", path);
            return StatusCode(500, "An error occurred while processing the request.");
        }
    }

    private static string GetContentType(string filePath)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };
    }

    private static string CanonicalPath(string p)
    {
        p = p.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return p.TrimEnd(Path.DirectorySeparatorChar);
    }
}
