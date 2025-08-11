namespace SearchEngineAgentsConfiguration;

public class AttachmentSettings
{
    public bool   Enabled          { get; set; } = true;
    public string RootPath         { get; set; } = "";
    public bool   SaveInline       { get; set; } = false;
    public int    MinInlineBytes   { get; set; } = 4096;
    public int    MaxAttachmentMB  { get; set; } = 25;
    public bool   BlockExecutables { get; set; } = true;
    public List<string> AllowedExtensions { get; init; } =
        [".pdf", ".docx", ".doc", ".txt", ".rtf", ".pptx", ".xlsx", ".csv", ".png", ".jpg", ".jpeg"];
}