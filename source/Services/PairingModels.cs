using System;

namespace Shoko.Plugin.Vouch.Services;

/// <summary>
/// The status of a pairing request as reported to a caller.
/// </summary>
/// <remarks>
/// Borrowed from RFC 8628 (the OAuth 2.0 Device Authorization Grant), which
/// has the same four outcomes for the same reason: a device that cannot be
/// typed on has to be able to tell "nobody has answered yet" from "someone
/// said no" from "you waited too long". A single "failed" would leave a
/// stuck device unable to say which of those happened, and the honest
/// message on screen differs for each.
/// </remarks>
public enum PairingStatus
{
    /// <summary>
    /// No such request. Either the code was never issued, or it was issued
    /// long enough ago that even the expired record has been swept away.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The request exists and is waiting for someone to approve or refuse it.
    /// </summary>
    Pending = 1,

    /// <summary>
    /// Someone approved the request and a key has been minted. The key is
    /// handed to the requesting device on its next poll, and to nobody else.
    /// </summary>
    Approved = 2,

    /// <summary>
    /// Someone refused the request. Terminal, and deliberately distinct from
    /// <see cref="Expired"/> — a refusal means "not this device", a timeout
    /// means "try again".
    /// </summary>
    Denied = 3,

    /// <summary>
    /// The request ran out of time before anyone answered, or it was approved
    /// and the requesting device never came back to collect the key.
    /// </summary>
    Expired = 4,

    /// <summary>
    /// The key was collected. Terminal, and reported without the key —
    /// a pairing request is single-use, so a device that lost the response
    /// has to start a new request rather than replay this one.
    /// </summary>
    Completed = 5,
}

/// <summary>
/// The internal state of a pairing request.
/// </summary>
/// <remarks>
/// One value wider than <see cref="PairingStatus"/>: <see cref="Approving"/>
/// exists because minting the key is an <c>await</c> against the host, and
/// the request has to be claimed before that call so two approvers racing
/// cannot both mint. It is reported to callers as
/// <see cref="PairingStatus.Pending"/>, because from outside nothing has
/// happened yet.
/// </remarks>
internal enum PairingState
{
    Pending = 0,
    Approving = 1,
    Approved = 2,
    Denied = 3,
    Completed = 4,
}

/// <summary>
/// A freshly created pairing request, as handed back to the device that
/// asked for it.
/// </summary>
/// <param name="DeviceCode">
/// The secret half. Held only by the requesting device, never displayed,
/// never in the QR code, and required to collect the key.
/// </param>
/// <param name="UserCode">
/// The public half, in <c>XXXX-XXXX</c> form. Displayed on the requesting
/// device and carried by the QR code. It names a request; it is not a
/// credential and cannot be exchanged for one.
/// </param>
/// <param name="ExpiresAt">When the request stops being answerable.</param>
/// <param name="Interval">How often the device should poll.</param>
public sealed record NewPairing(
    string DeviceCode,
    string UserCode,
    DateTimeOffset ExpiresAt,
    TimeSpan Interval
);

/// <summary>
/// What a pairing request looks like to the person being asked to approve
/// it. Everything here is meant to be shown on the approval screen, and
/// nothing here is a secret — no device code, and no key.
/// </summary>
public sealed record PairingRequest
{
    /// <summary>The public code, as displayed on the requesting device.</summary>
    public required string UserCode { get; init; }

    /// <summary>
    /// What the device calls itself. This is the name the issued key is
    /// filed under, so it is what someone revoking it later will see.
    /// </summary>
    public required string DeviceName { get; init; }

    /// <summary>A rough kind — "TV", "Console", "Phone" — when the device offered one.</summary>
    public string? DeviceType { get; init; }

    /// <summary>The requesting device's user agent, when it sent one.</summary>
    public string? UserAgent { get; init; }

    /// <summary>The address the request came from, for a person to sanity-check.</summary>
    public required string RequestedFrom { get; init; }

    /// <summary>When the device asked.</summary>
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>When the request stops being answerable.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// A claim on a pending request, taken before the key is minted and
/// surrendered afterwards.
/// </summary>
/// <remarks>
/// Carries no key of its own. The controller mints against the host with
/// this in hand, then calls <see cref="PairingStore.CompleteApproval"/>, or
/// <see cref="PairingStore.AbandonApproval"/> if the mint failed.
/// </remarks>
public sealed class ApprovalTicket
{
    internal ApprovalTicket(PairingEntry entry)
    {
        Entry = entry;
    }

    internal PairingEntry Entry { get; }

    /// <summary>The request being approved, for logging and for the response.</summary>
    public PairingRequest Request => Entry.ToRequest();
}

/// <summary>
/// The answer to a poll from the requesting device.
/// </summary>
/// <param name="Status">Which of the outcomes this is.</param>
/// <param name="SlowDown">
/// The device polled sooner than <paramref name="Interval"/> allows. Only
/// ever set alongside <see cref="PairingStatus.Pending"/>: a terminal answer
/// is always delivered, because rate limiting a refusal into silence would
/// turn a refusal into a timeout.
/// </param>
/// <param name="Interval">
/// How long to wait before polling again. Grows by five seconds each time
/// the device polls early, exactly as RFC 8628's <c>slow_down</c> does.
/// </param>
/// <param name="ExpiresAt">When the request stops being answerable, when it still exists.</param>
/// <param name="ApiKey">
/// The issued key. Non-null exactly once, on the poll that collects it.
/// </param>
/// <param name="Username">Who approved, so the device can say who it is signed in as.</param>
/// <param name="DeviceName">The name the key is filed under.</param>
/// <param name="ApiKeyExpiresAt">When the issued key expires, when it does.</param>
public sealed record PollResult(
    PairingStatus Status,
    bool SlowDown,
    TimeSpan Interval,
    DateTimeOffset? ExpiresAt,
    string? ApiKey = null,
    string? Username = null,
    string? DeviceName = null,
    DateTime? ApiKeyExpiresAt = null
);
