using System.Net;
using System.Net.Sockets;

namespace PropertyManager.Core.Configuration;

/// <summary>Who may open the app. It has no sign-in, so this is what keeps other devices on the network out.</summary>
public class AccessOptions
{
    public const string Section = "Access";

    /// <summary>
    /// False (the default): only this computer and devices on the owner's Tailscale network. True: any device that can
    /// reach the computer, e.g. everything on the home Wi-Fi.
    /// </summary>
    public bool AllowLocalNetwork { get; set; }
}

public static class AccessPolicy
{
    public enum Origin
    {
        ThisComputer,
        Tailscale,
        Network,
    }

    /// <summary>Where a request comes from, by its address.</summary>
    public static Origin OriginOf(IPAddress? address)
    {
        if (address is null)
        {
            // In-process requests (tests, the server calling itself) have no address.
            return Origin.ThisComputer;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address))
        {
            return Origin.ThisComputer;
        }
        var bytes = address.GetAddressBytes();
        // Tailscale gives every device an address in 100.64.0.0/10, and in fd7a:115c:a1e0::/48 for IPv6.
        var tailscale = address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 100 && (bytes[1] & 0xC0) == 64
            : bytes[0] == 0xfd && bytes[1] == 0x7a && bytes[2] == 0x11 && bytes[3] == 0x5c && bytes[4] == 0xa1 && bytes[5] == 0xe0;
        return tailscale ? Origin.Tailscale : Origin.Network;
    }

    public static bool IsAllowed(IPAddress? address, AccessOptions options) =>
        options.AllowLocalNetwork || OriginOf(address) != Origin.Network;
}
