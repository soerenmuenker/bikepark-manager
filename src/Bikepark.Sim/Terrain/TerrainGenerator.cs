using Bikepark.Sim.Core;
using static Bikepark.Sim.Terrain.FixedMath;

namespace Bikepark.Sim.Terrain;

/// <summary>
/// Builds a <see cref="TerrainGrid"/> from <see cref="TerrainSettings"/> using integer math only, so the same
/// settings and seed give a bit-identical terrain everywhere. Passes:
/// <list type="number">
///   <item>shape: main peak + radiating ridges + noise, rescaled to exactly the configured relief;</item>
///   <item>layers: slope, rock, tree density (below the tree line), roots. The water layer is reserved for
///         later features (ponds, streams) and is generated empty.</item>
/// </list>
/// </summary>
public static class TerrainGenerator
{
    private readonly record struct Ridge(long Ux, long Uz, long LengthM, long WidthM, uint MeanderSeed);

    public static TerrainGrid Generate(TerrainSettings settings, ulong worldSeed)
    {
        var errors = settings.Validate();
        if (errors.Count > 0)
            throw new InvalidDataException($"Invalid terrain settings: {string.Join("; ", errors)}");

        ulong seed = settings.Seed ?? worldSeed;
        int n = settings.SizeMeters + 1;
        var rng = SimRandom.FromSeed(seed, stream: 0x7E44A1);
        var seeds = new NoiseSeeds(rng);

        int[] heights = Shape(settings, n, rng, seeds);
        ushort[] slopes = ComputeSlopes(heights, n);
        var (trees, rock, roots) = Layers(settings, heights, slopes, n, seeds);
        var water = new byte[n * n];

        return new TerrainGrid(settings with { Seed = seed }, seed, heights, slopes, trees, rock, roots, water);
    }

    private sealed class NoiseSeeds(SimRandom rng)
    {
        public readonly uint Detail = rng.NextUInt();
        public readonly uint Crag = rng.NextUInt();
        public readonly uint Undulation = rng.NextUInt();
        public readonly uint Forest = rng.NextUInt();
        public readonly uint TreeLine = rng.NextUInt();
        public readonly uint Outcrop = rng.NextUInt();
        public readonly uint RootNoise = rng.NextUInt();
    }

    // ---------------------------------------------------------------- shape

