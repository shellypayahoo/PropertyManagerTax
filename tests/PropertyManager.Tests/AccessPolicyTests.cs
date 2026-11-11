using System.Net;
using PropertyManager.Core.Configuration;

namespace PropertyManager.Tests;

public class AccessPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1", AccessPolicy.Origin.ThisComputer)]
    [InlineData("::1", AccessPolicy.Origin.ThisComputer)]
    [InlineData("::ffff:127.0.0.1", AccessPolicy.Origin.ThisComputer)]
    [InlineData("100.101.102.103", AccessPolicy.Origin.Tailscale)]
    [InlineData("100.64.0.1", AccessPolicy.Origin.Tailscale)]
    [InlineData("100.127.255.254", AccessPolicy.Origin.Tailscale)]
    [InlineData("::ffff:100.101.102.103", AccessPolicy.Origin.Tailscale)]
    [InlineData("fd7a:115c:a1e0::1234", AccessPolicy.Origin.Tailscale)]
    [InlineData("100.63.255.255", AccessPolicy.Origin.Network)]
    [InlineData("100.128.0.1", AccessPolicy.Origin.Network)]
    [InlineData("10.0.0.154", AccessPolicy.Origin.Network)]
    [InlineData("192.168.1.20", AccessPolicy.Origin.Network)]
    [InlineData("8.8.8.8", AccessPolicy.Origin.Network)]
    [InlineData("fe80::1", AccessPolicy.Origin.Network)]
    public void Addresses_are_sorted_by_where_they_come_from(string address, AccessPolicy.Origin expected) =>
        Assert.Equal(expected, AccessPolicy.OriginOf(IPAddress.Parse(address)));

    [Fact]
    public void Network_devices_are_only_let_in_when_Settings_says_so()
    {
        var wifi = IPAddress.Parse("10.0.0.23");
        Assert.False(AccessPolicy.IsAllowed(wifi, new AccessOptions()));
        Assert.True(AccessPolicy.IsAllowed(wifi, new AccessOptions { AllowLocalNetwork = true }));
        Assert.True(AccessPolicy.IsAllowed(IPAddress.Parse("100.101.102.103"), new AccessOptions()));
        Assert.True(AccessPolicy.IsAllowed(IPAddress.Loopback, new AccessOptions()));
    }
}
