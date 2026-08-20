using System;
using Newtonsoft.Json;

namespace Shoko.Plugin.Vouch.API.Models;

/// <summary>
/// Represents a pending pairing request, as shown to the person being asked
/// to approve it.
/// </summary>
/// <remarks>
/// Everything here exists so the confirmation can name what is asking.
/// Nothing here is a secret: no device code, and no key.
/// </remarks>
public class PendingPairingResponse
{
    /// <summary>
    /// Gets the public code, so the approver can check it against the one on
    /// the screen in front of them.
    /// </summary>
    [JsonProperty("userCode")]
    public required string UserCode { get; init; }

    /// <summary>
    /// Gets what the device calls itself, and what the issued key will be
    /// filed under.
    /// </summary>
    [JsonProperty("deviceName")]
    public required string DeviceName { get; init; }

    /// <summary>
    /// Gets the rough kind the device claimed, when it claimed one.
    /// </summary>
    [JsonProperty("deviceType")]
    public string? DeviceType { get; init; }

    /// <summary>
    /// Gets the requesting device's user agent, when it sent one.
    /// </summary>
    [JsonProperty("userAgent")]
    public string? UserAgent { get; init; }

    /// <summary>
    /// Gets the address the request came from — the last check available to
    /// someone who cannot see the other screen.
    /// </summary>
    [JsonProperty("requestedFrom")]
    public required string RequestedFrom { get; init; }

    /// <summary>
    /// Gets when the device asked.
    /// </summary>
    [JsonProperty("requestedAt")]
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>
    /// Gets when the request stops being answerable.
    /// </summary>
    [JsonProperty("expiresAt")]
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Gets whether this user already has a non-expiring key filed under
    /// this device name.
    ///
    /// Shoko hands back the existing key rather than minting a second one
    /// for the same user and device name, so approving this would give the
    /// requesting device the key another device is already using — and
    /// revoking one would sign both out. Worth saying out loud on the
    /// approval screen rather than discovering later.
    /// </summary>
    [JsonProperty("deviceNameInUse")]
    public bool DeviceNameInUse { get; init; }
}
