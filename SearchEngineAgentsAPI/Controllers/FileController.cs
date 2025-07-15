using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SearchEngineAgentsConfiguration;

namespace SearchEngineAgentsAPI.Controllers;

[ApiController]
public class FileController(ILogger<FileController> logger, IOptions<ScanInclusions> includeOptions, IOptions<ScanExclusions> excludeOptions) : ControllerBase
{
    private readonly HashSet<string> _monitored =
        includeOptions.Value.Paths
            .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _excludedPaths =
        excludeOptions.Value.Paths
            .Select(p => Path.GetFullPath(p)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _excludedFolderNames =
        excludeOptions.Value.FolderNames
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    
    [HttpGet("download/{path}")]
    public async Task<IActionResult> DownloadFile(string path)
    {
        try
        { 
            string full = Path.GetFullPath(path);
            bool insideIncluded  = _monitored.Any(m => full.StartsWith(m, StringComparison.OrdinalIgnoreCase));
            bool insideExclPath = _excludedPaths.Any(e => full.StartsWith(e, StringComparison.OrdinalIgnoreCase));
            bool hasExclFolder = full.TrimEnd(Path.DirectorySeparatorChar)
                .Split(Path.DirectorySeparatorChar)
                .Any(part => _excludedFolderNames.Contains(part));

            bool allowed = insideIncluded && !(insideExclPath || hasExclFolder);
            if (!allowed)
            {
                logger.LogWarning("Blocked download of excluded file {FullPath}", full);
                return StatusCode(StatusCodes.Status403Forbidden, "This file is outside the monitored folders.");
            }
            
            if (!System.IO.File.Exists(full))
            {
                logger.LogWarning("File {FullPath} does not exist.", full);
                return NotFound($"File {full} does not exist.");
            }

            FileStream fileStream = new(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            string contentType = GetContentType(full);
            string fileName = Path.GetFileName(full);

            logger.LogInformation("Serving file {FileName}.", fileName);
            
            return File(fileStream, contentType, fileName, enableRangeProcessing: true);
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
}