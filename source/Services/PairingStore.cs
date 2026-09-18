using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Shoko.Plugin.Vouch.Services;

/// <summary>
/// The pairing handshake, and the only thing in this plugin that holds
/// state.
/// </summary>
/// <remarks>
/// <para>
/// Everything lives in memory and dies with the process, which is the right
/// lifetime for a record that is answerable for five minutes. A restart
/// drops pending requests; a device that was polling one learns
/// <see cref="PairingStatus.Unknown"/> and starts again, which is the same
/// thing it would have done had the request expired.
/// </para>
/// <para>
/// The store never mints and never revokes on its own — it is handed a key
/// to publish, and it is given a revoke callback for the one case it has to
/// clean up after itself: a key minted for a device that then never came
/// back for it. That keeps the state machine free of the host and testable
/// without one.
/// </para>
/// </remarks>
public sealed class PairingStore : IDisposable
{
    /// <summary>
    /// How long a request stays answerable. Short on purpose: the public
    /// code is readable by any camera in the room and by a photograph taken
    /// later, so what matters is that a photograph stops being worth
    /// anything in minutes.
    /// </summary>
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long the requesting device has to collect a key after approval.
    /// Approval moves the deadline here rather than leaving the original
    /// one, so a request approved in its last second is still collectable
    /// by a device polling every few seconds.
    /// </summary>
    public static readonly TimeSpan CollectionWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a finished or expired request is kept before it is swept.
    ///
    /// It is kept at all so that a device that stopped polling for a while
    /// is told <em>expired</em> or <em>denied</em> rather than
    /// <em>unknown</em>. Telling those apart is the whole reason this has a
    /// state machine.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    /// <summary>How often a device should poll.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How much a device's interval grows each time it polls early. RFC
    /// 8628's <c>slow_down</c> uses five seconds and there is no reason to
    /// pick a different number.
    /// </summary>
    public static readonly TimeSpan SlowDownIncrement = TimeSpan.FromSeconds(5);

    /// <summary>The interval stops growing here.</summary>
    public static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(15);

    private const int MaxRequestsPerIp = 20;

    /// <summary>
    /// The public code's alphabet: twenty consonants, no vowels and no
    /// character that can be read as another one. No vowels means no code
    /// ever spells a word, which matters because these are read aloud
    /// across a room.
    /// </summary>
    private const string UserCodeAlphabet = "BCDFGHJKLMNPQRSTVWXZ";

    /// <summary>
    /// Eight characters from a twenty-character alphabet — about 34.5 bits,
    /// or one in 2.5×10¹⁰. Guessing it is bounded further by the host's
    /// shared authentication lockout, which counts every code that matched
    /// nothing, and by the code being answerable for five minutes; and a
    /// guessed code still cannot produce a key, because guessing it is not
    /// being signed in.
    /// </summary>
    public const int UserCodeLength = 8;

    /// <summary>
    /// The secret half, 256 bits of hex. It is never displayed, never in the
    /// QR code, and never returned by any endpoint the approver touches, so
    /// it needs no shape a human can read.
    /// </summary>
    private const int DeviceCodeLength = 64;

    private const int MaxDeviceNameLength = 64;

    private const int MaxDeviceTypeLength = 32;

    private const int MaxUserAgentLength = 256;

    /// <summary>Eight characters, one separator, and slack for a paste.</summary>
    private const int MaxUserCodeInputLength = 24;

    private readonly ConcurrentDictionary<string, PairingEntry> _byDeviceCode = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, PairingEntry> _byUserCode = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, WindowEntry> _requestsPerIp = new(StringComparer.Ordinal);

    private readonly ILogger<PairingStore> _logger;

    private readonly Func<string, Task> _revokeApiKey;

    private readonly TimeProvider _timeProvider;

