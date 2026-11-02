using Bikepark.Sim.Core;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Terrain;

/// <summary>
/// What really stands on a real map (content, from <c>tools/terrain-import</c>, in the terrain file): water, buildings and
/// streets. Static: nothing flows or changes. Water is carved into the terrain and fills <see cref="TerrainGrid.WaterDepth"/>,
/// buildings and streets clear the vegetation, lakes and buildings block building (<see cref="TerrainGrid.BlockedAt"/>).
/// </summary>
public sealed class TerrainScenery
{
    public List<SceneryStream> Streams { get; set; } = [];
    public List<SceneryLake> Lakes { get; set; } = [];
    public List<SceneryBuilding> Buildings { get; set; } = [];
    public List<SceneryStreet> Streets { get; set; } = [];

    public List<string> Validate()
    {
        var errors = new List<string>();
        if (Streams.Any(s => s.Points.Count < 2 || s.WidthCm <= 0)) errors.Add("scenery: a stream needs two points and a width");
        if (Streets.Any(s => s.Points.Count < 2 || s.WidthCm <= 0)) errors.Add("scenery: a street needs two points and a width");
        if (Lakes.Any(l => l.Outline.Count < 3 || l.Holes.Any(h => h.Count < 3))) errors.Add("scenery: a lake outline needs three points");
        if (Buildings.Any(b => b.HalfLengthCm <= 0 || b.HalfWidthCm <= 0 || b.HeightCm <= 0 || (b.DirX == 0 && b.DirZ == 0)))
            errors.Add("scenery: a building needs a size, a height and a direction");
        return errors;
    }
}

/// <summary>A brook or river, in flow direction.</summary>
public sealed class SceneryStream
{
    public List<PointCm> Points { get; set; } = [];
    public int WidthCm { get; set; }
}

/// <summary>A lake: its level is the lowest point of its shore. Holes are islands.</summary>
public sealed class SceneryLake
{
    public List<PointCm> Outline { get; set; } = [];
    public List<List<PointCm>> Holes { get; set; } = [];
}

/// <summary>A building as an oriented rectangle. (DirX, DirZ) is the length axis, a permille unit vector.</summary>
public sealed class SceneryBuilding
{
    public int CenterX { get; set; }
    public int CenterZ { get; set; }
    public int HalfLengthCm { get; set; }
    public int HalfWidthCm { get; set; }
    public int DirX { get; set; } = 1000;
    public int DirZ { get; set; }
    public int HeightCm { get; set; }
    public bool FlatRoof { get; set; }
}

/// <summary>A tarmac street that is only scenery: nobody rides it, nothing routes over it. Only in older saves: real maps now build their roads as tarmac ways.</summary>
public sealed class SceneryStreet
{
    public List<PointCm> Points { get; set; } = [];
    public int WidthCm { get; set; }
}

/// <summary>One point of a stream's water surface.</summary>
public readonly record struct StreamPoint(int X, int Z, int LevelCm);

/// <summary>A stream's surface as carved: levels never rise downstream.</summary>
public sealed record WaterStream(IReadOnlyList<StreamPoint> Points, int WidthCm);

/// <summary>A lake's surface.</summary>
public sealed record WaterLake(IReadOnlyList<PointCm> Outline, int LevelCm);

/// <summary>What the scenery leaves on a grid, derived: where nothing can be built, and the water surfaces for the view.</summary>
public sealed class TerrainSceneryData(byte[] blocked, List<WaterStream> streams, List<WaterLake> lakes)
{
    public const byte Lake = 1;
    public const byte Building = 2;

    public byte[] Blocked { get; } = blocked;
    public IReadOnlyList<WaterStream> Streams { get; } = streams;
    public IReadOnlyList<WaterLake> Lakes { get; } = lakes;
}

/// <summary>Puts <see cref="TerrainScenery"/> on the generated layers. Integer-only, in list order.</summary>
internal static class TerrainSceneryBuilder
{
    private const int LakeDepthCm = 150;
    private const int BrookDepthCm = 30;
    private const int RiverDepthCm = 80;
    private const int RiverFromWidthCm = 300;
    private const int MinWaterHalfWidthCm = 100; // every crossing way sample sees water, however narrow the brook
    private const int BankReachCm = 1500;
    private const int BankBandCm = 1000;
    private const int BankGradePermille = 1000;
    private const int StepCm = 100;

