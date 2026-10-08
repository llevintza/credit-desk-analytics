namespace Desk.Seeder.Generation;

/// <summary>
/// xoshiro256** seeded through SplitMix64. Used instead of System.Random so the same seed produces the
/// same database on the same platform and runtime (README §5: deterministic seed). Math.* draws are
/// not guaranteed bit-identical across OS/arch. Not cryptographic.
/// </summary>
public sealed class Rng
{
    private ulong _s0, _s1, _s2, _s3;

    public Rng(ulong seed)
    {
        ulong x = seed;
        _s0 = SplitMix(ref x); _s1 = SplitMix(ref x); _s2 = SplitMix(ref x); _s3 = SplitMix(ref x);
    }

    /// <summary>An independent stream for one table, so adding rows to one table never shifts another's values.</summary>
    public static Rng For(int seed, string stream)
    {
        ulong h = 1469598103934665603UL; // FNV-1a over the stream name
        foreach (var ch in stream) { h ^= ch; h *= 1099511628211UL; }
        return new Rng((ulong)(uint)seed * 0x9E3779B97F4A7C15UL ^ h);
    }

    private static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    public ulong NextULong()
    {
        var result = Rotl(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0; _s3 ^= _s1; _s1 ^= _s2; _s0 ^= _s3; _s2 ^= t; _s3 = Rotl(_s3, 45);
        return result;
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform integer in [min, max] inclusive.</summary>
    public int Int(int min, int max) => min + (int)(NextDouble() * (max - min + 1));

    public double Uniform(double min, double max) => min + NextDouble() * (max - min);

    public bool Chance(double p) => NextDouble() < p;

    /// <summary>Standard normal via Box–Muller.</summary>
    public double Normal(double mean = 0, double sd = 1)
    {
        var u1 = 1.0 - NextDouble();
        var u2 = NextDouble();
        return mean + sd * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    public T Pick<T>(IReadOnlyList<T> items) => items[Int(0, items.Count - 1)];

    /// <summary>Weighted pick; weights need not sum to 1.</summary>
    public T Pick<T>(IReadOnlyList<(T Item, double Weight)> items)
    {
        var total = 0.0;
        foreach (var (_, w) in items) total += w;
        var r = NextDouble() * total;
        foreach (var (item, w) in items) { if ((r -= w) < 0) return item; }
        return items[^1].Item;
    }
}
