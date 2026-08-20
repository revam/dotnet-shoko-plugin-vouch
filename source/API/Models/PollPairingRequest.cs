using Newtonsoft.Json;

namespace Shoko.Plugin.Vouch.API.Models;

/// <summary>
/// Represents a requesting device asking after its own pairing request.
/// </summary>
public class PollPairingRequest
{
    /// <summary>
    /// Gets the secret half of the request, as issued.
    ///
    /// Never displayed and never part of the QR code. Holding it is what
    /// makes a caller the device that asked, and it is the only thing that
    /// can collect the key.
    /// </summary>
    [JsonProperty("deviceCode")]
    public required string DeviceCode { get; init; }
}
