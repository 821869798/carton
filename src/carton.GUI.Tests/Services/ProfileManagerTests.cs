using carton.Core.Models;
using carton.Core.Services;
using System.Text.Json.Nodes;
using Xunit;

namespace carton.GUI.Tests.Services;

public sealed class ProfileManagerTests
{
    [Fact]
    public async Task CreateAsync_RuntimeOptionsPreferMixedInboundPort()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "carton-profile-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configManager = new ConfigManager(baseDirectory);
            var profileManager = new ProfileManager(baseDirectory, configManager);
            // The mixed inbound below still carries "set_system_proxy": true on purpose: it used to
            // seed ProfileRuntimeOptions.EnableSystemProxy. The switch is an app-level preference
            // now (see AppPreferences.SystemProxyEnabled), so it must influence nothing here -
            // which is exactly what this test pins by checking only the config-owned values.
            var config = """
            {
              "log": {
                "level": "warn"
              },
              "inbounds": [
                {
                  "type": "socks",
                  "listen": "127.0.0.1",
                  "listen_port": 2080
                },
                {
                  "type": "mixed",
                  "listen": "0.0.0.0",
                  "listen_port": 7890,
                  "set_system_proxy": true
                }
              ]
            }
            """;

            var profile = await profileManager.CreateAsync(new Profile
            {
                Name = "mixed",
                Type = ProfileType.Local
            }, config);

            var options = await profileManager.GetRuntimeOptionsAsync(profile.Id);

