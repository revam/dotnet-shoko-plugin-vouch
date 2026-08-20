using System.ComponentModel.DataAnnotations;

using Shoko.Abstractions.Config;

namespace Shoko.Plugin.Vouch.Configuration;

/// <summary>
/// Configuration for the Vouch plugin.
/// </summary>
public class VouchPluginConfiguration : IConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether to trust <c>X-Forwarded-*</c>
    /// headers from proxies.
    ///
    /// It decides two things: the address shown to the approver as where the
    /// request came from, and the host in the verification URL the QR code
    /// carries. Enable it only when Shoko is behind a trusted reverse proxy —
    /// otherwise any client can name its own address and its own host.
    /// </summary>
    public bool TrustProxy { get; set; }

    /// <summary>
    /// Gets or sets how many hours an issued key lasts.
    ///
    /// A pairing key outliving the session that approved it is this plugin's
    /// purpose rather than a flaw in it — the whole point is signing in a
    /// television from a phone that will not stay signed in itself. What that
    /// costs is a bound, so the key expires on a schedule instead of never.
    ///
    /// The default is six months: long enough that a living-room device is
    /// not re-paired on any timescale a household notices, short enough that
    /// a key from a device someone no longer owns does not outlive them.
    /// The floor is one hour, because a key that expires faster than someone
    /// can walk to the other screen is a broken pairing, not a strict one.
    ///
    /// Only an administrator can change this. Shoko's configuration API is
    /// gated on the <c>admin</c> role in full, so there is no user-facing
    /// path to this value and no per-property setting needed to keep it that
    /// way.
    ///
    /// Independent of how long the pairing <em>request</em> lives, which is
    /// five minutes and not configurable — that one is a security property
    /// rather than a preference.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int IssuedKeyLifetimeHours { get; set; } = 4320;
}