    private readonly ITimer _cleanupTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="PairingStore"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="revokeApiKey">
    /// Revokes a key the store minted through someone else and then had to
    /// throw away — an approval nobody collected. The store is handed this
    /// rather than the user service so that the state machine can be tested
    /// without a host.
    /// </param>
    /// <param name="timeProvider">
    /// The clock. Every expiry decision is made against this at read time
    /// rather than by the sweep, so time can be moved in a test without
    /// waiting for one.
    /// </param>
    public PairingStore(ILogger<PairingStore> logger, Func<string, Task> revokeApiKey, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _revokeApiKey = revokeApiKey;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cleanupTimer = _timeProvider.CreateTimer(_ => RunCleanup(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }

    private DateTimeOffset Now => _timeProvider.GetUtcNow();

    // ──────────────────────────────────────────────
    //  The requesting device
    // ──────────────────────────────────────────────

    /// <summary>
    /// Checks whether the given address may open another pairing request.
    /// </summary>
    /// <remarks>
    /// A quota on requests that succeeded, not a counter of attempts that
    /// failed, which is why it is the store's own and not the host's
    /// authentication lockout. It is what bounds how much memory one
    /// address can make the store hold; nothing here has failed, so there
    /// is nothing to register against the caller.
    /// </remarks>
    /// <param name="clientIp">The requesting address.</param>
    /// <param name="nextAllowedAt">When set, when the next request is allowed.</param>
    /// <returns><c>true</c> when the request is allowed; otherwise, <c>false</c>.</returns>
    public bool CanCreate(string clientIp, out DateTimeOffset? nextAllowedAt)
        => IsUnderLimit(clientIp, MaxRequestsPerIp, out nextAllowedAt);

    /// <summary>
    /// Opens a pairing request and returns both halves of it.
    /// </summary>
    /// <param name="deviceName">
    /// What the device calls itself. This becomes the device name on the
    /// issued key, so it is what someone revoking that key later reads.
    /// </param>
    /// <param name="deviceType">An optional rough kind, shown to the approver.</param>
    /// <param name="userAgent">The device's user agent, shown to the approver.</param>
    /// <param name="clientIp">The requesting address, shown to the approver.</param>
    /// <returns>The new request.</returns>
    public NewPairing Create(string deviceName, string? deviceType, string? userAgent, string clientIp)
    {
        var now = Now;
        var deviceCode = RandomNumberGenerator.GetHexString(DeviceCodeLength, lowercase: true);

        // A collision would hand one device another device's request, so a
        // taken code is regenerated rather than overwritten. At twenty to
        // the eighth this loop effectively never runs twice.
        PairingEntry entry;
        while (true)
        {
            var userCode = GenerateUserCode();
            entry = new PairingEntry
            {
                DeviceCode = deviceCode,
                UserCode = userCode,
                FormattedUserCode = Format(userCode),
                DeviceName = deviceName,
                DeviceType = deviceType,
                UserAgent = userAgent,
                RequestedFrom = clientIp,
                RequestedAt = now,
                ExpiresAt = now + RequestLifetime,
                Interval = PollInterval,
            };
            if (_byUserCode.TryAdd(userCode, entry))
                break;
        }

        _byDeviceCode[deviceCode] = entry;
        RecordUse(clientIp);

        _logger.LogInformation(
            "Pairing requested by {DeviceName} ({DeviceType}) from {ClientIP}; code {UserCode} expires at {ExpiresAt}.",
            entry.DeviceName, entry.DeviceType ?? "unknown kind", clientIp, entry.FormattedUserCode, entry.ExpiresAt);

        return new NewPairing(entry.DeviceCode, entry.FormattedUserCode, entry.ExpiresAt, entry.Interval);
    }

    /// <summary>
    /// Answers a poll from the requesting device, and hands over the key on
    /// the one poll that finds an approval waiting.
    /// </summary>
    /// <param name="deviceCode">The secret half, as issued.</param>
    /// <returns>What the device should do next.</returns>
    public PollResult Poll(string deviceCode)
    {
        if (deviceCode.Length != DeviceCodeLength || !_byDeviceCode.TryGetValue(deviceCode, out var entry))
            return new PollResult(PairingStatus.Unknown, false, PollInterval, null);

        var now = Now;
        var status = Resolve(entry, now);

        // Early polls are told to back off, but only while there is nothing
        // to report. A refusal or an expiry withheld for being asked for too
        // eagerly would reach the device as silence, which is the one thing
        // this state machine exists to avoid.
        var slowDown = false;
        if (status is PairingStatus.Pending)
        {
            if (entry.LastPolledAt is { } last && now - last < entry.Interval)
            {
                slowDown = true;
                var grown = entry.Interval + SlowDownIncrement;
                entry.Interval = grown > MaxPollInterval ? MaxPollInterval : grown;
            }

            entry.LastPolledAt = now;
            return new PollResult(PairingStatus.Pending, slowDown, entry.Interval, entry.ExpiresAt);
        }

        entry.LastPolledAt = now;

        if (status is not PairingStatus.Approved)
            return new PollResult(status, false, entry.Interval, status is PairingStatus.Unknown ? null : entry.ExpiresAt);

        if (!entry.TryCollect(out var apiKey, out var username, out var apiKeyExpiresAt) || apiKey is null)
            return new PollResult(PairingStatus.Completed, false, entry.Interval, entry.ExpiresAt);

        _logger.LogWarning(
            "Pairing key collected by {DeviceName} from {ClientIP} for user {Username} (code {UserCode}).",
            entry.DeviceName, entry.RequestedFrom, username, entry.FormattedUserCode);

        return new PollResult(
            PairingStatus.Approved, false, entry.Interval, entry.ExpiresAt,
            apiKey, username, entry.DeviceName, apiKeyExpiresAt);
    }

    // ──────────────────────────────────────────────
    //  The approving device
    // ──────────────────────────────────────────────

    /// <summary>
    /// Resolves a public code to the request it names, for the approval
    /// screen to show.
    /// </summary>
    /// <remarks>
    /// A code that matches nothing comes back as
    /// <see cref="PairingStatus.Unknown"/> and is counted by the caller
    /// against the host's shared authentication lockout. The store keeps no
    /// miss counter of its own: a wrong code is a wrong secret, and a
    /// client working through wrong secrets should be shut out of every
    /// door at once rather than of this one.
    /// </remarks>
    /// <param name="userCode">The public code, in any spacing or casing.</param>
    /// <param name="request">The request, when there is one that is still pending.</param>
    /// <returns>The status of the named request.</returns>
    public PairingStatus Lookup(string userCode, out PairingRequest? request)
    {
        request = null;
        if (Normalize(userCode) is not { } normalized || !_byUserCode.TryGetValue(normalized, out var entry))
            return PairingStatus.Unknown;

        var status = Resolve(entry, Now);
        if (status is PairingStatus.Pending)
            request = entry.ToRequest();
        return status;
    }

    /// <summary>
    /// Claims a pending request so that a key can be minted for it.
    /// </summary>
    /// <remarks>
    /// Claiming first is what stops two approvers minting two keys for one
    /// request: the second finds the request no longer pending and is told
    /// so. The claim is surrendered by
    /// <see cref="CompleteApproval"/> or <see cref="AbandonApproval"/>.
    /// </remarks>
    /// <param name="userCode">The public code, in any spacing or casing.</param>
    /// <param name="ticket">The claim, when the request was pending.</param>
    /// <returns>The status of the named request.</returns>
    public PairingStatus BeginApproval(string userCode, [NotNullWhen(true)] out ApprovalTicket? ticket)
    {
        ticket = null;
        if (Normalize(userCode) is not { } normalized || !_byUserCode.TryGetValue(normalized, out var entry))
            return PairingStatus.Unknown;

        var status = Resolve(entry, Now);
        if (status is not PairingStatus.Pending)
            return status;

        if (!entry.TryBeginApproval())
            return Resolve(entry, Now);

        ticket = new ApprovalTicket(entry);
        return PairingStatus.Pending;
    }

    /// <summary>
    /// Publishes a minted key against a claimed request, for the requesting
    /// device to collect.
    /// </summary>
    /// <param name="ticket">The claim taken by <see cref="BeginApproval"/>.</param>
    /// <param name="apiKey">The key the host minted.</param>
    /// <param name="username">Who approved.</param>
    /// <param name="apiKeyExpiresAt">When the key expires, when it does.</param>
    public void CompleteApproval(ApprovalTicket ticket, string apiKey, string username, DateTime? apiKeyExpiresAt)
    {
        var entry = ticket.Entry;
        entry.CompleteApproval(apiKey, username, apiKeyExpiresAt, Now + CollectionWindow);

        _logger.LogWarning(
            "Pairing approved by {Username} for {DeviceName} at {ClientIP} (code {UserCode}).",
            username, entry.DeviceName, entry.RequestedFrom, entry.FormattedUserCode);
    }

    /// <summary>
    /// Gives a claim back without approving, when minting failed. The
    /// request goes back to pending and can be answered again.
    /// </summary>
    /// <param name="ticket">The claim taken by <see cref="BeginApproval"/>.</param>
    public void AbandonApproval(ApprovalTicket ticket)
    {
        ticket.Entry.AbandonApproval();
    }

    /// <summary>
    /// Refuses a request.
    /// </summary>
    /// <remarks>
    /// A refusal is not an error path. It is a terminal answer the polling
    /// device receives and understands, so that it can drop back to the
    /// ordinary sign-in it would have shown had nobody tried to pair it.
    /// </remarks>
    /// <param name="userCode">The public code, in any spacing or casing.</param>
    /// <param name="username">Who refused, for the log.</param>
    /// <returns>The status of the named request after the attempt.</returns>
    public PairingStatus Deny(string userCode, string username)
    {
        if (Normalize(userCode) is not { } normalized || !_byUserCode.TryGetValue(normalized, out var entry))
            return PairingStatus.Unknown;

        var status = Resolve(entry, Now);
        if (status is PairingStatus.Denied)
            return PairingStatus.Denied;

        if (status is not PairingStatus.Pending || !entry.TryDeny())
            return Resolve(entry, Now);

        _logger.LogInformation(
            "Pairing refused by {Username} for {DeviceName} at {ClientIP} (code {UserCode}).",
            username, entry.DeviceName, entry.RequestedFrom, entry.FormattedUserCode);

        return PairingStatus.Denied;
    }

    // ──────────────────────────────────────────────
    //  Status, codes and housekeeping
    // ──────────────────────────────────────────────

    /// <summary>
    /// The status of a request as of <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// Expiry is decided here, at read time, rather than by the sweep. That
    /// keeps a request's answer a function of its state and the clock — the
    /// sweep only reclaims memory, so a sweep that has not run yet cannot
    /// make a request look alive.
    ///
    /// Terminal answers outrank the clock. A refusal stays a refusal after
    /// the deadline passes, because "someone said no" is what the person in
    /// front of the device needs to know, and "you waited too long" would be
    /// a different and untrue sentence.
    /// </remarks>
    private static PairingStatus Resolve(PairingEntry entry, DateTimeOffset now)
        => entry.State switch
        {
            PairingState.Denied => PairingStatus.Denied,
            PairingState.Completed => PairingStatus.Completed,
            PairingState.Approved => now >= entry.ExpiresAt ? PairingStatus.Expired : PairingStatus.Approved,
            _ => now >= entry.ExpiresAt ? PairingStatus.Expired : PairingStatus.Pending,
        };

    private static string GenerateUserCode()
    {
        var code = new char[UserCodeLength];
        for (var i = 0; i < code.Length; i++)
            code[i] = UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)];
        return new string(code);
    }

