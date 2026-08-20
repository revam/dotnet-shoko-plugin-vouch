using System;
using Newtonsoft.Json;

namespace Shoko.Plugin.Vouch.API.Models;

/// <summary>
/// Represents an opened pairing request, as handed back to the device that
/// asked for it.
/// </summary>
public class RequestPairingResponse
{
    /// <summary>
    /// Gets the secret half. Keep it in memory, send it with every poll,
    /// and never render it — not on screen, not in the QR code.
    /// </summary>
    [JsonProperty("deviceCode")]
    public required string DeviceCode { get; init; }

    /// <summary>
    /// Gets the public half, in <c>XXXX-XXXX</c> form. Show this as text
    /// beside the QR code so a device with no camera in front of it can
    /// still be paired by typing.
    /// </summary>
    [JsonProperty("userCode")]
    public required string UserCode { get; init; }

    /// <summary>
    /// Gets the address a second device visits to answer this request.
    /// </summary>
    [JsonProperty("verificationUri")]
    public required string VerificationUri { get; init; }

    /// <summary>
    /// Gets the same address with the code already in it. This is what the
    /// QR code should encode; the QR is an affordance over this URL and
    /// nothing more.
    /// </summary>
    [JsonProperty("verificationUriComplete")]
    public required string VerificationUriComplete { get; init; }

    /// <summary>
    /// Gets when the request stops being answerable.
    /// </summary>
    [JsonProperty("expiresAt")]
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Gets how many seconds from now that is, for a client that would
    /// rather not trust its own clock.
    /// </summary>
    [JsonProperty("expiresIn")]
    public required int ExpiresIn { get; init; }

    /// <summary>
    /// Gets how many seconds to wait between polls.
    /// </summary>
    [JsonProperty("interval")]
    public required int Interval { get; init; }
}
