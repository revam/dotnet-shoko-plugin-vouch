using System;
using System.Threading;

namespace Shoko.Plugin.Vouch.Services;

/// <summary>
/// One pairing request, with everything about it that is not meant to leave
/// this assembly.
/// </summary>
/// <remarks>
/// State transitions are compare-and-swap rather than locked, because every
/// one of them is a single field moving between two known values and the
/// only thing that matters is that exactly one caller wins. That is the same
/// reason a request is single-use: two collectors racing for one key must
/// end with one key delivered, not two.
/// </remarks>
internal sealed class PairingEntry
{
    private int _state = (int)PairingState.Pending;

    /// <summary>The secret half, held only by the requesting device.</summary>
    public required string DeviceCode { get; init; }

    /// <summary>The public half, normalized (no separators, upper case).</summary>
    public required string UserCode { get; init; }

    /// <summary>The formatted public half, as displayed.</summary>
    public required string FormattedUserCode { get; init; }

    public required string DeviceName { get; init; }

    public string? DeviceType { get; init; }

    public string? UserAgent { get; init; }

    public required string RequestedFrom { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>
    /// When the request stops being answerable.
    ///
    /// Moved once, by approval, to <c>now + CollectionWindow</c>: the
    /// approver acted while it was pending, and a device that polls every
    /// few seconds must not lose a key that was minted for it a moment
    /// before the original deadline.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public PairingState State => (PairingState)Volatile.Read(ref _state);

    /// <summary>The issued key, set before the state moves to Approved.</summary>
    public string? ApiKey { get; private set; }

    public string? Username { get; private set; }

    public DateTime? ApiKeyExpiresAt { get; private set; }

    /// <summary>When the device last polled, for the slow-down rule.</summary>
    public DateTimeOffset? LastPolledAt { get; set; }

    /// <summary>
    /// The device's current poll interval. Grows when it polls early and
    /// never shrinks, so a device that ignores the interval is progressively
    /// asked to back off rather than cut off.
    /// </summary>
    public TimeSpan Interval { get; set; }

    /// <summary>
    /// Set when an expired-but-approved request has had its orphaned key
    /// revoked, so the sweep only revokes once.
    /// </summary>
    private int _revoked;

    public bool TryClaimForRevocation()
        => Interlocked.CompareExchange(ref _revoked, 1, 0) == 0;

    public bool TryBeginApproval()
        => Transition(PairingState.Pending, PairingState.Approving);

    public bool AbandonApproval()
        => Transition(PairingState.Approving, PairingState.Pending);

    /// <summary>
    /// Publish the minted key and move to Approved. The key is written
    /// before the state changes, so no poller can observe Approved without
    /// a key to collect.
    /// </summary>
    public bool CompleteApproval(string apiKey, string username, DateTime? apiKeyExpiresAt, DateTimeOffset collectBy)
    {
        if (State is not PairingState.Approving)
            return false;

        ApiKey = apiKey;
        Username = username;
        ApiKeyExpiresAt = apiKeyExpiresAt;
        ExpiresAt = collectBy;
        return Transition(PairingState.Approving, PairingState.Approved);
    }

    public bool TryDeny()
        => Transition(PairingState.Pending, PairingState.Denied);

    /// <summary>
    /// Hand the key over, exactly once. The winner gets the key and the
    /// request becomes Completed; everyone after gets Completed and nothing.
    /// </summary>
    public bool TryCollect(out string? apiKey, out string? username, out DateTime? apiKeyExpiresAt)
    {
        if (Transition(PairingState.Approved, PairingState.Completed))
        {
            apiKey = ApiKey;
            username = Username;
            apiKeyExpiresAt = ApiKeyExpiresAt;
            ApiKey = null;
            return true;
        }

        apiKey = null;
        username = null;
        apiKeyExpiresAt = null;
        return false;
    }

    public PairingRequest ToRequest()
        => new()
        {
            UserCode = FormattedUserCode,
            DeviceName = DeviceName,
            DeviceType = DeviceType,
            UserAgent = UserAgent,
            RequestedFrom = RequestedFrom,
            RequestedAt = RequestedAt,
            ExpiresAt = ExpiresAt,
        };

    private bool Transition(PairingState from, PairingState to)
        => Interlocked.CompareExchange(ref _state, (int)to, (int)from) == (int)from;
}
