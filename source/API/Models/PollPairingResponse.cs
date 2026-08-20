using System;
using Newtonsoft.Json;

namespace Shoko.Plugin.Vouch.API.Models;

/// <summary>
/// Represents the answer to a poll from a requesting device.
/// </summary>
/// <remarks>
/// <see cref="Status"/> is the whole point of the shape: a device that
/// cannot be typed on has to be able to say which of four things happened,
/// because the sentence it puts on screen differs for each — wait, someone
/// refused, you took too long, or this request is gone entirely.
/// </remarks>
public class PollPairingResponse
{
    /// <summary>
    /// Gets the status: <c>pending</c>, <c>approved</c>, <c>denied</c>,
    /// <c>expired</c>, <c>completed</c> or <c>unknown</c>.
    /// </summary>
    [JsonProperty("status")]
    public required string Status { get; init; }

    /// <summary>
    /// Gets the issued API key. Present on exactly one response — the poll
    /// that collects it — and never anywhere else in this API.
    /// </summary>
    [JsonProperty("apikey")]
    public string? ApiKey { get; init; }

    /// <summary>
    /// Gets the user the key belongs to, so the device can say who it is
    /// signed in as without a second call.
    /// </summary>
    [JsonProperty("username")]
    public string? Username { get; init; }

    /// <summary>
    /// Gets the device name the key is filed under. The same name that was
    /// shown to the approver, and the one to look for when revoking it.
    /// </summary>
    [JsonProperty("deviceName")]
    public string? DeviceName { get; init; }

    /// <summary>
    /// Gets when the issued key expires, when it does at all.
    /// </summary>
    [JsonProperty("apikeyExpiresAt")]
    public DateTime? ApiKeyExpiresAt { get; init; }

    /// <summary>
    /// Gets when the request stops being answerable, while it still exists.
    /// </summary>
    [JsonProperty("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Gets how many seconds to wait before polling again. Grows when the
    /// device polls early.
    /// </summary>
    [JsonProperty("interval")]
    public required int Interval { get; init; }

    /// <summary>
    /// Gets whether the device polled early and should slow down. Only ever
    /// set alongside <c>pending</c> — a terminal answer is always delivered.
    /// </summary>
    [JsonProperty("slowDown")]
    public bool SlowDown { get; init; }

    /// <summary>
    /// Gets a sentence describing the status, safe to show as-is.
    /// </summary>
    [JsonProperty("message")]
    public string? Message { get; init; }
}
