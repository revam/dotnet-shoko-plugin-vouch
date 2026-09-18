using Microsoft.Extensions.Logging.Abstractions;
using Shoko.Plugin.Vouch.Services;
using Xunit;

namespace Shoko.Plugin.Vouch.Tests;

/// <summary>
/// What the store accepts from a client, and how much of it.
/// </summary>
public class PairingInputTests
{
    private static PairingStore NewStore(TestTimeProvider clock)
        => new(NullLogger<PairingStore>.Instance, _ => Task.CompletedTask, clock);

    private static TestTimeProvider NewClock()
        => new(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("BCDFGHJK", "BCDFGHJK")]
    [InlineData("bcdfghjk", "BCDFGHJK")]
    [InlineData("BCDF-GHJK", "BCDFGHJK")]
    [InlineData("BCDF GHJK", "BCDFGHJK")]
    [InlineData("bcdf_ghjk", "BCDFGHJK")]
    public void A_code_is_read_the_way_a_person_would_type_it(string input, string expected)
        => Assert.Equal(expected, PairingStore.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("BCDFGHJ")]
    [InlineData("BCDFGHJKL")]
    [InlineData("BCDFGHJ!")]
    // Vowels are not in the alphabet, so no code ever spells a word — and
    // a code containing one was not one of ours.
    [InlineData("BCDAGHJK")]
    [InlineData("BCDFGHJK-BCDFGHJK-BCDFGHJK")]
    public void A_code_that_could_not_be_ours_is_refused(string input)
        => Assert.Null(PairingStore.Normalize(input));

    [Fact]
    public void A_null_code_is_refused()
        => Assert.Null(PairingStore.Normalize(null));

    [Theory]
    [InlineData("Living Room TV", "Living Room TV")]
    [InlineData("  Living Room TV  ", "Living Room TV")]
    public void A_device_name_is_kept_as_written(string input, string expected)
        => Assert.Equal(expected, PairingStore.NormalizeDeviceName(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    // Refused rather than stripped: the name is shown to the approver and
    // written to the log, and a name that can move the cursor is a name
    // that can rewrite what the approver reads.
    [InlineData("Living Room TV\nadmin")]
    [InlineData("Living\u0000Room")]
    public void A_device_name_that_is_not_a_name_is_refused(string input)
        => Assert.Null(PairingStore.NormalizeDeviceName(input));

    [Fact]
    public void A_device_name_has_a_length_the_token_list_can_show()
    {
        Assert.NotNull(PairingStore.NormalizeDeviceName(new string('x', 64)));
        Assert.Null(PairingStore.NormalizeDeviceName(new string('x', 65)));
    }

    [Fact]
    public void A_descriptor_is_cut_to_length_rather_than_refused()
    {
        // A user agent is nobody's carefully chosen label; truncating it
        // keeps the useful half instead of throwing the request away.
        var value = PairingStore.NormalizeDescriptor(new string('x', 400), PairingStore.UserAgentLimit);

        Assert.NotNull(value);
        Assert.Equal(PairingStore.UserAgentLimit, value!.Length);
    }

    [Fact]
    public void An_empty_descriptor_is_nothing_rather_than_an_empty_string()
    {
        Assert.Null(PairingStore.NormalizeDescriptor(null, 32));
        Assert.Null(PairingStore.NormalizeDescriptor("   ", 32));
    }

    [Fact]
    public void One_address_cannot_open_unlimited_requests()
    {
        var clock = NewClock();
        using var store = NewStore(clock);

        for (var i = 0; i < 20; i++)
        {
            Assert.True(store.CanCreate("10.0.0.5", out _));
            store.Create("TV", null, null, "10.0.0.5");
        }

        Assert.False(store.CanCreate("10.0.0.5", out var nextAllowedAt));
        Assert.NotNull(nextAllowedAt);

        // A limit on one address is not a limit on the house.
        Assert.True(store.CanCreate("10.0.0.6", out _));
    }

    [Fact]
    public void The_request_limit_lifts_when_the_window_passes()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        for (var i = 0; i < 20; i++)
            store.Create("TV", null, null, "10.0.0.5");

        Assert.False(store.CanCreate("10.0.0.5", out _));

        clock.Advance(TimeSpan.FromMinutes(15));

        Assert.True(store.CanCreate("10.0.0.5", out _));
    }
}