    private static string Format(string code)
        => $"{code[..4]}-{code[4..]}";

    /// <summary>
    /// Reduces a typed or pasted code to the form it is stored under, or
    /// <c>null</c> when it could not be one of ours.
    /// </summary>
    /// <param name="userCode">The code as entered.</param>
    /// <returns>The normalized code, or <c>null</c>.</returns>
    public static string? Normalize(string? userCode)
    {
        if (userCode is not { Length: > 0 } || userCode.Length > MaxUserCodeInputLength)
            return null;

        var builder = new StringBuilder(UserCodeLength);
        foreach (var character in userCode)
        {
            if (character is '-' or ' ' or '_')
                continue;

            var upper = char.ToUpperInvariant(character);
            if (!UserCodeAlphabet.Contains(upper, StringComparison.Ordinal))
                return null;

            if (builder.Length == UserCodeLength)
                return null;

            builder.Append(upper);
        }

        return builder.Length == UserCodeLength ? builder.ToString() : null;
    }

    /// <summary>
    /// Reduces a device name to something safe to store and legible in a
    /// token list, or <c>null</c> when it is not usable.
    /// </summary>
    /// <remarks>
    /// The name is shown to the approver verbatim and stored on the key
    /// verbatim, so control characters — which could rewrite a line of the
    /// approval screen or of the log — are refused rather than stripped.
    /// A device that sends one is broken, and quietly accepting a mangled
    /// name would break the one property that makes the key revocable
    /// later: that the name means something to whoever reads it.
    /// </remarks>
    /// <param name="deviceName">The name as offered.</param>
    /// <returns>The trimmed name, or <c>null</c>.</returns>
    public static string? NormalizeDeviceName(string? deviceName)
    {
        if (deviceName is null)
            return null;

        var trimmed = deviceName.Trim();
        if (trimmed.Length is 0 or > MaxDeviceNameLength)
            return null;

        return trimmed.Any(char.IsControl) ? null : trimmed;
    }

