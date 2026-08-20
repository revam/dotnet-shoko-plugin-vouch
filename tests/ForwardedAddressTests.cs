using Shoko.Plugin.Vouch.API.Controllers.v1;
using Xunit;

namespace Shoko.Plugin.Vouch.Tests;

/// <summary>
/// What we are willing to believe out of <c>X-Forwarded-For</c>.
///
/// This feeds a rate-limit key, so the property under test is not "does it
/// find the client" but "can a caller put a string of its choosing in here".
/// </summary>
public class ForwardedAddressTests
{
    [Fact]
    public void A_single_address_is_taken()
        => Assert.Equal("203.0.113.7", VouchController.RightmostForwardedAddress("203.0.113.7"));

    [Fact]
    public void The_rightmost_hop_wins_because_proxies_append()
        => Assert.Equal("203.0.113.7", VouchController.RightmostForwardedAddress("198.51.100.1, 203.0.113.7"));

    /// <summary>
    /// The bypass this replaced: vary the prefix, get a fresh bucket. Every
    /// one of these has to land on the same key.
    /// </summary>
    [Theory]
    [InlineData("anything at all, 203.0.113.7")]
    [InlineData("1.1.1.1, 2.2.2.2, 203.0.113.7")]
    [InlineData("   spaced   ,   203.0.113.7   ")]
    public void A_forged_prefix_does_not_change_the_answer(string header)
        => Assert.Equal("203.0.113.7", VouchController.RightmostForwardedAddress(header));

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("203.0.113.7, not-an-address")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("203.0.113.7:8080")]
    public void Anything_unparseable_is_discarded_rather_than_used(string? header)
        => Assert.Null(VouchController.RightmostForwardedAddress(header));

    [Fact]
    public void IPv6_is_understood()
        => Assert.Equal("2001:db8::1", VouchController.RightmostForwardedAddress("198.51.100.1, 2001:db8::1"));
}
