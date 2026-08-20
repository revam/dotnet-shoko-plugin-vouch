using System;
using Newtonsoft.Json;

namespace Shoko.Plugin.Vouch.API.Models;

/// <summary>
/// Represents a plain response from the Vouch plugin API, used where there
/// is nothing to return but whether it worked and why not.
/// </summary>
public class VouchResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether the operation was successful.
    /// </summary>
    [JsonProperty("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the response message.
    /// </summary>
    [JsonProperty("message")]
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the client can retry the request.
    /// </summary>
    [JsonProperty("retryAfter")]
    public DateTimeOffset? RetryAfter { get; set; }
}
