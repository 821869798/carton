using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using carton.Core.Models;
using carton.Core.Services;

namespace carton.GUI.Services;

public sealed record RemoteConfigUpdateResult(bool Success, string? ConfigPath, string? ErrorMessage, bool UsedProxy);

public sealed class RemoteConfigUpdateService
{
    private readonly IConfigManager _configManager;
    private readonly IProfileManager _profileManager;
    private readonly IPreferencesService _preferencesService;

    public RemoteConfigUpdateService(
        IConfigManager configManager,
        IProfileManager profileManager,
        IPreferencesService preferencesService)
    {
        _configManager = configManager;
        _profileManager = profileManager;
        _preferencesService = preferencesService;
    }

    public bool IsProxyUpdateEnabled()
        => _preferencesService.Load().UseProxyForRemoteConfigUpdates;

    public bool ShouldDeferRefreshUntilStarted(Profile profile, bool hasLocalConfig)
        => hasLocalConfig && IsProxyUpdateEnabled() && ShouldRefreshOnStart(profile);

    public async Task<RemoteConfigUpdateResult> UpdateAsync(Profile profile, int? mixedPort = null)
    {
        if (profile.Type != ProfileType.Remote)
        {
            return new RemoteConfigUpdateResult(false, null, "Profile is not remote.", false);
        }

        if (string.IsNullOrWhiteSpace(profile.Url))
        {
            return new RemoteConfigUpdateResult(false, null, "Remote profile URL is empty.", false);
        }

        var useProxy = IsProxyUpdateEnabled() && mixedPort is >= 1 and <= 65535;
        HttpClient client = HttpClientFactory.External;
        HttpClient? ownedClient = null;

        if (useProxy)
        {
            ownedClient = HttpClientFactory.CreateExternalProxyClient("127.0.0.1", mixedPort!.Value);
            client = ownedClient;
        }

        try
        {
            var content = await client.GetStringAsync(profile.Url);
            if (string.IsNullOrWhiteSpace(content))
            {
                return new RemoteConfigUpdateResult(false, null, "Downloaded remote config is empty.", useProxy);
            }

            await _configManager.SaveConfigAsync(profile.Id, content, ProfileType.Remote);
            profile.LastUpdated = DateTime.Now;
            await _profileManager.UpdateAsync(profile);

            var downloadedPath = await _configManager.GetConfigPathAsync(profile.Id, ProfileType.Remote);
            if (string.IsNullOrWhiteSpace(downloadedPath) || !File.Exists(downloadedPath))
            {
                return new RemoteConfigUpdateResult(false, null, "Remote config was fetched, but the local file is missing.", useProxy);
            }

            return new RemoteConfigUpdateResult(true, downloadedPath, null, useProxy);
        }
        catch (Exception ex)
        {
            return new RemoteConfigUpdateResult(false, null, ex.Message, useProxy);
        }
        finally
        {
            ownedClient?.Dispose();
        }
    }

    public static bool ShouldRefreshOnStart(Profile profile)
    {
        if (profile.Type != ProfileType.Remote || !profile.AutoUpdate)
        {
            return false;
        }

        if (profile.UpdateInterval <= 0 || profile.LastUpdated == null)
        {
            return true;
        }

        return DateTime.Now - profile.LastUpdated.Value >= TimeSpan.FromMinutes(profile.UpdateInterval);
    }
}
