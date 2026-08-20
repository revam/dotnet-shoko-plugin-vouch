using Newtonsoft.Json;

namespace Shoko.Plugin.Vouch.API.Models;

/// <summary>
/// Represents the outcome of approving or refusing a pairing request.
/// </summary>
/// <remarks>
/// Deliberately keyless. The approving device is a phone someone is holding
/// in a room, and a key rendered there outlives the flow in a screenshot —
/// so the key goes to the device that asked, over its own poll, and this
/// response says only what happened.
/// </remarks>
public class AnswerPairingResponse
{
    /// <summary>
    /// Gets the status of the request after the attempt: <c>approved</c>,
    /// <c>denied</c>, <c>expired</c>, <c>completed</c> or <c>unknown</c>.
    /// </summary>
    [JsonProperty("status")]
    public required string Status { get; init; }

    /// <summary>
    /// Gets the device name the answer applied to.
    /// </summary>
    [JsonProperty("deviceName")]
    public string? DeviceName { get; init; }

    /// <summary>
    /// Gets the user the answer was given as.
    /// </summary>
    [JsonProperty("username")]
    public string? Username { get; init; }

    /// <summary>
    /// Gets a sentence describing the outcome, safe to show as-is.
    /// </summary>
    [JsonProperty("message")]
    public string? Message { get; init; }
}
