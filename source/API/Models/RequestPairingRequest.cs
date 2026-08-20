using Newtonsoft.Json;

namespace Shoko.Plugin.Vouch.API.Models;

/// <summary>
/// Represents a request from a device asking to be paired.
/// </summary>
public class RequestPairingRequest
{
    /// <summary>
    /// Gets what the device calls itself.
    ///
    /// This is shown to the person approving and stored as the device name
    /// on the issued key, so it is also what someone reads in the token list
    /// later when deciding whether to revoke it. Name the thing in front of
    /// the user — "Living Room TV" — rather than the software.
    /// </summary>
    [JsonProperty("deviceName")]
    public required string DeviceName { get; init; }

    /// <summary>
    /// Gets an optional rough kind — "TV", "Console", "Phone", "Desktop" —
    /// shown beside the name so the approver can check it against what is
    /// actually in front of them.
    /// </summary>
    [JsonProperty("deviceType")]
    public string? DeviceType { get; init; }
}
