namespace Desk.Data.Marks;

/// <summary>A bond the intraday overlay marks (README §6 P6), with its overnight price and spread from the snapshot.</summary>
public sealed record MarkedBond(int BondId, double Price, double? SpreadBp);

/// <summary>
/// One simulated intraday mark: <paramref name="Seq"/> is the tick at which the bond's mark last changed, so it only
/// ever grows for a bond, across reconnects and restarts (the client drops anything at or below what it applied).
/// </summary>
public sealed record Mark(int BondId, long Seq, DateTimeOffset EventTs, double Price, double? SpreadBp);

/// <summary>
/// Simulated intraday marks for a subset of bonds (README §6 P6, ADR-0015). Pure and deterministic: a bond's mark
/// is a function of its id, its overnight values and a tick number, and the tick is the wall clock divided by the
/// tick length. So every instance, every connection and every restart agrees on what the mark of a bond is at a
/// tick, and <c>seq</c> is monotonic without any shared state. Nothing here touches the database.
/// </summary>
/// <remarks>
/// Each bond changes every 3 to 14 ticks, at its own phase, so a tick moves a few bonds rather than all of them.
/// The price drifts on a slow sine of a few tenths of a point plus a little hash noise; the spread moves the other
/// way at 20 bp per point (a spread duration of about 5). Values are rounded like the snapshot's (price 4 dp,
/// spread 1 dp).
/// </remarks>
public sealed class MarkSimulator(TimeSpan tick)
{
    public const int MinPeriodTicks = 3;
    public const int MaxPeriodTicks = 14;
    private const double SpreadBpPerPoint = 20;

    public TimeSpan Tick { get; } = tick > TimeSpan.Zero ? tick : throw new ArgumentOutOfRangeException(nameof(tick));

    /// <summary>The tick <paramref name="now"/> falls in.</summary>
    public long TickAt(DateTimeOffset now) => now.ToUnixTimeMilliseconds() / (long)Tick.TotalMilliseconds;

    /// <summary>When <paramref name="tick"/> starts: the <c>event_ts</c> of a mark made at that tick.</summary>
    public DateTimeOffset TimeOf(long tick) => DateTimeOffset.FromUnixTimeMilliseconds(tick * (long)Tick.TotalMilliseconds);

    /// <summary>The last tick at or before <paramref name="tick"/> at which the bond's mark changed.</summary>
    public static long LastChange(int bondId, long tick)
    {
        var (period, phase) = Cadence(bondId);
        return tick - Mod(tick + phase, period);
    }

    /// <summary>The bond's mark as of <paramref name="tick"/>: the one made at its last change.</summary>
    public Mark MarkAt(MarkedBond bond, long tick)
    {
        var changed = LastChange(bond.BondId, tick);
        var h = Hash(bond.BondId);
        // A slow swing (period 600 to 1199 changes) plus noise in [-0.2, 0.2] points, both fixed by (bond, tick).
        var swing = 0.6 * Math.Sin(2 * Math.PI * changed / (600 + (int)(h % 600)) + h % 360);
        var noise = 0.2 * (Hash(bond.BondId, changed) % 2001 / 1000.0 - 1);
        var move = swing + noise;
        var price = Math.Round(Math.Max(0.01, bond.Price + move), 4);
        double? spread = bond.SpreadBp is { } s ? Math.Round(s - move * SpreadBpPerPoint, 1) : null;
        return new Mark(bond.BondId, changed, TimeOf(changed), price, spread);
    }

    /// <summary>
    /// The marks that changed after <paramref name="after"/> and at or before <paramref name="now"/>, oldest first:
    /// one per bond, its latest. A reconnect passes its last applied seq as <paramref name="after"/> and gets only
    /// what it missed; a stream that slept through several ticks catches up the same way.
    /// </summary>
    public IReadOnlyList<Mark> ChangedBetween(IEnumerable<MarkedBond> bonds, long after, long now) =>
        bonds.Where(b => LastChange(b.BondId, now) > after)
            .Select(b => MarkAt(b, now))
            .OrderBy(m => m.Seq).ThenBy(m => m.BondId)
            .ToList();

    private static (int Period, int Phase) Cadence(int bondId)
    {
        var h = Hash(bondId);
        var period = MinPeriodTicks + (int)(h % (MaxPeriodTicks - MinPeriodTicks + 1));
        return (period, (int)(h / 16 % (ulong)period));
    }

    private static long Mod(long a, int m) => ((a % m) + m) % m;

    /// <summary>SplitMix64: a stable, well-spread hash (never <see cref="HashCode"/>, which is seeded per process).</summary>
    private static ulong Hash(long a, long b = 0)
    {
        var z = unchecked((ulong)a * 0x9E3779B97F4A7C15UL + (ulong)b * 0xBF58476D1CE4E5B9UL + 0x632BE59BD9B4E019UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
