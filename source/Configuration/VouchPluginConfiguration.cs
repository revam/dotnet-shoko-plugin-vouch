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
    /// Gets or sets how many hours an issued key lasts, or <c>null</c> for a
    /// key that does not expire.
    ///
    /// Null by default because the device being paired is usually a
    /// television someone wants to stay signed in, and a key that expires is
    /// a device that stops working on a schedule nobody remembers setting.
    /// Set it where pairing is used for guests or shared screens: the key is
    /// then bounded as well as revocable.
    ///
    /// Independent of how long the pairing <em>request</em> lives, which is
    /// five minutes and not configurable — that one is a security property
    /// rather than a preference.
    /// </summary>
    public int? IssuedKeyLifetimeHours { get; set; }
}
