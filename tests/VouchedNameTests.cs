using Shoko.Plugin.Vouch.API.Controllers.v1;
using Xunit;

namespace Shoko.Plugin.Vouch.Tests;

/// <summary>
/// The device name an issued key carries.
///
/// The marker in it is a security control rather than a label: Approve
/// refuses a caller whose own device name contains it, which is what stops a
/// vouched key from vouching. So these tests are mostly about the marker
/// surviving inputs that would otherwise push it off the end.
/// </summary>
public class VouchedNameTests
{
    [Fact]
    public void A_vouched_name_says_who_vouched_for_it()
        => Assert.Equal("Living Room TV (Vouched by revam)", VouchController.StampVouchedBy("Living Room TV", "revam"));

    [Fact]
    public void The_marker_survives_a_device_name_that_fills_the_budget()
    {
        var stamped = VouchController.StampVouchedBy(new string('a', 200), "revam");
        Assert.Contains(Constants.VouchedByMarker, stamped, StringComparison.Ordinal);
        Assert.EndsWith(")", stamped, StringComparison.Ordinal);
        Assert.True(stamped.Length <= Constants.IssuedDeviceNameLimit, $"was {stamped.Length}");
    }

    [Fact]
    public void The_marker_survives_a_username_that_fills_the_budget()
    {
        var stamped = VouchController.StampVouchedBy("TV", new string('u', 200));
        Assert.Contains(Constants.VouchedByMarker, stamped, StringComparison.Ordinal);
        Assert.True(stamped.Length <= Constants.IssuedDeviceNameLimit, $"was {stamped.Length}");
    }

    [Fact]
    public void Both_at_once_still_leaves_a_marker_to_refuse_on()
    {
        var stamped = VouchController.StampVouchedBy(new string('a', 500), new string('u', 500));
        Assert.Contains(Constants.VouchedByMarker, stamped, StringComparison.Ordinal);
        Assert.True(stamped.Length <= Constants.IssuedDeviceNameLimit, $"was {stamped.Length}");
    }

    /// <summary>
    /// The round trip the refusal actually depends on: whatever we stamp, the
    /// check Approve makes must recognise it.
    /// </summary>
    [Theory]
    [InlineData("TV", "revam")]
    [InlineData("A name with (parentheses) in it", "revam")]
    [InlineData("", "revam")]
    [InlineData("TV", "")]
    public void A_stamped_name_is_recognised_as_vouched(string deviceName, string username)
        => Assert.Contains(
            Constants.VouchedByMarker,
            VouchController.StampVouchedBy(deviceName, username),
            StringComparison.Ordinal);
}
