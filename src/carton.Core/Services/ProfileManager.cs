using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Threading;
using carton.Core.Models;
using carton.Core.Serialization;

namespace carton.Core.Services;

public interface IProfileManager
{
    Task<List<Profile>> ListAsync();
    Task<Profile?> GetAsync(int id);
    Task<Profile> CreateAsync(Profile profile, string? configContent = null);
    Task UpdateAsync(Profile profile);
    Task DeleteAsync(int id);
    Task<int> GetSelectedProfileIdAsync();
    Task SetSelectedProfileIdAsync(int id);
    Task<ProfileRuntimeOptions> GetRuntimeOptionsAsync(int profileId);
    Task SaveRuntimeOptionsAsync(int profileId, ProfileRuntimeOptions options);
}

public class ProfileManager : IProfileManager
{
    private readonly string _dataPath;
    private readonly IConfigManager _configManager;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public ProfileManager(string baseDirectory, IConfigManager configManager)
    {
        Directory.CreateDirectory(baseDirectory);
        _dataPath = Path.Combine(baseDirectory, "sing-box-data.json");
        _configManager = configManager;
    }

    public async Task<List<Profile>> ListAsync()
    {
        await _syncLock.WaitAsync();
        try
        {
            var data = await LoadOrCreateDataUnlockedAsync();
            var normalized = NormalizeProfileIds(data.Profiles);
            var selectedNormalized = EnsureSelectedProfileExists(data);

            if (normalized || selectedNormalized)
            {
                await SaveDataUnlockedAsync(data);
            }

            return data.Profiles.Select(CloneProfile).ToList();
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<Profile?> GetAsync(int id)
    {
        await _syncLock.WaitAsync();
        try
        {
            var data = await LoadOrCreateDataUnlockedAsync();
            var profile = data.Profiles.FirstOrDefault(p => p.Id == id);
            return profile != null ? CloneProfile(profile) : null;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<Profile> CreateAsync(Profile profile, string? configContent = null)
    {
        await _syncLock.WaitAsync();
        try
        {
            var data = await LoadOrCreateDataUnlockedAsync();
            var maxId = data.Profiles.Count > 0 ? data.Profiles.Max(p => p.Id) : 0;
            profile.Id = maxId + 1;
            profile.CreatedAt = DateTime.Now;
            profile.RuntimeOptions = await ResolveRuntimeOptionsAsync(profile.Id, profile.RuntimeOptions);
            data.Profiles.Add(profile);
            await SaveDataUnlockedAsync(data);

            if (!string.IsNullOrEmpty(configContent))
            {
                await _configManager.SaveConfigAsync(profile.Id, configContent, profile.Type);
            }

            return CloneProfile(profile);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task UpdateAsync(Profile profile)
    {
        await _syncLock.WaitAsync();
        try
        {
            var data = await LoadOrCreateDataUnlockedAsync();
            var index = data.Profiles.FindIndex(p => p.Id == profile.Id);
            if (index >= 0)
            {
                profile.RuntimeOptions = await ResolveRuntimeOptionsAsync(profile.Id, profile.RuntimeOptions);
                data.Profiles[index] = profile;
                await SaveDataUnlockedAsync(data);
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task DeleteAsync(int id)
    {
        await _syncLock.WaitAsync();
        try
        {
            var data = await LoadOrCreateDataUnlockedAsync();
            var removed = data.Profiles.RemoveAll(p => p.Id == id) > 0;
            if (removed && data.SelectedProfileId == id)
            {
                data.SelectedProfileId = 0;
            }

            if (removed)
            {
                await SaveDataUnlockedAsync(data);
            }
        }
        finally
        {
            _syncLock.Release();
        }

        await _configManager.DeleteConfigAsync(id);
    }

    public async Task<int> GetSelectedProfileIdAsync()
    {
        await _syncLock.WaitAsync();
        try
        {
            var data = await LoadOrCreateDataUnlockedAsync();
            var changed = EnsureSelectedProfileExists(data);
            if (changed)
            {
                await SaveDataUnlockedAsync(data);
            }
            return data.SelectedProfileId;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task SetSelectedProfileIdAsync(int id)
    {
        await _syncLock.WaitAsync();
        try
        {
            var data = await LoadOrCreateDataUnlockedAsync();
            data.SelectedProfileId = id;
            await SaveDataUnlockedAsync(data);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<ProfileRuntimeOptions> GetRuntimeOptionsAsync(int profileId)
    {
        await _syncLock.WaitAsync();
        try
        {
            var data = await LoadOrCreateDataUnlockedAsync();
            var profile = data.Profiles.FirstOrDefault(p => p.Id == profileId);
            return profile?.RuntimeOptions != null
                ? CloneRuntimeOptions(profile.RuntimeOptions)
                : new ProfileRuntimeOptions();
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task SaveRuntimeOptionsAsync(int profileId, ProfileRuntimeOptions options)
    {
        await _syncLock.WaitAsync();
        try
        {
            var data = await LoadOrCreateDataUnlockedAsync();
            var profile = data.Profiles.FirstOrDefault(p => p.Id == profileId);
            if (profile == null)
            {
                return;
            }

            var normalized = CloneRuntimeOptions(options);
            normalized.Initialized = true;
            normalized.InboundPort = NormalizePort(normalized.InboundPort);
            profile.RuntimeOptions = normalized;
            await SaveDataUnlockedAsync(data);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<SingBoxData> LoadOrCreateDataUnlockedAsync()
    {
        if (File.Exists(_dataPath))
        {
            var json = await File.ReadAllTextAsync(_dataPath);
            var data = JsonSerializer.Deserialize(
                           json,
                           CartonCoreJsonContext.Default.SingBoxData) ?? new SingBoxData();
            await EnsureConfigLayoutAsync(data.Profiles);
            await EnsureRuntimeOptionsAsync(data.Profiles);
            return data;
        }

        var dataToCreate = new SingBoxData();
        await EnsureConfigLayoutAsync(dataToCreate.Profiles);
        await EnsureRuntimeOptionsAsync(dataToCreate.Profiles);
        await SaveDataUnlockedAsync(dataToCreate);
        return dataToCreate;
    }

    private async Task SaveDataUnlockedAsync(SingBoxData data)
    {
        var buffer = new ArrayBufferWriter<byte>();
        await using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            Encoder = Utilities.UnicodeJsonEncoder.Instance
        }))
        {
            JsonSerializer.Serialize(writer, data, CartonCoreJsonContext.Default.SingBoxData);
            await writer.FlushAsync();
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);

        var directory = Path.GetDirectoryName(_dataPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(_dataPath, json);
    }

    private static bool NormalizeProfileIds(List<Profile> profiles)
    {
        var changed = false;
        var usedIds = new HashSet<int>();
        var nextId = 1;

        foreach (var profile in profiles)
        {
            if (profile.Id > 0 && usedIds.Add(profile.Id))
            {
                nextId = Math.Max(nextId, profile.Id + 1);
                continue;
            }

            while (usedIds.Contains(nextId))
            {
                nextId++;
            }

            profile.Id = nextId;
            usedIds.Add(nextId);
            nextId++;
            changed = true;
        }

        return changed;
    }

    private static bool EnsureSelectedProfileExists(SingBoxData data)
    {
        if (data.SelectedProfileId == 0)
        {
            return false;
        }

        if (data.Profiles.Any(p => p.Id == data.SelectedProfileId))
        {
            return false;
        }

        data.SelectedProfileId = 0;
        return true;
    }

    private async Task EnsureRuntimeOptionsAsync(IEnumerable<Profile> profiles)
    {
        foreach (var profile in profiles)
        {
            profile.RuntimeOptions = await ResolveRuntimeOptionsAsync(profile.Id, profile.RuntimeOptions);
        }
    }

    private async Task EnsureConfigLayoutAsync(IEnumerable<Profile> profiles)
    {
        foreach (var profile in profiles)
        {
            await _configManager.GetConfigPathAsync(profile.Id, profile.Type);
        }
    }

    private async Task<ProfileRuntimeOptions> ResolveRuntimeOptionsAsync(int profileId, ProfileRuntimeOptions? options)
    {
        if (options != null && options.Initialized)
        {
            options.InboundPort = NormalizePort(options.InboundPort);
            return options;
        }

        var resolved = await TryLoadRuntimeOptionsFromConfigAsync(profileId) ?? options ?? new ProfileRuntimeOptions();
        resolved.Initialized = true;
        resolved.InboundPort = NormalizePort(resolved.InboundPort);
        return resolved;
    }

    private async Task<ProfileRuntimeOptions?> TryLoadRuntimeOptionsFromConfigAsync(int profileId)
    {
        try
        {
            var configContent = await _configManager.LoadConfigAsync(profileId);
            if (string.IsNullOrWhiteSpace(configContent))
            {
                return null;
            }

            using var document = JsonDocument.Parse(configContent);
            if (!document.RootElement.TryGetProperty("inbounds", out var inboundsElement) ||
                inboundsElement.ValueKind != JsonValueKind.Array ||
                inboundsElement.GetArrayLength() == 0)
            {
                return null;
            }

            var hasTun = false;
            JsonElement? firstInbound = null;
            JsonElement? primaryInbound = null;

            foreach (var inbound in inboundsElement.EnumerateArray())
            {
                if (inbound.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                firstInbound ??= inbound;

                var type = TryReadString(inbound, "type");
                if (string.Equals(type, "tun", StringComparison.OrdinalIgnoreCase))
                {
                    hasTun = true;
                    continue;
                }

                primaryInbound ??= inbound;
            }

            var inboundElement = primaryInbound ?? firstInbound;
            if (inboundElement is null)
            {
                return null;
            }

            var port = TryReadInt(inboundElement.Value, "listen_port") ?? 2028;
            var listen = TryReadString(inboundElement.Value, "listen");
            var setSystemProxy = TryReadBool(inboundElement.Value, "set_system_proxy") ?? false;

            return new ProfileRuntimeOptions
            {
                InboundPort = NormalizePort(port),
                AllowLanConnections = string.Equals(listen, "0.0.0.0", StringComparison.OrdinalIgnoreCase),
                EnableSystemProxy = setSystemProxy,
                EnableTunInbound = hasTun,
                Initialized = true
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static int? TryReadInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var longValue))
        {
            return (int)longValue;
        }

        if (property.ValueKind == JsonValueKind.String &&
            int.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        return null;
    }

    private static bool? TryReadBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property))
        {
            return property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
                _ => null
            };
        }

        return null;
    }

    private static Profile CloneProfile(Profile profile)
    {
        return new Profile
        {
            Id = profile.Id,
            Name = profile.Name,
            Type = profile.Type,
            Path = profile.Path,
            Url = profile.Url,
            LastUpdated = profile.LastUpdated,
            UpdateInterval = profile.UpdateInterval,
            AutoUpdate = profile.AutoUpdate,
            CreatedAt = profile.CreatedAt,
            RuntimeOptions = CloneRuntimeOptions(profile.RuntimeOptions ?? new ProfileRuntimeOptions())
        };
    }

    private static ProfileRuntimeOptions CloneRuntimeOptions(ProfileRuntimeOptions options)
    {
        return new ProfileRuntimeOptions
        {
            InboundPort = NormalizePort(options.InboundPort),
            AllowLanConnections = options.AllowLanConnections,
            EnableSystemProxy = options.EnableSystemProxy,
            EnableTunInbound = options.EnableTunInbound,
            Initialized = options.Initialized
        };
    }

    private static int NormalizePort(int port)
    {
        return port is >= 1 and <= 65535 ? port : 2028;
    }

}