    private static int[] Shape(TerrainSettings s, int n, SimRandom rng, NoiseSeeds seeds)
    {
        long radius = s.PeakRadiusMeters;
        var ridges = new Ridge[s.RidgeCount];
        for (int r = 0; r < ridges.Length; r++)
        {
            var (ux, uz) = RandomDirection(rng);
            ridges[r] = new Ridge(
                ux, uz,
                LengthM: radius * rng.Range(800, 1150) / 1000,
                WidthM: radius * rng.Range(160, 240) / 1000,
                MeanderSeed: rng.NextUInt());
        }

        var raw = new long[n * n];
        long min = long.MaxValue, max = long.MinValue;
        for (int z = 0; z < n; z++)
        {
            for (int x = 0; x < n; x++)
            {
                long dx = x - s.PeakXMeters, dz = z - s.PeakZMeters;

                // Main peak: concave-ish radial falloff.
                long distQ8 = ISqrt((dx * dx + dz * dz) << 16);
                long t = Clamp01(One - distQ8 * One / (radius * 256));
                long dome = (Smooth(t) + Mul(t, t)) / 2;

                // Ridges radiating from the peak, with meandering crests.
                long ridge = 0;
                // Distances in Q8 meters: whole meters would show up as visible terracing.
                foreach (var rd in ridges)
                {
                    long alongQ8 = (dx * rd.Ux + dz * rd.Uz) >> (Shift - 8);
                    long profile;
                    if (alongQ8 < 0)
                    {
                        // Fade in behind the peak so the ridge doesn't start with a cliff.
                        profile = Smooth(Clamp01(One + alongQ8 * One / (rd.WidthM * 256)));
                    }
                    else
                    {
                        long alongT = Clamp01(One - alongQ8 * One / (rd.LengthM * 256));
                        if (alongT == 0) continue;
                        profile = Smooth(alongT);
                    }
                    long forward = Math.Max(0, alongQ8);
                    long signedPerpQ8 = (dx * rd.Uz - dz * rd.Ux) >> (Shift - 8);
                    long meanderQ8 = (IntNoise.Value(forward * One / (140 * 256), 0, rd.MeanderSeed) - OneHalf) * forward * 35 / 100 >> Shift;
                    long perpQ8 = Math.Abs(signedPerpQ8 - meanderQ8);
                    long across = Smooth(Clamp01(One - perpQ8 * One / (rd.WidthM * 256)));
                    ridge = Math.Max(ridge, Mul(profile, across));
                }

                long detail = IntNoise.Fbm(x, z, 240, 4, seeds.Detail) - OneHalf;
                long cragBase = IntNoise.Fbm(x, z, 90, 3, seeds.Crag);
                long crag = One - Math.Abs(2 * cragBase - One);
                crag = Mul(crag, crag);
                long undulation = IntNoise.Fbm(x, z, 450, 2, seeds.Undulation);

                long v = dome * 1000
                         + ridge * s.RidgeStrengthPermille
                         + detail * s.RoughnessPermille * 2
                         + Mul(crag, dome) * s.RoughnessPermille
                         + undulation * 80;
                raw[z * n + x] = v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        // Rescale so the terrain spans exactly [BaseElevation, BaseElevation + Relief].
        var heights = new int[n * n];
        long range = Math.Max(1, max - min);
        for (int i = 0; i < heights.Length; i++)
            heights[i] = s.BaseElevationCm + (int)((raw[i] - min) * s.ReliefCm / range);
        return heights;
    }

    private static (long Ux, long Uz) RandomDirection(SimRandom rng)
    {
        while (true)
        {
            long vx = rng.Range(-1000, 1001), vz = rng.Range(-1000, 1001);
            long len = ISqrt(vx * vx + vz * vz);
            if (len >= 200 && len <= 1000)
                return (vx * One / len, vz * One / len);
        }
    }

    // ---------------------------------------------------------------- layers

    internal static ushort[] ComputeSlopes(int[] heights, int n)
    {
        var slopes = new ushort[n * n];
        for (int z = 0; z < n; z++)
        {
            int z0 = Math.Max(0, z - 1), z1 = Math.Min(n - 1, z + 1);
            for (int x = 0; x < n; x++)
            {
                int x0 = Math.Max(0, x - 1), x1 = Math.Min(n - 1, x + 1);
                // Gradient in cm per m, scaled by the (1 or 2 m) sample spacing.
                long gx = (long)(heights[z * n + x1] - heights[z * n + x0]) * 1000 / (x1 - x0);
                long gz = (long)(heights[z1 * n + x] - heights[z0 * n + x]) * 1000 / (z1 - z0);
                // grade = |gradient| / 100 cm; permille = sqrt(gx² + gz²) * 1000 / 100 / 1000.
                long grade = ISqrt(gx * gx + gz * gz) / 100;
                slopes[z * n + x] = (ushort)Math.Min(ushort.MaxValue, grade);
            }
        }
        return slopes;
    }

    private static (byte[] Trees, byte[] Rock, byte[] Roots) Layers(
        TerrainSettings s, int[] heights, ushort[] slopes, int n, NoiseSeeds seeds)
    {
        int count = n * n;
        var trees = new byte[count];
        var rock = new byte[count];
        var roots = new byte[count];

        // Forest patches: pick the noise threshold so that ForestCoverage of the map lies inside a patch.
        var patch = new long[count];
        var histogram = new int[1 << 10];
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                long v = IntNoise.Fbm(x, z, 160, 4, seeds.Forest);
                patch[z * n + x] = v;
                histogram[(int)(v >> (Shift - 10))]++;
            }
        long threshold = Quantile(histogram, count, 1000 - s.ForestCoveragePermille) << (Shift - 10);
        long patchEdge = One / 20;

        for (int z = 0; z < n; z++)
        {
            for (int x = 0; x < n; x++)
            {
                int i = z * n + x;
                long h = heights[i];
                long slope = slopes[i];

                // Rock: steep faces, the summit area, and noisy outcrops on moderate slopes.
                long steepRock = Math.Clamp((slope - 700) * 255 / 600, 0, 255);
                long summitRock = Math.Clamp((h - (s.TreeLineCm + 2_500)) * 255 / 3_000, 0, 255);
                long outcrop = 0;
                if (slope > 300)
                {
                    long o = IntNoise.Fbm(x, z, 45, 3, seeds.Outcrop);
                    outcrop = Math.Clamp((o - One * 68 / 100) * 255 / (One * 12 / 100), 0, 255);
                }
                long r = Math.Max(steepRock, Math.Max(summitRock, outcrop));
                rock[i] = (byte)r;

                // Trees: inside forest patches, below a jittered tree line, not too steep.
                long jitter = (IntNoise.Fbm(x, z, 70, 2, seeds.TreeLine) - OneHalf) * 3_000 >> Shift; // ±15 m
                long elevationF = Clamp01((s.TreeLineCm + jitter - h) * One / 5_000);
                long slopeF = Clamp01((1_400 - slope) * One / 600);
                long patchF = Clamp01((patch[i] - threshold) * One / patchEdge);
                long density = Mul(Mul(elevationF, slopeF), patchF) * 255 >> Shift;
                density = density * (255 - r) / 255;
                trees[i] = (byte)density;

                // Roots: exposed under trees, more on slopes.
                if (density > 0)
                {
                    long slopeN = Clamp01(slope * One / 900);
                    long noise = IntNoise.Fbm(x, z, 14, 2, seeds.RootNoise);
                    long v = density * (One * 15 / 100 + slopeN * 85 / 100) >> Shift;
                    roots[i] = (byte)Math.Clamp(v * 2 * noise >> Shift, 0, 255);
                }
            }
        }
        return (trees, rock, roots);
    }

    /// <summary>Bucket index below which <paramref name="permille"/> of all values lie.</summary>
    private static long Quantile(int[] histogram, int count, int permille)
    {
        long target = (long)count * permille / 1000;
        long seen = 0;
        for (int b = 0; b < histogram.Length; b++)
        {
            seen += histogram[b];
            if (seen > target) return b;
        }
        return histogram.Length;
    }
}
