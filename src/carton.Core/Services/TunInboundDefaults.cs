using System.Text.Json.Nodes;

namespace carton.Core.Services;

public static class TunInboundDefaults
{
    /// <summary>
    /// Fills in the values carton needs for a TUN inbound, without ever overriding what the
    /// user already wrote: each key is only set when it is absent.
    /// </summary>
    /// <remarks>
    /// A TUN node carries a lot of parameters (mtu, stack, auto_redirect, exclude_package,
    /// route_exclude_address...). The dashboard switch expresses on/off only, so a node that
    /// already exists in the user's config must come through untouched - including an explicit
    /// <c>auto_route: false</c>, which used to be silently flipped to true just because TUN was
    /// enabled in the UI. A node carton creates itself (the config has no tun inbound at all)
    /// still gets the full default set below, since every key is missing there.
    /// </remarks>
    public static void Apply(JsonObject tunInbound, bool supportsIpv6)
    {
        ArgumentNullException.ThrowIfNull(tunInbound);

        if (tunInbound["address"] is null)
        {
            var addresses = new JsonArray((JsonNode)"172.18.0.1/30");
            if (supportsIpv6)
            {
                addresses.Add((JsonNode)"fdfe:dcba:9876::1/126");
            }

            tunInbound["address"] = addresses;
        }

        if (tunInbound["auto_route"] is null)
        {
            tunInbound["auto_route"] = true;
        }

        if (tunInbound["strict_route"] is null)
        {
            tunInbound["strict_route"] = true;
        }
    }
}
