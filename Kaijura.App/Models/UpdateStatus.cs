namespace Kaijura.App.Models;

public sealed class UpdateStatus
{
    public string Status { get; set; } = "idle";
    public string Message { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public int Progress { get; set; }
    public bool CanInstall { get; set; }
}
