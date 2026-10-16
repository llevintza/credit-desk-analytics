using Desk.Data.Marks;

namespace Desk.Data.Tests;

/// <summary>README §6 P6 / ADR-0015: the simulated marks are a pure function of (bond, tick), and seq only grows.</summary>
public sealed class MarkSimulatorTests
{
    private static readonly MarkSimulator Sim = new(TimeSpan.FromSeconds(1));
    private static readonly MarkedBond[] Bonds = [.. Enumerable.Range(1, 200).Select(i => new MarkedBond(i, 90 + i % 15, i % 7 == 0 ? null : 150 + i))];

    [Fact]
    public void A_tick_is_the_wall_clock_in_tick_lengths_and_maps_back_to_its_start()
    {
        var now = new DateTimeOffset(2026, 10, 16, 9, 30, 0, 250, TimeSpan.Zero);
        var tick = Sim.TickAt(now);
        Assert.Equal(now.ToUnixTimeMilliseconds() / 1000, tick);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 9, 30, 0, TimeSpan.Zero), Sim.TimeOf(tick));
        Assert.Equal(tick * 4 + 1, new MarkSimulator(TimeSpan.FromMilliseconds(250)).TickAt(now)); // 250 ms into the second
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkSimulator(TimeSpan.Zero));
    }

    [Fact]
    public void Each_bond_changes_on_its_own_cadence_of_3_to_14_ticks()
    {
        foreach (var bond in Bonds.Take(50))
        {
            var changes = Enumerable.Range(1_000, 200).Where(t => MarkSimulator.LastChange(bond.BondId, t) == t).ToArray();
            var gaps = changes.Zip(changes.Skip(1), (a, b) => b - a).Distinct().ToArray();
            var gap = Assert.Single(gaps); // a fixed period per bond
            Assert.InRange(gap, MarkSimulator.MinPeriodTicks, MarkSimulator.MaxPeriodTicks);
        }
        // Not every bond moves together: a tick changes only some of them.
        var movedAtOneTick = Bonds.Count(b => MarkSimulator.LastChange(b.BondId, 5_000) == 5_000);
        Assert.InRange(movedAtOneTick, 1, Bonds.Length / 2);
    }

    [Fact]
    public void A_mark_is_fixed_between_changes_and_its_seq_is_the_change_tick()
    {
        var bond = Bonds[10];
        var now = 1_800_000_000L;
        var changed = MarkSimulator.LastChange(bond.BondId, now);
        var mark = Sim.MarkAt(bond, now);
        Assert.Equal(changed, mark.Seq);
        Assert.Equal(Sim.TimeOf(changed), mark.EventTs);
        Assert.Equal(mark, Sim.MarkAt(bond, changed)); // same as at the change itself
        Assert.Equal(mark, new MarkSimulator(TimeSpan.FromSeconds(1)).MarkAt(bond, now)); // and on any other instance
    }

    [Fact]
    public void Seq_never_goes_back_for_a_bond_however_time_moves_forward()
    {
        foreach (var bond in Bonds.Take(20))
        {
            var last = long.MinValue;
            for (var t = 10_000L; t < 10_300; t++)
            {
                var seq = Sim.MarkAt(bond, t).Seq;
                Assert.True(seq >= last && seq <= t);
                last = seq;
            }
        }
    }

    [Fact]
    public void Marks_stay_near_the_overnight_values_and_move_spread_against_price()
    {
        foreach (var bond in Bonds)
        {
            var mark = Sim.MarkAt(bond, 1_800_000_123L);
            var move = mark.Price - bond.Price;
            Assert.InRange(move, -0.81, 0.81); // 0.6 swing + 0.2 noise, plus rounding
            Assert.Equal(Math.Round(mark.Price, 4), mark.Price);
            if (bond.SpreadBp is null)
                Assert.Null(mark.SpreadBp);
            else
                Assert.InRange(Math.Abs(bond.SpreadBp.Value - move * 20 - mark.SpreadBp!.Value), 0, 0.06); // 20 bp per point, 1 dp
        }
        // A price never goes to zero or below, whatever the overnight price.
        Assert.True(Sim.MarkAt(new MarkedBond(3, 0.001, null), 7).Price >= 0.01);
    }

    [Fact]
    public void Marks_actually_move_over_time()
    {
        var prices = Enumerable.Range(0, 300).Select(t => Sim.MarkAt(Bonds[0], 2_000_000L + t).Price).Distinct().Count();
        Assert.True(prices > 10, $"only {prices} distinct prices in 300 ticks");
    }

    [Fact]
    public void ChangedBetween_returns_only_what_changed_after_the_given_seq_oldest_first()
    {
        const long now = 3_000_000L;
        var all = Sim.ChangedBetween(Bonds, long.MinValue, now);
        Assert.Equal(Bonds.Length, all.Count); // a fresh connection gets every bond's current mark
        Assert.Equal(all.OrderBy(m => m.Seq).ThenBy(m => m.BondId), all);

        // A reconnect that applied everything up to now - 5 gets only the bonds that changed since.
        var missed = Sim.ChangedBetween(Bonds, now - 5, now);
        Assert.NotEmpty(missed);
        Assert.All(missed, m => Assert.InRange(m.Seq, now - 4, now));
        Assert.Equal(all.Where(m => m.Seq > now - 5).Select(m => m.BondId).Order(), missed.Select(m => m.BondId).Order());

        // Nothing is newer than now itself: a reconnect within the same tick replays nothing.
        Assert.Empty(Sim.ChangedBetween(Bonds, now, now));
    }
}
