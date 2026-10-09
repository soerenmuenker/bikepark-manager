using Bikepark.Sim.Terrain;
using static Bikepark.Sim.Terrain.FixedMath;

namespace Bikepark.Sim.Trails;

/// <summary>Bike park difficulty rating (see <see cref="WayGeometry.DifficultyScore"/>).</summary>
public enum TrailRating : byte
{
    Green = 0,
    Blue = 1,
    Red = 2,
    Black = 3,
}

/// <summary>What decided a trail's rating.</summary>
public enum RatingCause : byte
{
    /// <summary>The trail as a whole: its typical (median) gradient.</summary>
    OverallSteepness = 0,

    /// <summary>Its hardest stretches: the 90th-percentile segment (steepness, plus some for rocks, roots and tight turns).</summary>
    SteepSections = 1,

    /// <summary>Its hardest feature (a feature on the line is mandatory).</summary>
    Feature = 2,
}

/// <summary>
/// One ~10 m piece of a way. Grade is signed along the way's direction (+ = uphill), in permille of horizontal
/// distance. Turn is the heading change from the previous segment (0 = straight, 500 = 90°, 1000 = reversal).
/// Layer values are averages of the terrain samples along the segment (0..255). Difficulty is 0..1000: the terrain's
/// (<see cref="TerrainDifficulty"/>), or the hardest feature's on the segment (<see cref="FeatureDifficulty"/>, 0 = no
/// feature) if that is higher.
/// </summary>
public readonly record struct WaySegment(
    int Index,
    long StartCm,
    long EndCm,
    int GradePermille,
    int TurnPermille,
    int Rock,
    int Roots,
    int Trees,
    TerrainSurface Surface,
    int Difficulty,
    int FeatureDifficulty = 0,
    int TerrainDifficulty = 0)
{
    /// <summary>Gradient score in tenths (-100..100) along the way's direction; see <see cref="Trails.Gradient"/>.</summary>
    public int GradientTenths => Trails.Gradient.FromPermille(GradePermille);
}

/// <summary>A point on a way: position in cm and the (unnormalized) horizontal direction of travel.</summary>
public readonly record struct WayPoint(int X, int Y, int Z, int DirX, int DirZ);

/// <summary>
/// The derived shape of a way: an integer Catmull-Rom spline through the control points, sampled about every
/// meter and laid onto the terrain, with cumulative 3D distance and ~10 m segments. Fully deterministic.
/// Trails follow the ground; access paths are graded (cut and fill): their height is the terrain smoothed over
/// ±<c>gradingMeters</c> (about one sample per meter), with both ends kept at ground level.
/// </summary>
public sealed class WayGeometry
{
    public const int GreenMaxDifficulty = 250;
    public const int BlueMaxDifficulty = 480;
    public const int RedMaxDifficulty = 700;
    public const int DefaultGradingMeters = 25;

    private readonly int[] _x;
    private readonly int[] _y;
    private readonly int[] _z;
    private readonly long[] _distance;
    private readonly WaySegment[] _segments;

    private WayGeometry(WayKind kind, int[] x, int[] y, int[] z, long[] distance, WaySegment[] segments)
    {
        Kind = kind;
        _x = x;
        _y = y;
        _z = z;
        _distance = distance;
        _segments = segments;

        // The hardest of: the trail overall (median gradient), its hardest stretches (90th-percentile segment, terrain
        // only) and its hardest feature (a feature on the line is mandatory, so it is a floor for the rating).
        int overall = 0, sections = 0, feature = 0;
        if (segments.Length > 0)
        {
            var down = segments.Select(s => Math.Max(0, -s.GradientTenths)).Order().ToArray();
            overall = OverallSteepness(down[down.Length / 2]);
            var terrain = segments.Select(s => s.TerrainDifficulty).Order().ToArray();
            sections = terrain[Math.Min(terrain.Length - 1, terrain.Length * 9 / 10)];
            feature = segments.Max(s => s.FeatureDifficulty);
        }
        DifficultyScore = Math.Max(overall, Math.Max(sections, feature));
        RatingCause = feature >= DifficultyScore ? RatingCause.Feature
            : overall >= sections ? RatingCause.OverallSteepness
            : RatingCause.SteepSections;
        Rating = RatingFor(DifficultyScore);
    }

