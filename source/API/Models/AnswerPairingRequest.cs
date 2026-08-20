using Newtonsoft.Json;

namespace Shoko.Plugin.Vouch.API.Models;

/// <summary>
/// Represents a signed-in user answering a pairing request, either way.
/// </summary>
public class AnswerPairingRequest
{
    /// <summary>
    /// Gets the public code, as shown on the requesting device. Separators
    /// and casing are ignored.
    /// </summary>
    [JsonProperty("userCode")]
    public required string UserCode { get; init; }
}
