using Newtonsoft.Json;

namespace Shoko.Plugin.Vouch.API.Models;

/// <summary>
/// Represents what a client learns by probing for this plugin.
/// </summary>
/// <remarks>
/// Anonymous and cheap on purpose: a front-end offers the "show me a code
/// instead" path when this answers and the ordinary sign-in form when it
/// does not, which is what it would have shown anyway.
/// </remarks>
public class AvailableResponse
{
    /// <summary>
    /// Gets the plugin version.
    /// </summary>
    [JsonProperty("version")]
    public required string Version { get; init; }

    /// <summary>
    /// Gets the path a second device visits to answer a request. Absolute
    /// URLs are handed out per-request by <c>Request</c>; this is here so a
    /// client can describe the flow before starting one.
    /// </summary>
    [JsonProperty("verificationPath")]
    public required string VerificationPath { get; init; }

    /// <summary>
    /// Gets how many characters a public code has, excluding the separator.
    /// </summary>
    [JsonProperty("userCodeLength")]
    public required int UserCodeLength { get; init; }

    /// <summary>
    /// Gets how long a request stays answerable, in seconds.
    /// </summary>
    [JsonProperty("requestLifetimeSeconds")]
    public required int RequestLifetimeSeconds { get; init; }

    /// <summary>
    /// Gets the starting poll interval, in seconds.
    /// </summary>
    [JsonProperty("pollIntervalSeconds")]
    public required int PollIntervalSeconds { get; init; }
}
