namespace SearchEngineAgents.Settings;

public class EmailSettings
{
    public bool Enabled { get; set; }
    public string ImapHost  { get; set; } = "";
    public int    ImapPort  { get; set; }
    public bool   UseSsl    { get; set; }
    public string Username  { get; set; } = "";
    public string Password  { get; set; } = "";
}