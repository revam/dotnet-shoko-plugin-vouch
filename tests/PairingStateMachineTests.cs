using Microsoft.Extensions.Logging.Abstractions;
using Shoko.Plugin.Vouch.Services;
using Xunit;

namespace Shoko.Plugin.Vouch.Tests;

/// <summary>
/// The four outcomes, and the transitions between them.
/// </summary>
/// <remarks>
/// This is the part of the plugin worth testing hardest. A device being
/// paired has no keyboard and no error console; the only thing it can tell
/// the person standing in front of it is which of <em>waiting</em>,
/// <em>refused</em>, <em>too slow</em> and <em>no such code</em> happened,
/// and it can only do that if the server keeps them apart. Every test below
/// is one pair of those that must not collapse into each other.
/// </remarks>
public class PairingStateMachineTests
{
    private const string DeviceIp = "10.0.0.5";

    private static PairingStore NewStore(TestTimeProvider clock, List<string>? revoked = null)
        => new(NullLogger<PairingStore>.Instance, key =>
        {
            revoked?.Add(key);
            return Task.CompletedTask;
        }, clock);

    private static TestTimeProvider NewClock()
        => new(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));

    private static NewPairing Request(PairingStore store, string deviceName = "Living Room TV")
        => store.Create(deviceName, "TV", "TV/1.0", DeviceIp);

    // ──────────────────────────────────────────────
    //  Pending
    // ──────────────────────────────────────────────

    [Fact]
    public void A_fresh_request_polls_as_pending()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);

        var result = store.Poll(pairing.DeviceCode);

        Assert.Equal(PairingStatus.Pending, result.Status);
        Assert.Null(result.ApiKey);
        Assert.False(result.SlowDown);
    }

    [Fact]
    public void The_public_code_is_eight_letters_from_the_unambiguous_alphabet()
    {
        var clock = NewClock();
        using var store = NewStore(clock);

        var pairing = Request(store);

        Assert.Equal(9, pairing.UserCode.Length);
        Assert.Equal('-', pairing.UserCode[4]);
        Assert.All(pairing.UserCode.Replace("-", string.Empty), character =>
            Assert.Contains(character, "BCDFGHJKLMNPQRSTVWXZ"));
    }

    [Fact]
    public void The_secret_half_is_not_the_public_half()
    {
        var clock = NewClock();
        using var store = NewStore(clock);

        var pairing = Request(store);

        Assert.Equal(64, pairing.DeviceCode.Length);
        Assert.DoesNotContain(pairing.UserCode.Replace("-", string.Empty), pairing.DeviceCode, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────
    //  Approved
    // ──────────────────────────────────────────────

    [Fact]
    public void An_approved_request_hands_the_key_to_the_polling_device()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);

        Assert.Equal(PairingStatus.Pending, store.BeginApproval(pairing.UserCode, out var ticket));
        store.CompleteApproval(ticket!, "key-1", "alice", null);

        var result = store.Poll(pairing.DeviceCode);

        Assert.Equal(PairingStatus.Approved, result.Status);
        Assert.Equal("key-1", result.ApiKey);
        Assert.Equal("alice", result.Username);
        Assert.Equal("Living Room TV", result.DeviceName);
    }

    [Fact]
    public void The_key_is_handed_over_exactly_once()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);
        store.BeginApproval(pairing.UserCode, out var ticket);
        store.CompleteApproval(ticket!, "key-1", "alice", null);

        var first = store.Poll(pairing.DeviceCode);
        var second = store.Poll(pairing.DeviceCode);

        Assert.Equal(PairingStatus.Approved, first.Status);
        Assert.Equal("key-1", first.ApiKey);
        Assert.Equal(PairingStatus.Completed, second.Status);
        Assert.Null(second.ApiKey);
    }

    [Fact]
    public void Only_one_of_two_racing_approvals_gets_to_mint()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);

        var first = store.BeginApproval(pairing.UserCode, out var firstTicket);
        var second = store.BeginApproval(pairing.UserCode, out var secondTicket);

        Assert.Equal(PairingStatus.Pending, first);
        Assert.NotNull(firstTicket);
        // The second approver is told the request is no longer theirs to
        // answer rather than being allowed to mint a second key.
        Assert.Equal(PairingStatus.Pending, second);
        Assert.Null(secondTicket);
    }

    [Fact]
    public void A_claim_given_back_leaves_the_request_answerable()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);
        store.BeginApproval(pairing.UserCode, out var ticket);

        store.AbandonApproval(ticket!);

        Assert.Equal(PairingStatus.Pending, store.Poll(pairing.DeviceCode).Status);
        Assert.Equal(PairingStatus.Pending, store.BeginApproval(pairing.UserCode, out var again));
        Assert.NotNull(again);
    }

    [Fact]
    public void Approval_extends_the_deadline_so_a_late_approval_is_still_collectable()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);

        // One second before the request would have expired.
        clock.Advance(PairingStore.RequestLifetime - TimeSpan.FromSeconds(1));
        store.BeginApproval(pairing.UserCode, out var ticket);
        store.CompleteApproval(ticket!, "key-1", "alice", null);

        // Past the original deadline, inside the collection window.
        clock.Advance(TimeSpan.FromSeconds(30));

        var result = store.Poll(pairing.DeviceCode);

        Assert.Equal(PairingStatus.Approved, result.Status);
        Assert.Equal("key-1", result.ApiKey);
    }

    // ──────────────────────────────────────────────
    //  Denied — and the reason this is not "expired"
    // ──────────────────────────────────────────────

    [Fact]
    public void A_refusal_reaches_the_polling_device_as_a_refusal()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);

        Assert.Equal(PairingStatus.Denied, store.Deny(pairing.UserCode, "alice"));

        var result = store.Poll(pairing.DeviceCode);

        Assert.Equal(PairingStatus.Denied, result.Status);
        Assert.Null(result.ApiKey);
    }

    [Fact]
    public void A_refusal_stays_a_refusal_after_the_deadline_passes()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);
        store.Deny(pairing.UserCode, "alice");

        clock.Advance(PairingStore.RequestLifetime + TimeSpan.FromMinutes(1));

        // "Someone said no" and "you waited too long" are different
        // sentences on the device's screen, and the first one is true.
        Assert.Equal(PairingStatus.Denied, store.Poll(pairing.DeviceCode).Status);
    }

    [Fact]
    public void A_refused_request_cannot_then_be_approved()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);
        store.Deny(pairing.UserCode, "alice");

        var status = store.BeginApproval(pairing.UserCode, out var ticket);

        Assert.Equal(PairingStatus.Denied, status);
        Assert.Null(ticket);
    }

    [Fact]
    public void Refusing_twice_is_still_a_refusal()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);

        Assert.Equal(PairingStatus.Denied, store.Deny(pairing.UserCode, "alice"));
        Assert.Equal(PairingStatus.Denied, store.Deny(pairing.UserCode, "bob"));
    }

    [Fact]
    public void An_approved_request_cannot_then_be_refused()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);
        store.BeginApproval(pairing.UserCode, out var ticket);
        store.CompleteApproval(ticket!, "key-1", "alice", null);

        Assert.Equal(PairingStatus.Approved, store.Deny(pairing.UserCode, "bob"));
        Assert.Equal("key-1", store.Poll(pairing.DeviceCode).ApiKey);
    }

    // ──────────────────────────────────────────────
    //  Expired
    // ──────────────────────────────────────────────

    [Fact]
    public void A_request_nobody_answered_expires()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);

        clock.Advance(PairingStore.RequestLifetime);

        Assert.Equal(PairingStatus.Expired, store.Poll(pairing.DeviceCode).Status);
    }

    [Fact]
    public void An_expired_request_cannot_be_approved_or_refused()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);

        clock.Advance(PairingStore.RequestLifetime);

        Assert.Equal(PairingStatus.Expired, store.BeginApproval(pairing.UserCode, out var ticket));
        Assert.Null(ticket);
        Assert.Equal(PairingStatus.Expired, store.Deny(pairing.UserCode, "alice"));
    }

    [Fact]
    public void An_expired_request_does_not_answer_a_lookup()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);

        clock.Advance(PairingStore.RequestLifetime);

        Assert.Equal(PairingStatus.Expired, store.Lookup(pairing.UserCode, out var request));
        Assert.Null(request);
    }

    [Fact]
    public void An_approval_nobody_collected_expires_and_the_key_is_revoked()
    {
        var clock = NewClock();
        var revoked = new List<string>();
        using var store = NewStore(clock, revoked);
        var pairing = Request(store);
        store.BeginApproval(pairing.UserCode, out var ticket);
        store.CompleteApproval(ticket!, "key-1", "alice", null);

        clock.Advance(PairingStore.CollectionWindow + TimeSpan.FromSeconds(1));

        Assert.Equal(PairingStatus.Expired, store.Poll(pairing.DeviceCode).Status);
        Assert.Null(store.Poll(pairing.DeviceCode).ApiKey);

        store.RunCleanup();
        SpinUntil(() => revoked.Count > 0);

        // A key minted for a device that never came back would otherwise sit
        // in the user's token list forever, issued and unclaimed.
        Assert.Equal(["key-1"], revoked);
    }

    [Fact]
    public void An_uncollected_key_is_revoked_only_once()
    {
        var clock = NewClock();
        var revoked = new List<string>();
        using var store = NewStore(clock, revoked);
        var pairing = Request(store);
        store.BeginApproval(pairing.UserCode, out var ticket);
        store.CompleteApproval(ticket!, "key-1", "alice", null);

        clock.Advance(PairingStore.CollectionWindow + TimeSpan.FromSeconds(1));
        store.RunCleanup();
        SpinUntil(() => revoked.Count > 0);
        store.RunCleanup();

        Assert.Single(revoked);
    }

    [Fact]
    public void A_collected_key_is_never_revoked()
    {
        var clock = NewClock();
        var revoked = new List<string>();
        using var store = NewStore(clock, revoked);
        var pairing = Request(store);
        store.BeginApproval(pairing.UserCode, out var ticket);
        store.CompleteApproval(ticket!, "key-1", "alice", null);
        store.Poll(pairing.DeviceCode);

        clock.Advance(PairingStore.CollectionWindow + PairingStore.Retention);
        store.RunCleanup();

        Assert.Empty(revoked);
    }

    // ──────────────────────────────────────────────
    //  Unknown
    // ──────────────────────────────────────────────

    [Fact]
    public void A_code_that_was_never_issued_is_unknown()
    {
        var clock = NewClock();
        using var store = NewStore(clock);

        Assert.Equal(PairingStatus.Unknown, store.Poll(new string('a', 64)).Status);
        Assert.Equal(PairingStatus.Unknown, store.Lookup("BCDF-GHJK", out _));
    }

    [Fact]
    public void An_expired_request_is_remembered_before_it_becomes_unknown()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);

        clock.Advance(PairingStore.RequestLifetime + TimeSpan.FromMinutes(1));
        store.RunCleanup();

        // Still inside the retention window: a device that stopped polling
        // for a minute learns it was too slow rather than that its request
        // never existed.
        Assert.Equal(PairingStatus.Expired, store.Poll(pairing.DeviceCode).Status);

        clock.Advance(PairingStore.Retention);
        store.RunCleanup();

        Assert.Equal(PairingStatus.Unknown, store.Poll(pairing.DeviceCode).Status);
    }

    // ──────────────────────────────────────────────
    //  Polling discipline
    // ──────────────────────────────────────────────

    [Fact]
    public void Polling_early_asks_the_device_to_slow_down()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);

        var first = store.Poll(pairing.DeviceCode);
        var second = store.Poll(pairing.DeviceCode);

        Assert.False(first.SlowDown);
        Assert.True(second.SlowDown);
        Assert.Equal(PairingStatus.Pending, second.Status);
        Assert.Equal(first.Interval + PairingStore.SlowDownIncrement, second.Interval);
    }

    [Fact]
    public void Polling_on_time_does_not()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);

        store.Poll(pairing.DeviceCode);
        clock.Advance(PairingStore.PollInterval);
        var second = store.Poll(pairing.DeviceCode);

        Assert.False(second.SlowDown);
        Assert.Equal(PairingStore.PollInterval, second.Interval);
    }

    [Fact]
    public void The_interval_stops_growing_at_the_cap()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);

        PollResult result;
        do
        {
            result = store.Poll(pairing.DeviceCode);
        }
        while (result.Interval < PairingStore.MaxPollInterval);

        Assert.Equal(PairingStore.MaxPollInterval, store.Poll(pairing.DeviceCode).Interval);
    }

    [Fact]
    public void A_terminal_answer_is_never_withheld_for_polling_too_fast()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);

        store.Poll(pairing.DeviceCode);
        store.Deny(pairing.UserCode, "alice");

        // Same instant as the last poll, so the slow-down rule would fire
        // if it applied. Withholding a refusal for eagerness would turn it
        // into a timeout on the device's screen.
        var result = store.Poll(pairing.DeviceCode);

        Assert.Equal(PairingStatus.Denied, result.Status);
        Assert.False(result.SlowDown);
    }

    [Fact]
    public void An_approval_is_never_withheld_for_polling_too_fast()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);
        store.Poll(pairing.DeviceCode);
        store.BeginApproval(pairing.UserCode, out var ticket);
        store.CompleteApproval(ticket!, "key-1", "alice", null);

        var result = store.Poll(pairing.DeviceCode);

        Assert.Equal(PairingStatus.Approved, result.Status);
        Assert.Equal("key-1", result.ApiKey);
        Assert.False(result.SlowDown);
    }

    // ──────────────────────────────────────────────
    //  What the approver is shown
    // ──────────────────────────────────────────────

    [Fact]
    public void A_lookup_names_what_is_asking()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = store.Create("Kitchen Tablet", "Tablet", "Tablet/2.0", DeviceIp);

        Assert.Equal(PairingStatus.Pending, store.Lookup(pairing.UserCode, out var request));
        Assert.NotNull(request);
        Assert.Equal("Kitchen Tablet", request!.DeviceName);
        Assert.Equal("Tablet", request.DeviceType);
        Assert.Equal("Tablet/2.0", request.UserAgent);
        Assert.Equal(DeviceIp, request.RequestedFrom);
        Assert.Equal(pairing.UserCode, request.UserCode);
    }

    [Fact]
    public void The_key_is_filed_under_the_name_the_approver_was_shown()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = store.Create("Bedroom TV", null, null, DeviceIp);

        store.Lookup(pairing.UserCode, out var shown);
        store.BeginApproval(pairing.UserCode, out var ticket);

        // The approval path reads the name from the request rather than
        // from whatever the approving call carried, so what is issued is
        // what was displayed.
        Assert.Equal(shown!.DeviceName, ticket!.Request.DeviceName);
        Assert.Equal("Bedroom TV", ticket.Request.DeviceName);
    }

    [Fact]
    public void The_public_code_is_read_back_in_any_spacing_or_casing()
    {
        var clock = NewClock();
        using var store = NewStore(clock);
        var pairing = Request(store);
        var bare = pairing.UserCode.Replace("-", string.Empty);

        Assert.Equal(PairingStatus.Pending, store.Lookup(bare.ToLowerInvariant(), out _));
        Assert.Equal(PairingStatus.Pending, store.Lookup(bare[..4] + " " + bare[4..], out _));
        Assert.Equal(PairingStatus.Pending, store.Lookup(pairing.UserCode, out _));
    }

    private static void SpinUntil(Func<bool> condition)
    {
        // The revoke is fired and forgotten by design — the sweep runs on a
        // timer with nobody waiting on it — so a test that wants to see the
        // effect has to wait for it.
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)));
    }
}