    /// <summary>Carves lakes and streams into the heights and fills the water layer. Before the slopes are computed.</summary>
    public static TerrainSceneryData Carve(TerrainScenery scenery, int[] heights, byte[] water, int nx, int nz)
    {
        var blocked = new byte[nx * nz];
        var best = new long[nx * nz];
        Array.Fill(best, long.MaxValue);
        var target = new int[nx * nz];
        var lakes = new List<WaterLake>();
        foreach (var lake in scenery.Lakes)
        {
            var shore = Walk([.. lake.Outline, lake.Outline[0]]);
            int level = int.MaxValue;
            foreach (var p in shore)
                level = Math.Min(level, heights[Sample(p.Z, nz) * nx + Sample(p.X, nx)]);
            Channel(shore.Select(p => new StreamPoint(p.X, p.Z, level)).ToList(), 0, 0, heights, water, blocked, best, target, nx, nz);
            Fill(lake.Outline, nx, nz, i => blocked[i] = TerrainSceneryData.Lake);
            foreach (var hole in lake.Holes)
                Fill(hole, nx, nz, i => blocked[i] = 0);
            Fill(lake.Outline, nx, nz, i =>
            {
                if (blocked[i] != TerrainSceneryData.Lake) return;
                heights[i] = level - LakeDepthCm;
                water[i] = LakeDepthCm;
            });
            lakes.Add(new WaterLake(lake.Outline, level));
        }

        var streams = new List<WaterStream>();
        foreach (var stream in scenery.Streams)
        {
            var points = Walk(stream.Points);
            int first = heights[Sample(points[0].Z, nz) * nx + Sample(points[0].X, nx)];
            int last = heights[Sample(points[^1].Z, nz) * nx + Sample(points[^1].X, nx)];
            if (last > first) points.Reverse(); // mapped against the flow
            var levels = NeverRising(points.Select(p => heights[Sample(p.Z, nz) * nx + Sample(p.X, nx)]).ToList());
            var surface = new List<StreamPoint>(points.Count);
            for (int i = 0; i < points.Count; i++)
                surface.Add(new StreamPoint(points[i].X, points[i].Z, levels[i]));
            int half = Math.Max(stream.WidthCm / 2, MinWaterHalfWidthCm);
            Channel(surface, half, stream.WidthCm >= RiverFromWidthCm ? RiverDepthCm : BrookDepthCm, heights, water, blocked, best, target, nx, nz);
            streams.Add(new WaterStream(surface, stream.WidthCm));
        }
        return new TerrainSceneryData(blocked, streams, lakes);
    }

    /// <summary>Clears the vegetation under water, buildings and streets and blocks the buildings. After the layers.</summary>
    public static void Clear(TerrainScenery scenery, TerrainSceneryData data, byte[] water, byte[] trees, byte[] rock, byte[] roots, int nx, int nz)
    {
        void Bare(int i)
        {
            trees[i] = 0;
            rock[i] = 0;
            roots[i] = 0;
        }
        for (int i = 0; i < water.Length; i++)
            if (water[i] > 0) Bare(i);

        foreach (var street in scenery.Streets)
        {
            int radius = street.WidthCm / 200 + 1;
            foreach (var p in Walk(street.Points))
            {
                int cx = Sample(p.X, nx), cz = Sample(p.Z, nz);
                for (int z = Math.Max(0, cz - radius); z <= Math.Min(nz - 1, cz + radius); z++)
                    for (int x = Math.Max(0, cx - radius); x <= Math.Min(nx - 1, cx + radius); x++)
                        Bare(z * nx + x);
            }
        }

        foreach (var b in scenery.Buildings)
        {
            int reach = (Math.Max(b.HalfLengthCm, b.HalfWidthCm) * 1415 / 1000 + 200) / 100 + 1;
            int cx = Sample(b.CenterX, nx), cz = Sample(b.CenterZ, nz);
            for (int z = Math.Max(0, cz - reach); z <= Math.Min(nz - 1, cz + reach); z++)
                for (int x = Math.Max(0, cx - reach); x <= Math.Min(nx - 1, cx + reach); x++)
                {
                    long dx = x * 100L - b.CenterX, dz = z * 100L - b.CenterZ;
                    long u = Math.Abs(dx * b.DirX + dz * b.DirZ) / 1000, v = Math.Abs(dz * b.DirX - dx * b.DirZ) / 1000;
                    if (u > b.HalfLengthCm + 200 || v > b.HalfWidthCm + 200) continue;
                    Bare(z * nx + x);
                    if (u <= b.HalfLengthCm + 100 && v <= b.HalfWidthCm + 100 && water[z * nx + x] == 0)
                        data.Blocked[z * nx + x] = TerrainSceneryData.Building;
                }
        }
    }

    private static int Sample(int cm, int n) => Math.Clamp((cm + 50) / 100, 0, n - 1);

