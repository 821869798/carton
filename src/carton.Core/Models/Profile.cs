namespace carton.Core.Models;

public class Profile
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ProfileType Type { get; set; }
    public string Path { get; set; } = string.Empty;
    public string? Url { get; set; }
    public DateTime? LastUpdated { get; set; }
    public int UpdateInterval { get; set; } = 1440;
    public bool AutoUpdate { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ProfileRuntimeOptions? RuntimeOptions { get; set; }
}

public enum ProfileType
{
    Local,
    Remote
}

public class ProfilePreview
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ProfileType Type { get; set; }
    public DateTime? LastUpdated { get; set; }
}

public class ProfileRuntimeOptions
{
    public int InboundPort { get; set; } = 2028;
    public bool AllowLanConnections { get; set; }
    public bool EnableSystemProxy { get; set; }
    public bool EnableTunInbound { get; set; }
    public string LogLevel { get; set; } = carton.Core.Utilities.SingBoxLogLevelHelper.DefaultLevel;
    public bool LogLevelInitialized { get; set; }
    public bool Initialized { get; set; }
}