            Assert.Equal(7890, options.InboundPort);
            Assert.True(options.AllowLanConnections);
            Assert.Equal("warn", options.LogLevel);
        }
        finally
        {
            if (Directory.Exists(baseDirectory))
            {
                Directory.Delete(baseDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("::", true)]
    [InlineData("[::]", true)]
    [InlineData("192.168.1.8", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("localhost", false)]
    public async Task CreateAsync_RuntimeOptionsReadLanScopeFromListenAddress(string listen, bool expectedAllowLan)
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "carton-profile-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configManager = new ConfigManager(baseDirectory);
            var profileManager = new ProfileManager(baseDirectory, configManager);
            var config = $$"""
            {
              "inbounds": [
                {
                  "type": "mixed",
                  "listen": "{{listen}}",
                  "listen_port": 7890
                }
              ]
            }
            """;

            var profile = await profileManager.CreateAsync(new Profile
            {
                Name = "lan",
                Type = ProfileType.Local
            }, config);

            var options = await profileManager.GetRuntimeOptionsAsync(profile.Id);

            Assert.Equal(expectedAllowLan, options.AllowLanConnections);
        }
        finally
        {
            if (Directory.Exists(baseDirectory))
            {
                Directory.Delete(baseDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ListAndRuntimeOptions_AfterCreate_DoNotReadConfigFileContents()
    {
        // Scope: this pins the steady-state contract, NOT cold start. CreateAsync resolves the
        // runtime options and persists them, so by the time the list or the dashboard reads
        // metadata there is nothing left to derive from the config body.
        //
        // A profile whose RuntimeOptions were never initialized (legacy data, or a
        // hand-edited sing-box-data.json) IS parsed once on its first read and immediately
        // persisted - see GetRuntimeOptionsAsync. That one-time migration is deliberate and
        // is not what this test covers, so it must not be cited as a cold-start guarantee.
        //
        // Proof method: hold every config file open with FileShare.None. Any read attempt
        // from ProfileManager would throw IOException/UnauthorizedAccessException. If both
        // ListAsync and GetRuntimeOptionsAsync still succeed, neither touched the contents.
        var baseDirectory = Path.Combine(Path.GetTempPath(), "carton-profile-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configManager = new ConfigManager(baseDirectory);
            var profileManager = new ProfileManager(baseDirectory, configManager);

            var expectedPorts = new Dictionary<int, int>();
            var lockStreams = new List<FileStream>();
            for (var i = 0; i < 8; i++)
            {
                var port = 2100 + i;
                var profile = await profileManager.CreateAsync(new Profile
                {
                    Name = $"profile-{i}",
                    Type = ProfileType.Local
                }, $$"""
                {
                  "log": { "level": "info" },
                  "inbounds": [
                    { "type": "mixed", "listen": "127.0.0.1", "listen_port": {{port}} }
                  ]
                }
                """);

                expectedPorts[profile.Id] = port;

                var configPath = await configManager.GetConfigPathAsync(profile.Id, ProfileType.Local);
                Assert.NotNull(configPath);
                Assert.True(File.Exists(configPath));
                // Exclusive: no other handle may read it while held.
                lockStreams.Add(new FileStream(configPath!, FileMode.Open, FileAccess.Read, FileShare.None));
            }

            try
            {
                // Sanity: prove the exclusive lock genuinely blocks reads, otherwise the
                // assertions below would pass vacuously (a no-op lock proves nothing).
                var lockedPath = await configManager.GetConfigPathAsync(expectedPorts.Keys.First(), ProfileType.Local);
                Assert.ThrowsAny<IOException>(() => File.ReadAllText(lockedPath!));

                // 1) The list must come back fully populated with metadata only.
                var list = await profileManager.ListAsync();

                Assert.Equal(8, list.Count);
                Assert.Equal(
                    Enumerable.Range(0, 8).Select(i => $"profile-{i}").OrderBy(n => n),
                    list.Select(p => p.Name).OrderBy(n => n));

                // 2) Runtime options must come from persisted state, not from re-parsing JSON.
                foreach (var profile in list)
                {
                    var options = await profileManager.GetRuntimeOptionsAsync(profile.Id);
                    Assert.Equal(expectedPorts[profile.Id], options.InboundPort);
                }
            }
            finally
            {
                foreach (var stream in lockStreams)
                {
                    stream.Dispose();
                }
            }

            // Control: with the locks released the file is readable, so the lock above was
            // genuinely blocking - otherwise the test would pass vacuously.
            var firstId = expectedPorts.Keys.First();
            var content = await configManager.LoadConfigAsync(firstId, ProfileType.Local);
            Assert.NotNull(content);
            Assert.Contains("listen_port", content);
        }
        finally
        {
            if (Directory.Exists(baseDirectory))
            {
                Directory.Delete(baseDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResetRuntimeOptionsToConfig_RestoresOnlyConfigOwnedValues()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "carton-profile-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configManager = new ConfigManager(baseDirectory);
            var profileManager = new ProfileManager(baseDirectory, configManager);
            var config = """
            {
              "log": { "level": "warn" },
              "inbounds": [
                { "type": "mixed", "listen": "0.0.0.0", "listen_port": 7890 }
              ]
            }
            """;

            var profile = await profileManager.CreateAsync(new Profile
            {
                Name = "resettable",
                Type = ProfileType.Local
            }, config);

            // Drift every config-owned value away from the file, then reset.
            await profileManager.SaveRuntimeOptionsAsync(profile.Id, new ProfileRuntimeOptions
            {
                InboundPort = 1111,
                AllowLanConnections = false,
                LogLevel = "error",
                Initialized = true,
                LogLevelInitialized = true
            });

            var reset = await profileManager.ResetRuntimeOptionsToConfigAsync(profile.Id);

            Assert.NotNull(reset);
            Assert.Equal(7890, reset!.InboundPort);
            Assert.True(reset.AllowLanConnections);
            Assert.Equal("warn", reset.LogLevel);
        }
        finally
        {
            if (Directory.Exists(baseDirectory))
            {
                Directory.Delete(baseDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LegacyRuntimeOptionsSwitches_AreIgnoredAndDroppedOnNextSave()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "carton-profile-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configManager = new ConfigManager(baseDirectory);
            var profileManager = new ProfileManager(baseDirectory, configManager);
            var config = """
            {
              "log": { "level": "info" },
              "inbounds": [
                { "type": "mixed", "listen": "127.0.0.1", "listen_port": 2080 }
              ]
            }
            """;

            var profile = await profileManager.CreateAsync(new Profile
            {
                Name = "legacy",
                Type = ProfileType.Local
            }, config);

            // Simulate sing-box-data.json written by a build where the two switches lived in each
            // profile's runtime options. They must not break the load, and they must disappear
            // the next time the file is written (the model no longer has those members).
            var dataPath = Path.Combine(baseDirectory, "sing-box-data.json");
            var data = JsonNode.Parse(await File.ReadAllTextAsync(dataPath))!;
            data["profiles"]![0]!["runtimeOptions"]!["enableSystemProxy"] = true;
            data["profiles"]![0]!["runtimeOptions"]!["enableTunInbound"] = true;
            await File.WriteAllTextAsync(dataPath, data.ToJsonString());

            var options = await profileManager.GetRuntimeOptionsAsync(profile.Id);

            Assert.NotNull(options);
            Assert.Equal(2080, options!.InboundPort);

            await profileManager.SaveRuntimeOptionsAsync(profile.Id, options);

            var rewritten = await File.ReadAllTextAsync(dataPath);
            Assert.DoesNotContain("enableSystemProxy", rewritten);
            Assert.DoesNotContain("enableTunInbound", rewritten);
        }
        finally
        {
            if (Directory.Exists(baseDirectory))
            {
                Directory.Delete(baseDirectory, recursive: true);
            }
        }
    }
}
