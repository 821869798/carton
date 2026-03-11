namespace carton.Core.Models;

public enum ServiceStatus
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}

public class ServiceState
{
    public ServiceStatus Status { get; set; } = ServiceStatus.Stopped;
    public string StatusText => Status switch
    {
        ServiceStatus.Stopped => "Stopped",
        ServiceStatus.Starting => "Starting...",
        ServiceStatus.Running => "Running",
        ServiceStatus.Stopping => "Stopping...",
        ServiceStatus.Error => "Error",
        _ => "Unknown"
    };
    public long UploadSpeed { get; set; }
    public long DownloadSpeed { get; set; }
    public long TotalUpload { get; set; }
    public long TotalDownload { get; set; }
    public int ConnectionCount { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartTime { get; set; }
}

public class TrafficInfo
{
    public long Uplink { get; set; }
    public long Downlink { get; set; }
}

public class ConnectionInfo
{
    public string Id { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public string Inbound { get; set; } = string.Empty;
    public string Process { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string Outbound { get; set; } = string.Empty;
    public long Upload { get; set; }
    public long Download { get; set; }
}

public class OutboundGroup
{
    public string Tag { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Selected { get; set; } = string.Empty;
    public List<OutboundItem> Items { get; set; } = new();
}

public class OutboundItem
{
    public string Tag { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int UrlTestDelay { get; set; }
}