    /// <summary>The polyline in steps of about 1 m, both ends exact.</summary>
    private static List<PointCm> Walk(IReadOnlyList<PointCm> points)
    {
        var walked = new List<PointCm> { points[0] };
        for (int i = 1; i < points.Count; i++)
        {
            long dx = points[i].X - points[i - 1].X, dz = points[i].Z - points[i - 1].Z;
            int steps = (int)Math.Max(1, FixedMath.ISqrt(dx * dx + dz * dz) / StepCm);
            for (int s = 1; s <= steps; s++)
                walked.Add(new PointCm((int)(points[i - 1].X + dx * s / steps), (int)(points[i - 1].Z + dz * s / steps)));
        }
        return walked;
    }

    /// <summary>
    /// The closest never-rising fit to the ground along a stream (pool adjacent violators): real heights are too coarse to
    /// fall steadily, and a plain running minimum would dig a canyon after every dip.
    /// </summary>
    private static List<int> NeverRising(List<int> ground)
    {
        var sums = new List<long>();
        var counts = new List<int>();
        foreach (int h in ground)
        {
            long sum = h;
            int count = 1;
            while (sums.Count > 0 && sums[^1] * count < sum * counts[^1])
            {
                sum += sums[^1];
                count += counts[^1];
                sums.RemoveAt(sums.Count - 1);
                counts.RemoveAt(counts.Count - 1);
            }
            sums.Add(sum);
            counts.Add(count);
        }
        var levels = new List<int>(ground.Count);
        for (int b = 0; b < sums.Count; b++)
            for (int i = 0; i < counts[b]; i++)
                levels.Add((int)(sums[b] / counts[b]));
        return levels;
    }

    /// <summary>
    /// A bed under the line (<paramref name="depthCm"/> below the level within <paramref name="halfCm"/>) and banks meeting
    /// the ground. Lakes stay as they are. <paramref name="best"/> and <paramref name="target"/> are scratch, reset here.
    /// </summary>
    private static void Channel(List<StreamPoint> line, int halfCm, int depthCm, int[] heights, byte[] water, byte[] blocked, long[] best, int[] target, int nx, int nz)
    {
        int radius = (halfCm + BankReachCm) / 100 + 1;
        long reach2 = (long)(halfCm + BankReachCm) * (halfCm + BankReachCm);
        var touched = new List<int>();
        foreach (var p in line)
        {
            int cx = Sample(p.X, nx), cz = Sample(p.Z, nz);
            for (int z = Math.Max(0, cz - radius); z <= Math.Min(nz - 1, cz + radius); z++)
                for (int x = Math.Max(0, cx - radius); x <= Math.Min(nx - 1, cx + radius); x++)
                {
                    long dx = x * 100L - p.X, dz = z * 100L - p.Z, d2 = dx * dx + dz * dz;
                    int i = z * nx + x;
                    if (d2 > reach2 || d2 >= best[i]) continue;
                    if (best[i] == long.MaxValue) touched.Add(i);
                    best[i] = d2;
                    target[i] = p.LevelCm;
                }
        }
        foreach (int i in touched)
        {
            long d = FixedMath.ISqrt(best[i]);
            best[i] = long.MaxValue;
            if (blocked[i] == TerrainSceneryData.Lake) continue;
            if (d <= halfCm && depthCm > 0)
            {
                heights[i] = target[i] - depthCm;
                water[i] = (byte)depthCm;
                continue;
            }
            long allowed = TerrainEditor.AllowedDifference(d - halfCm, BankGradePermille, BankBandCm);
            long diff = heights[i] - target[i];
            if (Math.Abs(diff) > allowed)
                heights[i] = (int)(target[i] + Math.Clamp(diff, -allowed, allowed));
        }
    }

    /// <summary>Calls <paramref name="visit"/> for every sample inside the polygon (scanlines, even-odd).</summary>
    private static void Fill(IReadOnlyList<PointCm> polygon, int nx, int nz, Action<int> visit)
    {
        int minZ = Math.Max(0, polygon.Min(p => p.Z) / 100), maxZ = Math.Min(nz - 1, polygon.Max(p => p.Z) / 100 + 1);
        var crossings = new List<long>();
        for (int z = minZ; z <= maxZ; z++)
        {
            long zc = z * 100L;
            crossings.Clear();
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                if ((a.Z <= zc) == (b.Z <= zc)) continue;
                crossings.Add(a.X + (zc - a.Z) * (b.X - a.X) / (b.Z - a.Z));
            }
            crossings.Sort();
            for (int c = 0; c + 1 < crossings.Count; c += 2)
            {
                int x0 = (int)Math.Max(0, (crossings[c] + 99) / 100), x1 = (int)Math.Min(nx - 1, crossings[c + 1] / 100);
                for (int x = x0; x <= x1; x++) visit(z * nx + x);
            }
        }
    }
}
