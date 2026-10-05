using Bikepark.Sim.Terrain;
using static Bikepark.Sim.Terrain.FixedMath;

namespace Bikepark.Sim.Trails;

/// <summary>Bike park difficulty rating, from the 90th-percentile segment difficulty.</summary>
public enum TrailRating : byte
{
    Green = 0,
    Blue = 1,
    Red = 2,
    Black = 3,
}

/// <summary>
/// One ~10 m piece of a way. Grade is signed along the way's direction (+ = uphill), in permille of horizontal
/// distance. Turn is the heading change from the previous segment (0 = straight, 500 = 90°, 1000 = reversal).
/// Layer values are averages of the terrain samples along the segment (0..255). Difficulty is 0..1000.
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
    int Difficulty);

/// <summary>A point on a way: position in cm and the (unnormalized) horizontal direction of travel.</summary>
public readonly record struct WayPoint(int X, int Y, int Z, int DirX, int DirZ);

/// <summary>
/// The derived shape of a way: an integer Catmull-Rom spline through the control points, sampled about every
/// meter and laid onto the terrain, with cumulative 3D distance and ~10 m segments. Fully deterministic.
/// Trails follow the ground; access paths are graded (cut and fill): their height is the terrain smoothed over
/// ±<see cref="GradingHalfWindowSamples"/> samples, with both ends kept at ground level.
/// </summary>
public sealed class WayGeometry
{
    public const int GreenMaxDifficulty = 250;
    public const int BlueMaxDifficulty = 480;
    public const int RedMaxDifficulty = 700;
    public const int GradingHalfWindowSamples = 10;

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

        var sorted = segments.Select(s => s.Difficulty).Order().ToArray();
        DifficultyScore = sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, sorted.Length * 9 / 10)];
        Rating = DifficultyScore switch
        {
            < GreenMaxDifficulty => TrailRating.Green,
            < BlueMaxDifficulty => TrailRating.Blue,
            < RedMaxDifficulty => TrailRating.Red,
            _ => TrailRating.Black,
        };
    }

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

    /// <summary>90th-percentile segment difficulty (0..1000).</summary>
    public int DifficultyScore { get; }

    public TrailRating Rating { get; }

    public int MaxDownGradePermille => _segments.Length == 0 ? 0 : Math.Max(0, -_segments.Min(s => s.GradePermille));
    public int MaxUpGradePermille => _segments.Length == 0 ? 0 : Math.Max(0, _segments.Max(s => s.GradePermille));

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

    public static WayGeometry Build(TerrainGrid grid, WayKind kind, IReadOnlyList<PointCm> points, int segmentLengthCm)
    {
        if (points.Count < 2) throw new ArgumentException("A way needs at least two points.", nameof(points));

        var (xs, zs) = SampleSpline(points, grid.SizeMeters * 100);
        int n = xs.Count;
        var ground = new int[n];
        for (int i = 0; i < n; i++)
            ground[i] = grid.HeightAt(xs[i], zs[i]);
        var y = kind == WayKind.AccessPath ? Grade(ground) : ground;

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
        return new WayGeometry(kind, x, y, z, distance, segments);
    }

    /// <summary>Symmetric moving average that shrinks towards the ends, so the end heights stay unchanged.</summary>
    private static int[] Grade(int[] ground)
    {
        int n = ground.Length;
        var prefix = new long[n + 1];
        for (int i = 0; i < n; i++)
            prefix[i + 1] = prefix[i] + ground[i];

        var graded = new int[n];
        for (int i = 0; i < n; i++)
        {
            int w = Math.Min(GradingHalfWindowSamples, Math.Min(i, n - 1 - i));
            graded[i] = (int)((prefix[i + w + 1] - prefix[i - w]) / (2 * w + 1));
        }
        return graded;
    }

    /// <summary>Catmull-Rom through all control points (endpoints repeated), sampled about every meter.</summary>
    private static (List<int> X, List<int> Z) SampleSpline(IReadOnlyList<PointCm> p, int limitCm)
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
            int steps = (int)Math.Max(1, (ISqrt(cdx * cdx + cdz * cdz) + 99) / 100);
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

        if (xs.Count == 1)
        {
            // Degenerate input (all points equal): keep a two-sample way so lengths stay positive.
            xs.Add(xs[0]);
            zs.Add(zs[0]);
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
            segments.Add(new WaySegment(
                segments.Count, distance[start], distance[end], grade, turn, r, ro, (int)(trees / count), surface,
                Difficulty(kind, grade, turn, r, ro)));
            start = end;
        }
        return segments.ToArray();
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
    /// Segment difficulty 0..1000: steepness downhill dominates, then roughness (rock, roots), then tight turns.
    /// Climbing sections of a trail add a little.
    /// </summary>
    internal static int Difficulty(WayKind kind, int gradePermille, int turnPermille, int rock, int roots)
    {
        int down = Math.Max(0, -gradePermille);
        int up = Math.Max(0, gradePermille);
        int steep = down <= 150 ? down * 250 / 150 : Math.Min(1000, 250 + (down - 150) * 750 / 450);
        int rough = Math.Min(1000, Math.Max(rock * 1000 / 255, roots * 800 / 255));
        int tight = Math.Min(1000, turnPermille * 2);
        int climb = kind == WayKind.Trail ? Math.Min(300, up * 3) : 0;
        return Math.Clamp((steep * 55 + rough * 25 + tight * 20) / 100 + climb, 0, 1000);
    }

    private static int Lerp(int a, int b, long f) => (int)(a + (b - a) * f / 1000);
}
