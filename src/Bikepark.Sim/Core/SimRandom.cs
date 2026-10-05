namespace Bikepark.Sim.Core;

/// <summary>
/// Deterministic PCG32 (XSH-RR) generator. The only source of randomness allowed in the simulation.
/// Its full state lives in <see cref="State.WorldState.Rng"/>, so saving and loading continues the exact sequence.
/// Never use <see cref="System.Random"/> in the sim: its algorithm is not guaranteed stable across runtimes.
/// </summary>
public sealed class SimRandom
{
    private const ulong Multiplier = 6364136223846793005UL;
    private const ulong DefaultStream = 54UL;

    /// <summary>Internal generator state. Public only for serialization.</summary>
    public ulong State { get; set; }

    /// <summary>Stream selector (always odd). Public only for serialization.</summary>
    public ulong Increment { get; set; }

    /// <summary>Seeds the generator the same way as the PCG reference <c>pcg32_srandom_r</c>.</summary>
    public static SimRandom FromSeed(ulong seed, ulong stream = DefaultStream)
    {
        var rng = new SimRandom { State = 0, Increment = (stream << 1) | 1UL };
        rng.NextUInt();
        rng.State += seed;
        rng.NextUInt();
        return rng;
    }

    public uint NextUInt()
    {
        ulong old = State;
        State = unchecked(old * Multiplier + Increment);
        uint xorShifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorShifted >> rot) | (xorShifted << (-rot & 31));
    }

    /// <summary>Uniform integer in [0, maxExclusive), without modulo bias.</summary>
    public int NextInt(int maxExclusive)
    {
        if (maxExclusive <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "Must be positive.");

        uint bound = (uint)maxExclusive;
        uint threshold = unchecked(0u - bound) % bound;
        while (true)
        {
            uint r = NextUInt();
            if (r >= threshold)
                return (int)(r % bound);
        }
    }

    /// <summary>Uniform integer in [minInclusive, maxExclusive).</summary>
    public int Range(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "Must be greater than minInclusive.");
        return minInclusive + NextInt(maxExclusive - minInclusive);
    }

    /// <summary>Returns true with probability permille / 1000. Values outside [0, 1000] are clamped.</summary>
    public bool ChancePermille(int permille)
    {
        if (permille <= 0) return false;
        if (permille >= 1000) return true;
        return NextInt(1000) < permille;
    }
}
