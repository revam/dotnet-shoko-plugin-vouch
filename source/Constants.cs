using System;

namespace Shoko.Plugin.Vouch;

/// <summary>
/// The handful of values that more than one file has to agree on.
/// </summary>
public static class Constants
{
    /// <summary>The plugin's unique identifier.</summary>
    public static readonly Guid PluginGuid = new("d88779b6-3551-4b07-a05e-595b6e167b13");

    /// <summary>The plugin's name, which is also its API route segment.</summary>
    public const string PluginName = "Vouch";

    /// <summary>
    /// Stamped into the device name of every key this plugin issues, so a
    /// vouched key is identifiable wherever device names are listed.
    ///
    /// It is load-bearing rather than decorative: <c>Approve</c> refuses a
    /// caller whose own device name carries it, which is what stops a
    /// vouched key from minting further vouched keys. Changing the wording
    /// changes that rule, and would let keys issued under the old wording
    /// vouch again.
    /// </summary>
    public const string VouchedByMarker = " (Vouched by ";

    /// <summary>
    /// The longest device name a key may carry, marker included.
    ///
    /// The requested name is truncated to fit rather than the marker, since
    /// the marker is a security control and the name is a label.
    /// </summary>
    public const int IssuedDeviceNameLimit = 128;

    /// <summary>
    /// Where a second device goes to answer a pairing request.
    ///
    /// It is a path rather than a URL because the host part depends on how
    /// the requesting device reached the server, and only a request knows
    /// that.
    /// </summary>
    public const string ApprovalPath = "/plugin/Vouch/Approve";
}
