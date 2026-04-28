namespace Kaijura.App.Models;

public sealed class ConnectionState
{
    public string Status { get; set; } = "unconfigured";
    public string Message { get; set; } = "Configura Jira para empezar.";
    public bool IsConfigured { get; set; }
    public bool IsRefreshing { get; set; }
}