    public static TrailRating RatingFor(int difficulty) => difficulty switch
    {
        < GreenMaxDifficulty => TrailRating.Green,
        < BlueMaxDifficulty => TrailRating.Blue,
        < RedMaxDifficulty => TrailRating.Red,
        _ => TrailRating.Black,
    };

    public WayKind Kind { get; }
    public int SampleCount => _x.Length;
    public long LengthCm => _distance[^1];
    public IReadOnlyList<WaySegment> Segments => _segments;
    public ReadOnlySpan<int> Xs => _x;
    public ReadOnlySpan<int> Ys => _y;
    public ReadOnlySpan<int> Zs => _z;
    public ReadOnlySpan<long> Distances => _distance;

    public int StartHeightCm => _y[0];
    public int EndHeightCm => _y[^1];

    /// <summary>
    /// 0..1000, the hardest of: the median gradient (<see cref="OverallSteepness"/>), the 90th-percentile segment's terrain
    /// difficulty and the hardest feature. Rated green &lt; 250 &lt;= blue &lt; 480 &lt;= red &lt; 700 &lt;= black.
    /// </summary>
    public int DifficultyScore { get; }

    public TrailRating Rating { get; }

    /// <summary>Which of the three decided <see cref="DifficultyScore"/>.</summary>
    public RatingCause RatingCause { get; }

    /// <summary>Steepest drop along the way, as a positive gradient score in tenths.</summary>
    public int MaxDropGradient => _segments.Length == 0 ? 0 : Math.Max(0, -_segments.Min(s => s.GradientTenths));

    /// <summary>Steepest climb along the way, gradient score in tenths.</summary>
    public int MaxClimbGradient => _segments.Length == 0 ? 0 : Math.Max(0, _segments.Max(s => s.GradientTenths));

    /// <summary>Position at a distance along the way (clamped), interpolated between samples.</summary>
    public WayPoint PositionAt(long distanceCm)
    {
        distanceCm = Math.Clamp(distanceCm, 0, LengthCm);
        int i = SampleIndexAt(distanceCm);
        int j = Math.Min(i + 1, _x.Length - 1);
        long span = _distance[j] - _distance[i];
        long f = span == 0 ? 0 : Math.Clamp((distanceCm - _distance[i]) * 1000 / span, 0, 1000);
        return new WayPoint(
            Lerp(_x[i], _x[j], f), Lerp(_y[i], _y[j], f), Lerp(_z[i], _z[j], f),
            _x[j] - _x[i], _z[j] - _z[i]);
    }

    /// <summary>Height at a distance along the way, in cm.</summary>
    public int HeightAt(long distanceCm) => PositionAt(distanceCm).Y;

    public int SegmentIndexAt(long distanceCm)
    {
        int lo = 0, hi = _segments.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_segments[mid].StartCm <= distanceCm) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Index of the last sample at or before the distance.</summary>
    public int SampleIndexAt(long distanceCm)
    {
        if (distanceCm <= 0) return 0;
        if (distanceCm >= _distance[^1]) return Math.Max(0, _distance.Length - 2);
        int i = Array.BinarySearch(_distance, distanceCm);
        return i >= 0 ? Math.Min(i, _distance.Length - 2) : ~i - 1;
    }

