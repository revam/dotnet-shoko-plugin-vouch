namespace Shoko.Plugin.Vouch.Tests;

/// <summary>
/// A clock a test can move.
/// </summary>
/// <remarks>
/// Every expiry decision the store makes is a comparison against this at
/// read time, which is what lets a five-minute lifetime and a ten-minute
/// retention be tested in a millisecond. A store that decided expiry from a
/// background sweep instead would need a test that waits.
/// </remarks>
internal sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
