using System.Numerics;

namespace Bikepark.Sim.Terrain;

/// <summary>
/// Q16 fixed-point helpers (<see cref="One"/> = 1.0). Terrain generation uses only integer math so it is
/// bit-identical on every platform.
/// </summary>
internal static class FixedMath
{
    public const int Shift = 16;
    public const long One = 1L << Shift;
    public const long OneHalf = One / 2;

    public static long Clamp01(long v) => Math.Clamp(v, 0, One);

    /// <summary>Hermite smoothstep for t in [0, One].</summary>
    public static long Smooth(long t) => t * t * (3 * One - 2 * t) >> (2 * Shift);

    public static long Lerp(long a, long b, long t) => a + ((b - a) * t >> Shift);

    public static long Mul(long a, long b) => a * b >> Shift;

    /// <summary>Floor of the square root of a non-negative integer.</summary>
    public static long ISqrt(long v)
    {
        if (v <= 0) return 0;
        long x = 1L << ((BitOperations.Log2((ulong)v) >> 1) + 1);
        while (true)
        {
            long y = (x + v / x) >> 1;
            if (y >= x) return x;
            x = y;
        }
    }
}

/// <summary>Integer hash-based value noise. Returns values in [0, One).</summary>
internal static class IntNoise
{
    public static uint Hash(int x, int z, uint seed)
    {
        unchecked
        {
            uint h = seed ^ 0x9E3779B9u;
            h ^= (uint)x * 0x85EBCA6Bu;
            h = BitOperations.RotateLeft(h, 13) * 0xC2B2AE35u;
            h ^= (uint)z * 0x27D4EB2Fu;
            h = BitOperations.RotateLeft(h, 15) * 0x165667B1u;
            h ^= h >> 16;
            h *= 0x85EBCA6Bu;
            h ^= h >> 13;
            h *= 0xC2B2AE35u;
            h ^= h >> 16;
            return h;
        }
    }

    /// <summary>Smoothly interpolated lattice noise at Q16 lattice coordinates.</summary>
    public static long Value(long xq, long zq, uint seed)
    {
        int ix = (int)(xq >> FixedMath.Shift);
        int iz = (int)(zq >> FixedMath.Shift);
        long sx = FixedMath.Smooth(xq & (FixedMath.One - 1));
        long sz = FixedMath.Smooth(zq & (FixedMath.One - 1));

        long c00 = Hash(ix, iz, seed) >> 16;
        long c10 = Hash(ix + 1, iz, seed) >> 16;
        long c01 = Hash(ix, iz + 1, seed) >> 16;
        long c11 = Hash(ix + 1, iz + 1, seed) >> 16;

        return FixedMath.Lerp(FixedMath.Lerp(c00, c10, sx), FixedMath.Lerp(c01, c11, sx), sz);
    }

    /// <summary>Fractal value noise over integer meter coordinates. Result in [0, One).</summary>
    public static long Fbm(long xM, long zM, int wavelengthM, int octaves, uint seed)
    {
        long sum = 0, total = 0, amplitude = FixedMath.One;
        for (int o = 0; o < octaves; o++)
        {
            long wavelength = Math.Max(1, wavelengthM >> o);
            uint octaveSeed = Hash(o, 0x5EED, seed);
            // Offset each octave's lattice so octaves don't share lattice points.
            long xq = (xM * FixedMath.One + o * 0x3A71L * FixedMath.One) / wavelength;
            long zq = (zM * FixedMath.One + o * 0x1F2BL * FixedMath.One) / wavelength;
            sum += Value(xq, zq, octaveSeed) * amplitude >> FixedMath.Shift;
            total += amplitude;
            amplitude >>= 1;
        }
        return sum * FixedMath.One / total;
    }
}