    /// <summary>
    /// Builds the geometry of a way. <paramref name="features"/> (placed trail features, see <see cref="TrailFeatures.Resolve"/>)
    /// raise the difficulty of the segments they cover; the shape does not depend on them.
    /// </summary>
    public static WayGeometry Build(
        TerrainGrid grid, WayKind kind, IReadOnlyList<PointCm> points, int segmentLengthCm, int gradingMeters = DefaultGradingMeters,
        IReadOnlyList<PlacedFeature>? features = null)
    {
        if (points.Count < 2) throw new ArgumentException("A way needs at least two points.", nameof(points));

        var (xs, zs) = SampleSpline(points, grid.SizeMeters * 100);
        int n = xs.Count;
        var ground = new int[n];
        for (int i = 0; i < n; i++)
            ground[i] = grid.HeightAt(xs[i], zs[i]);
        var y = kind == WayKind.AccessPath ? Grade(ground, gradingMeters) : ground;

        // Accumulate in 1/16 cm: rounding each ~1 m step to whole cm would lose the height component.
        var distance = new long[n];
        long total = 0;
        for (int i = 1; i < n; i++)
        {
            long dx = xs[i] - xs[i - 1], dy = y[i] - y[i - 1], dz = zs[i] - zs[i - 1];
            total += ISqrt((dx * dx + dy * dy + dz * dz) << 8);
            distance[i] = Math.Max(distance[i - 1] + 1, total >> 4);
        }

        var x = xs.ToArray();
        var z = zs.ToArray();
        var segments = BuildSegments(grid, kind, x, y, z, distance, segmentLengthCm);
        if (features is { Count: > 0 })
            ApplyFeatures(segments, features);
        return new WayGeometry(kind, x, y, z, distance, segments);
    }

    /// <summary>Symmetric moving average that shrinks towards the ends, so the end heights stay unchanged.</summary>
    private static int[] Grade(int[] ground, int halfWindow)
    {
        int n = ground.Length;
        var prefix = new long[n + 1];
        for (int i = 0; i < n; i++)
            prefix[i + 1] = prefix[i] + ground[i];

        var graded = new int[n];
        for (int i = 0; i < n; i++)
        {
            int w = Math.Min(halfWindow, Math.Min(i, n - 1 - i));
            graded[i] = (int)((prefix[i + w + 1] - prefix[i - w]) / (2 * w + 1));
        }
        return graded;
    }

    private const int SampleSpacingCm = 100;
    private const int DenseSpacingCm = 20;

    /// <summary>
    /// Catmull-Rom through all control points (endpoints repeated), then resampled every
    /// <see cref="SampleSpacingCm"/> of horizontal distance. Even spacing matters: path grading averages over a
    /// number of samples, and spline parameter steps are not evenly spaced. Both ends stay exact.
    /// </summary>
    private static (List<int> X, List<int> Z) SampleSpline(IReadOnlyList<PointCm> p, int limitCm)
    {
        var (dx, dz) = SampleSplineDense(p, limitCm);
        var xs = new List<int> { dx[0] };
        var zs = new List<int> { dz[0] };
        // Distances in 1/16 cm, so rounding the short dense steps doesn't stretch the spacing.
        const long spacing = SampleSpacingCm * 16L;
        long walked = 0, next = spacing;
        for (int i = 1; i < dx.Count; i++)
        {
            long sx = dx[i] - dx[i - 1], sz = dz[i] - dz[i - 1];
            long step = ISqrt((sx * sx + sz * sz) << 8);
            while (step > 0 && walked + step >= next)
            {
                long f = (next - walked) * 1000 / step; // position within this dense step, permille
                xs.Add((int)(dx[i - 1] + sx * f / 1000));
                zs.Add((int)(dz[i - 1] + sz * f / 1000));
                next += spacing;
            }
            walked += step;
        }

        // End exactly on the last control point; drop a resampled point that would sit too close to it.
        if (xs.Count > 1 && walked - (next - spacing) < spacing / 3)
        {
            xs.RemoveAt(xs.Count - 1);
            zs.RemoveAt(zs.Count - 1);
        }
        if (xs[^1] != dx[^1] || zs[^1] != dz[^1])
        {
            xs.Add(dx[^1]);
            zs.Add(dz[^1]);
        }
        if (xs.Count == 1)
        {
            // Degenerate input (all points equal): keep a two-sample way so lengths stay positive.
            xs.Add(xs[0]);
            zs.Add(zs[0]);
        }
        return (xs, zs);
    }