    /// <summary>
    /// Reduces an optional descriptive field — device kind, user agent — to
    /// something safe to show, or <c>null</c>.
    /// </summary>
    /// <param name="value">The value as offered.</param>
    /// <param name="maxLength">The longest value kept.</param>
    /// <returns>The trimmed value, or <c>null</c>.</returns>
    public static string? NormalizeDescriptor(string? value, int maxLength)
    {
        if (value is null)
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length is 0)
            return null;

        if (trimmed.Length > maxLength)
            trimmed = trimmed[..maxLength];

        return trimmed.Any(char.IsControl) ? null : trimmed;
    }

    /// <summary>The longest device kind kept.</summary>
    public static int DeviceTypeLimit => MaxDeviceTypeLength;

    /// <summary>The longest user agent kept.</summary>
    public static int UserAgentLimit => MaxUserAgentLength;

    /// <summary>
    /// Drops requests nobody can act on any more, and revokes the keys of
    /// approvals nobody collected.
    /// </summary>
    /// <remarks>
    /// The second half is the reason this is not merely a memory sweep. An
    /// approval mints a real key; if the requesting device never comes back
    /// — it was switched off, it was a photograph of someone else's screen
    /// — that key would otherwise sit in the user's token list forever,
    /// issued and unclaimed.
    /// </remarks>
    public void RunCleanup()
    {
        var now = Now;
        foreach (var entry in _byUserCode.Values.ToArray())
        {
            var status = Resolve(entry, now);
            if (status is PairingStatus.Expired && entry.State is PairingState.Approved && entry.ApiKey is { } orphaned)
            {
                if (entry.TryClaimForRevocation())
                {
                    _logger.LogWarning(
                        "Revoking the key issued to {DeviceName} (code {UserCode}) — approved, never collected.",
                        entry.DeviceName, entry.FormattedUserCode);
                    RevokeQuietly(orphaned);
                }
            }

            if (now < entry.ExpiresAt + Retention)
                continue;

            _byUserCode.TryRemove(entry.UserCode, out _);
            _byDeviceCode.TryRemove(entry.DeviceCode, out _);
        }

        Sweep(now);
    }

    private void RevokeQuietly(string apiKey)
    {
        // Fire and forget on purpose: the sweep runs on a timer with nobody
        // waiting on it, and a revoke that fails must not take the sweep
        // down with it. The next sweep will not retry — the claim is taken
        // — so the failure is logged loudly enough to be actionable.
        _ = Task.Run(async () =>
        {
            try
            {
                await _revokeApiKey(apiKey).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to revoke an uncollected pairing key.");
            }
        });
    }

    private bool IsUnderLimit(string key, int limit, out DateTimeOffset? nextAllowedAt)
    {
        nextAllowedAt = null;
        if (!_requestsPerIp.TryGetValue(key, out var entry))
            return true;

        var now = Now;
        if (now - entry.WindowStart >= RateLimitWindow)
        {
            _requestsPerIp.TryRemove(key, out _);
            return true;
        }

        if (entry.Count < limit)
            return true;

        nextAllowedAt = entry.WindowStart + RateLimitWindow;
        return false;
    }

    private void RecordUse(string key)
    {
        var now = Now;
        _requestsPerIp.AddOrUpdate(
            key,
            _ => new WindowEntry(now),
            (_, existing) =>
            {
                if (now - existing.WindowStart >= RateLimitWindow)
                    return new WindowEntry(now);

                existing.Increment();
                return existing;
            });
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (key, entry) in _requestsPerIp.ToArray())
        {
            if (now - entry.WindowStart >= RateLimitWindow)
                _requestsPerIp.TryRemove(key, out _);
        }
    }

    private sealed class WindowEntry(DateTimeOffset windowStart)
    {
        private int _count = 1;

        public DateTimeOffset WindowStart { get; } = windowStart;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }
}
