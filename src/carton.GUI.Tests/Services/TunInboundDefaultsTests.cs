using System.Text.Json.Nodes;
using carton.Core.Services;
using Xunit;

namespace carton.GUI.Tests.Services;

public sealed class TunInboundDefaultsTests
{
    [Fact]
    public void Apply_PreservesEveryConfiguredValue()
    {
        var tunInbound = new JsonObject
        {
            ["address"] = new JsonArray("10.20.0.1/24", "fd00::1/64"),
            ["auto_route"] = false,
            ["strict_route"] = false,
            ["route_exclude_address"] = new JsonArray("192.168.0.0/16")
        };

        TunInboundDefaults.Apply(tunInbound, supportsIpv6: true);

        // A TUN node the user already wrote must come through untouched: the dashboard switch
        // only expresses on/off, it never rewrites parameters inside the node.
        Assert.Equal("[\"10.20.0.1/24\",\"fd00::1/64\"]", tunInbound["address"]!.ToJsonString());
        Assert.False(tunInbound["auto_route"]!.GetValue<bool>());
        Assert.False(tunInbound["strict_route"]!.GetValue<bool>());
        Assert.Equal("[\"192.168.0.0/16\"]", tunInbound["route_exclude_address"]!.ToJsonString());
    }

    [Theory]
    [InlineData(false, "[\"172.18.0.1/30\"]")]
    [InlineData(true, "[\"172.18.0.1/30\",\"fdfe:dcba:9876::1/126\"]")]
    public void Apply_AddsDefaultsForMissingValues(bool supportsIpv6, string expectedAddresses)
    {
        var tunInbound = new JsonObject();

        TunInboundDefaults.Apply(tunInbound, supportsIpv6);

        Assert.Equal(expectedAddresses, tunInbound["address"]!.ToJsonString());
        Assert.True(tunInbound["auto_route"]!.GetValue<bool>());
        Assert.True(tunInbound["strict_route"]!.GetValue<bool>());
        Assert.Null(tunInbound["route_exclude_address"]);
    }
}