    /// <summary>Catmull-Rom through all control points (endpoints repeated), sampled densely by spline parameter.</summary>
    private static (List<int> X, List<int> Z) SampleSplineDense(IReadOnlyList<PointCm> p, int limitCm)
    {
        var xs = new List<int> { Math.Clamp(p[0].X, 0, limitCm) };
        var zs = new List<int> { Math.Clamp(p[0].Z, 0, limitCm) };
        for (int i = 0; i < p.Count - 1; i++)
        {
            var p0 = p[Math.Max(i - 1, 0)];
            var p1 = p[i];
            var p2 = p[i + 1];
            var p3 = p[Math.Min(i + 2, p.Count - 1)];
            long cdx = p2.X - p1.X, cdz = p2.Z - p1.Z;
            int steps = (int)Math.Max(1, (ISqrt(cdx * cdx + cdz * cdz) + DenseSpacingCm - 1) / DenseSpacingCm);
            for (int k = 1; k <= steps; k++)
            {
                long t = k * One / steps;
                int x = Math.Clamp((int)CatmullRom(p0.X, p1.X, p2.X, p3.X, t), 0, limitCm);
                int z = Math.Clamp((int)CatmullRom(p0.Z, p1.Z, p2.Z, p3.Z, t), 0, limitCm);
                if (x == xs[^1] && z == zs[^1]) continue;
                xs.Add(x);
                zs.Add(z);
            }
        }

        return (xs, zs);
    }

    private static long CatmullRom(long p0, long p1, long p2, long p3, long t)
    {
        long c0 = 2 * p1;
        long c1 = p2 - p0;
        long c2 = 2 * p0 - 5 * p1 + 4 * p2 - p3;
        long c3 = 3 * (p1 - p2) + p3 - p0;
        long v = ((((c3 * t >> Shift) + c2) * t >> Shift) + c1) * t >> Shift;
        return (v + c0) / 2;
    }

    private static WaySegment[] BuildSegments(
        TerrainGrid grid, WayKind kind, int[] x, int[] y, int[] z, long[] distance, int segmentLengthCm)
    {
        var segments = new List<WaySegment>();
        int start = 0;
        long previousDx = 0, previousDz = 0;
        while (start < x.Length - 1)
        {
            int end = start + 1;
            while (end < x.Length - 1 && distance[end] - distance[start] < segmentLengthCm)
                end++;
            // Avoid a tiny last segment: merge it into this one.
            if (end < x.Length - 1 && distance[^1] - distance[end] < segmentLengthCm / 3)
                end = x.Length - 1;

            long horizontal = 0;
            long rock = 0, roots = 0, trees = 0;
            var surfaceCounts = new int[5];
            for (int i = start; i <= end; i++)
            {
                if (i > start)
                {
                    long hx = x[i] - x[i - 1], hz = z[i] - z[i - 1];
                    horizontal += ISqrt(hx * hx + hz * hz);
                }
                var s = grid.Sample(x[i], z[i]);
                rock += s.Rock;
                roots += s.Roots;
                trees += s.TreeDensity;
                surfaceCounts[(int)s.Surface]++;
            }
            int count = end - start + 1;
            int grade = (int)((long)(y[end] - y[start]) * 1000 / Math.Max(1, horizontal));

            long dx = x[end] - x[start], dz = z[end] - z[start];
            int turn = Turn(previousDx, previousDz, dx, dz);
            previousDx = dx;
            previousDz = dz;

            var surface = (TerrainSurface)Array.IndexOf(surfaceCounts, surfaceCounts.Max());
            int r = (int)(rock / count), ro = (int)(roots / count);
            int difficulty = Difficulty(kind, grade, turn, r, ro);
            segments.Add(new WaySegment(
                segments.Count, distance[start], distance[end], grade, turn, r, ro, (int)(trees / count), surface,
                difficulty, TerrainDifficulty: difficulty));
            start = end;
        }
        return segments.ToArray();
    }

    private static void ApplyFeatures(WaySegment[] segments, IReadOnlyList<PlacedFeature> features)
    {
        for (int i = 0; i < segments.Length; i++)
        {
            var s = segments[i];
            int hardest = 0;
            foreach (var f in features)
                if (f.StartCm < s.EndCm && f.EndCm > s.StartCm)
                    hardest = Math.Max(hardest, f.Type.Difficulty);
            if (hardest > 0)
                segments[i] = s with { FeatureDifficulty = hardest, Difficulty = Math.Max(s.Difficulty, hardest) };
        }
    }

    /// <summary>Heading change between two direction vectors: (1 - cos) / 2, scaled to 0..1000.</summary>
    private static int Turn(long ax, long az, long bx, long bz)
    {
        long la = ISqrt(ax * ax + az * az), lb = ISqrt(bx * bx + bz * bz);
        if (la == 0 || lb == 0) return 0;
        long dot = ax * bx + az * bz;
        return (int)Math.Clamp((la * lb - dot) * 500 / (la * lb), 0, 1000);
    }

    /// <summary>
    /// Segment difficulty 0..1000: the steepness of the stretch (<see cref="SectionSteepness"/>) plus up to 100 for
    /// roughness (rock, roots) and up to 100 for tight turns. Climbing sections of a trail add a little.
    /// </summary>
    internal static int Difficulty(WayKind kind, int gradePermille, int turnPermille, int rock, int roots)
    {
        int down = Math.Max(0, -Gradient.FromPermille(gradePermille));
        int up = Math.Max(0, gradePermille);
        int rough = Math.Min(1000, Math.Max(rock * 1000 / 255, roots * 800 / 255));
        int tight = Math.Min(1000, turnPermille * 2);
        int climb = kind == WayKind.Trail ? Math.Min(300, up * 3) : 0;
        return Math.Clamp(SectionSteepness(down) + rough * 10 / 100 + tight * 10 / 100 + climb, 0, 1000);
    }

    /// <summary>
    /// Difficulty of a short stretch this steep (downhill gradient in tenths of the game's score): up to -1.5 (≈ 24 %)
    /// is green, up to -3.5 (≈ 61 %) blue, up to -4.8 (≈ 94 %) red, steeper black.
    /// </summary>
    internal static int SectionSteepness(int downTenths) => Ramp(downTenths, 15, 35, 48, 60);

    /// <summary>
    /// Difficulty of a trail whose typical (median) gradient is this steep: up to -0.7 (≈ 11 %) is green, up to -1.5
    /// (≈ 24 %) blue, up to -2.4 (≈ 40 %) red, steeper overall black.
    /// </summary>
    internal static int OverallSteepness(int downTenths) => Ramp(downTenths, 7, 15, 24, 35);

    /// <summary>Piecewise linear: 0 → 0, green → 250, blue → 480, red → 700, max → 1000 (and capped there).</summary>
    private static int Ramp(int x, int green, int blue, int red, int max)
    {
        if (x <= 0) return 0;
        if (x <= green) return x * GreenMaxDifficulty / green;
        if (x <= blue) return GreenMaxDifficulty + (x - green) * (BlueMaxDifficulty - GreenMaxDifficulty) / (blue - green);
        if (x <= red) return BlueMaxDifficulty + (x - blue) * (RedMaxDifficulty - BlueMaxDifficulty) / (red - blue);
        return Math.Min(1000, RedMaxDifficulty + (x - red) * (1000 - RedMaxDifficulty) / (max - red));
    }

    private static int Lerp(int a, int b, long f) => (int)(a + (b - a) * f / 1000);
}
