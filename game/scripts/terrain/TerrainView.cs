using System.Diagnostics;
using Bikepark.Sim;
using Bikepark.Sim.Land;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Terrain;

/// <summary>
/// Renders the Sim's <see cref="TerrainGrid"/>: chunked terrain meshes with two levels of detail, instanced trees
/// and rocks, and a debug overlay (F1 cycles natural / slope / surface). Pure view; rebuilds when the simulation
/// is replaced with one that has a different terrain.
/// </summary>
public partial class TerrainView : Node3D
{
    public enum OverlayMode
    {
        Natural,
        Slope,
        Surface,
    }

    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";
    [Export] public int ChunkSizeMeters { get; set; } = 64;

    /// <summary>Chunks closer than this use full 1 m detail; farther ones use <see cref="FarStep"/>.</summary>
    [Export] public float DetailDistance { get; set; } = 220f;

    [Export] public int FarStep { get; set; } = 4;
    [Export] public float TreeDrawDistance { get; set; } = 550f;
    [Export] public float RockDrawDistance { get; set; } = 350f;

    private ShaderMaterial _terrainMaterial = null!;
    private ArrayMesh _treeMesh = null!;
    private ArrayMesh _juniperMesh = null!, _shrubMesh = null!;
    private ArrayMesh[] _rockMeshes = [], _stoneMeshes = [];
    private StandardMaterial3D _scatterMaterial = null!;
    private Node3D? _chunks;
    private readonly List<(Node3D Node, int X0, int Z0, int SizeX, int SizeZ)> _chunkInfo = [];
    private Func<int, int, bool>? _cleared;
    private SimHost _host = null!;
    private ImageTexture? _ownMask;
    private string _ownSignature = "";
    private bool _landDim;

    public TerrainGrid? Grid { get; private set; }

    public OverlayMode Overlay { get; private set; } = OverlayMode.Natural;

    /// <summary>
    /// Hides trees and rocks for which <paramref name="cleared"/>(xCm, zCm) is true (built way corridors, felled trees)
    /// and rebuilds the instance meshes: of every chunk, or only of the chunks overlapping <paramref name="area"/> (meters).
    /// </summary>
    public void SetScatterFilter(Func<int, int, bool>? cleared, Rect2? area = null)
    {
        _cleared = cleared;
        if (Grid is null) return;
        var scatter = new List<ScatterInstance>();
        if (area is null) _treeCount = _rockCount = 0;
        foreach (var (node, x0, z0, sizeX, sizeZ) in _chunkInfo)
        {
            if (area is { } a && !a.Intersects(new Rect2(x0, z0, sizeX, sizeZ), includeBorders: true)) continue;
            foreach (var child in node.GetChildren().OfType<MultiMeshInstance3D>().ToList())
            {
                node.RemoveChild(child);
                child.QueueFree();
            }
            AddScatter(Grid, node, x0, z0, sizeX, sizeZ, node.Position, scatter);
        }
    }

    /// <summary>Raised after the meshes were (re)built for a new terrain.</summary>
    public event Action<TerrainGrid>? TerrainBuilt;

    public override void _Ready()
    {
        _terrainMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/terrain.gdshader") };
        _treeMesh = ScatterMeshes.CreateTree();
        _juniperMesh = ScatterMeshes.CreateJuniper();
        _shrubMesh = ScatterMeshes.CreateShrub();
        _rockMeshes = Enum.GetValues<RockShape>().Select(shape => ScatterMeshes.CreateRock(shape, detail: 2)).ToArray();
        _stoneMeshes = Enum.GetValues<RockShape>().Select(shape => ScatterMeshes.CreateRock(shape, detail: 1)).ToArray();
        _scatterMaterial = ScatterMeshes.CreateMaterial();

        _host = GetNode<SimHost>(SimHostPath);
        _host.SimulationReplaced += OnSimulationReplaced;
        OnSimulationReplaced(_host.Sim);
    }

    public override void _Process(double delta)
    {
        // Structures flatten the ground (terrain edits): rebuild the chunks whose samples changed.
        var grid = _host.Sim.Terrain;
        UpdateOwnership();
        if (Grid is not null && !ReferenceEquals(grid, Grid) && grid.WidthMeters == Grid.WidthMeters && grid.DepthMeters == Grid.DepthMeters)
            RebuildChangedChunks(grid);
    }

    private void RebuildChangedChunks(TerrainGrid grid)
    {
        var old = Grid!;
        Grid = grid;
        var scatter = new List<ScatterInstance>();
        int rebuilt = 0;
        for (int i = 0; i < _chunkInfo.Count; i++)
        {
            var (node, x0, z0, sizeX, sizeZ) = _chunkInfo[i];
            if (!ChunkChanged(old, grid, x0, z0, sizeX, sizeZ)) continue;
            int index = node.GetIndex();
            _chunks!.RemoveChild(node);
            node.QueueFree();
            _chunkInfo.RemoveAt(i);
            var chunk = BuildChunk(grid, x0, z0, scatter);
            _chunkInfo.Insert(i, _chunkInfo[^1]);
            _chunkInfo.RemoveAt(_chunkInfo.Count - 1);
            _chunks.AddChild(chunk);
            _chunks.MoveChild(chunk, index);
            rebuilt++;
        }
        GD.Print($"Terrain: rebuilt {rebuilt} changed chunks");
        TerrainBuilt?.Invoke(grid);
    }

    private static bool ChunkChanged(TerrainGrid a, TerrainGrid b, int x0, int z0, int sizeX, int sizeZ)
    {
        // Include the skirt/normal neighbourhood: one sample around the chunk.
        int xa = Math.Max(0, x0 - 1), xb = Math.Min(a.WidthMeters, x0 + sizeX + 1);
        for (int z = Math.Max(0, z0 - 1); z <= Math.Min(a.DepthMeters, z0 + sizeZ + 1); z++)
        {
            int start = a.Index(xa, z), length = xb - xa + 1;
            if (!a.Heights.Slice(start, length).SequenceEqual(b.Heights.Slice(start, length))) return true;
            if (!a.TreeDensity.Slice(start, length).SequenceEqual(b.TreeDensity.Slice(start, length))) return true;
            if (!a.Rock.Slice(start, length).SequenceEqual(b.Rock.Slice(start, length))) return true;
            if (!a.WaterDepth.Slice(start, length).SequenceEqual(b.WaterDepth.Slice(start, length))) return true;
        }
        return false;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F1 })
        {
            SetOverlay((OverlayMode)(((int)Overlay + 1) % Enum.GetValues<OverlayMode>().Length));
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>Play view: grey out the land the park doesn't own (only in scenarios with parcels).</summary>
    public void SetLandDim(bool dim)
    {
        _landDim = dim;
        ApplyLandDim();
    }

    private void ApplyLandDim()
    {
        bool on = _landDim && _host.Sim.State.Parcels.Count > 0;
        _terrainMaterial.SetShaderParameter("land_dim", on ? 1f : 0f);
    }

    /// <summary>Rasterizes the owned land into a small mask (4 m per pixel) whenever the owned parcels change.</summary>
    private void UpdateOwnership()
    {
        var state = _host.Sim.State;
        if (Grid is null) return;
        string signature = $"{state.Parcels.Count}|{string.Join(',', state.OwnedParcelIds)}|{Grid.WidthMeters}x{Grid.DepthMeters}";
        if (signature == _ownSignature) return;
        _ownSignature = signature;
        const int cell = 4;
        int nx = Math.Max(1, Grid.WidthMeters / cell), nz = Math.Max(1, Grid.DepthMeters / cell);
        var image = Image.CreateEmpty(nx, nz, false, Image.Format.R8);
        for (int z = 0; z < nz; z++)
            for (int x = 0; x < nx; x++)
                image.SetPixel(x, z, LandMath.IsOwned(state, (x * cell + cell / 2) * 100L, (z * cell + cell / 2) * 100L) ? Colors.White : Colors.Black);
        _ownMask = ImageTexture.CreateFromImage(image);
        _terrainMaterial.SetShaderParameter("own_mask", _ownMask);
        _terrainMaterial.SetShaderParameter("map_size_m", new Vector2(Grid.WidthMeters, Grid.DepthMeters));
        ApplyLandDim();
    }

    public void SetOverlay(OverlayMode mode)
    {
        Overlay = mode;
        _terrainMaterial.SetShaderParameter("overlay_mode", (int)mode);
    }

    /// <summary>Terrain height in meters at a world position (clamped to the map).</summary>
    public float HeightAt(float x, float z) =>
        Grid is null ? 0f : Grid.HeightAt((long)MathF.Round(x * 100f), (long)MathF.Round(z * 100f)) / 100f;

    /// <summary>Ray-marches the heightmap. Returns false if the ray leaves the map without hitting the ground.</summary>
    public bool TryRaycast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 hit)
    {
        hit = default;
        if (Grid is null) return false;

        direction = direction.Normalized();
        const float step = 1f;
        var previous = origin;
        for (float t = step; t <= maxDistance; t += step)
        {
            var p = origin + direction * t;
            if (p.X < -50 || p.Z < -50 || p.X > Grid.WidthMeters + 50 || p.Z > Grid.DepthMeters + 50) break;
            if (p.Y > HeightAt(p.X, p.Z)) { previous = p; continue; }

            // Bisect between the last point above and this point below the ground.
            Vector3 above = previous, below = p;
            for (int i = 0; i < 12; i++)
            {
                var mid = (above + below) / 2f;
                if (mid.Y > HeightAt(mid.X, mid.Z)) above = mid; else below = mid;
            }
            hit = below;
            return Grid.Contains((long)(hit.X * 100), (long)(hit.Z * 100));
        }
        return false;
    }

    private void OnSimulationReplaced(Simulation sim)
    {
        var grid = sim.Terrain;
        if (ReferenceEquals(grid, Grid)) return;
        Build(grid);
    }

    private void Build(TerrainGrid grid)
    {
        var stopwatch = Stopwatch.StartNew();
        Grid = grid;
        _chunks?.QueueFree();
        _chunkInfo.Clear();
        _treeCount = _rockCount = 0;
        _chunks = new Node3D { Name = "Chunks" };
        AddChild(_chunks);
        _terrainMaterial.SetShaderParameter("tree_line_m", grid.Settings.TreeLineCm / 100f);
        _terrainMaterial.SetShaderParameter("desert", grid.Settings.Biome == TerrainBiome.Desert);

        var scatter = new List<ScatterInstance>();
        int chunkCount = 0;
        for (int z0 = 0; z0 < grid.DepthMeters; z0 += ChunkSizeMeters)
        {
            for (int x0 = 0; x0 < grid.WidthMeters; x0 += ChunkSizeMeters)
            {
                _chunks.AddChild(BuildChunk(grid, x0, z0, scatter));
                chunkCount++;
            }
        }

        GD.Print($"Terrain: {chunkCount} chunks, {_treeCount} trees, {_rockCount} rocks built in {stopwatch.ElapsedMilliseconds} ms");
        TerrainBuilt?.Invoke(grid);
    }

    private int _treeCount, _rockCount;

    private Node3D BuildChunk(TerrainGrid grid, int x0, int z0, List<ScatterInstance> scatter)
    {
        int sizeX = Math.Min(ChunkSizeMeters, grid.WidthMeters - x0);
        int sizeZ = Math.Min(ChunkSizeMeters, grid.DepthMeters - z0);
        float centerHeight = grid.HeightAtSample(x0 + sizeX / 2, z0 + sizeZ / 2) / 100f;
        var center = new Vector3(x0 + sizeX / 2f, centerHeight, z0 + sizeZ / 2f);

        var chunk = new Node3D { Name = $"Chunk_{x0}_{z0}", Position = center };

        var near = new MeshInstance3D
        {
            Name = "Detail",
            Mesh = TerrainMeshBuilder.Build(grid, x0, z0, sizeX, sizeZ, 1, center),
            MaterialOverride = _terrainMaterial,
            VisibilityRangeEnd = DetailDistance,
        };
        var far = new MeshInstance3D
        {
            Name = "Far",
            Mesh = TerrainMeshBuilder.Build(grid, x0, z0, sizeX, sizeZ, FarStep, center),
            MaterialOverride = _terrainMaterial,
            VisibilityRangeBegin = DetailDistance,
        };
        chunk.AddChild(near);
        chunk.AddChild(far);
        _chunkInfo.Add((chunk, x0, z0, sizeX, sizeZ));
        AddScatter(grid, chunk, x0, z0, sizeX, sizeZ, center, scatter);
        return chunk;
    }

    private void AddScatter(TerrainGrid grid, Node3D chunk, int x0, int z0, int sizeX, int sizeZ, Vector3 center, List<ScatterInstance> scatter)
    {
        // Scatter cells are owned by the chunk containing their origin; the last row/column also owns the map edge.
        int maxX = x0 + sizeX >= grid.WidthMeters ? grid.WidthMeters + 1 : x0 + sizeX;
        int maxZ = z0 + sizeZ >= grid.DepthMeters ? grid.DepthMeters + 1 : z0 + sizeZ;
        scatter.Clear();
        TerrainScatter.Collect(grid, x0, z0, maxX, maxZ, scatter);
        if (_cleared is { } cleared)
            scatter.RemoveAll(s => cleared(s.XCm, s.ZCm) && !DenseFieldOnTrail(grid, s) && !UnderFlatDrop(s));
        var treeItems = scatter.Where(s => s.Kind == ScatterKind.Tree).ToList();
        var rockItems = scatter.Where(s => s.Kind == ScatterKind.Rock).ToList();
        bool desert = grid.Settings.Biome == TerrainBiome.Desert;
        Func<ScatterInstance, Color> rockTint = desert ? DesertRockTint : RockTint;
        if (desert)
        {
            var junipers = treeItems.Where(s => s.Variant % 2 == 0).ToList();
            var shrubs = treeItems.Where(s => s.Variant % 2 != 0).ToList();
            if (junipers.Count > 0) chunk.AddChild(BuildInstances("Junipers", _juniperMesh, junipers, center, TreeDrawDistance, TreeTint));
            if (shrubs.Count > 0) chunk.AddChild(BuildInstances("Shrubs", _shrubMesh, shrubs, center, RockDrawDistance, TreeTint));
        }
        else if (treeItems.Count > 0) chunk.AddChild(BuildInstances("Trees", _treeMesh, treeItems, center, TreeDrawDistance, TreeTint));
        // Stone fields get pebbles between their stones (looks only: the rules count the stones).
        var pebbles = desert ? [] : Pebbles(grid, rockItems);
        Func<ScatterInstance, Color> stoneTint = desert ? DesertRockTint : StoneTint;
        foreach (var shape in Enum.GetValues<RockShape>())
        {
            var big = rockItems.Where(s => s.Shape == shape && !IsSmallStone(s)).ToList();
            var small = rockItems.Where(s => s.Shape == shape && IsSmallStone(s)).Concat(pebbles.Where(p => p.Shape == shape)).ToList();
            if (big.Count > 0) chunk.AddChild(BuildInstances($"Rocks {shape}", _rockMeshes[(int)shape], big, center, TreeDrawDistance, rockTint));
            if (small.Count > 0) chunk.AddChild(BuildInstances($"Stones {shape}", _stoneMeshes[(int)shape], small, center, RockDrawDistance, stoneTint, tumble: true));
        }
        _treeCount += treeItems.Count;
        _rockCount += rockItems.Count;
    }

    /// <summary>A stone field stays on a trail: it is what makes the trail black (the trail's own rocks say where). A gravel path clears it.</summary>
    private bool DenseFieldOnTrail(TerrainGrid grid, ScatterInstance item)
    {
        if (item.Kind != ScatterKind.Rock || RockClasses.Of(item) != RockClass.Stone) return false;
        var network = _host.Sim.Network;
        if (network.Nearest(item.XCm, item.ZCm, 300) is not { } near || network.FindWay(near.WayId) is not { Kind: WayKind.Trail }) return false;
        var g = network.Geometry(near.WayId);
        return g.Rocks.Any(r => r.Kind == RockFormation.DenseStones && near.DistanceCm >= g.Distances[r.From] - 300 && near.DistanceCm <= g.Distances[r.To] + 300);
    }

    /// <summary>A flat drop's deck rests on the rock: it stays, also while the drop is only planned.</summary>
    private bool UnderFlatDrop(ScatterInstance item)
    {
        if (item.Kind != ScatterKind.Rock || RockClasses.Of(item) == RockClass.Stone) return false;
        var network = _host.Sim.Network;
        return network.NearestTrail(item.XCm, item.ZCm, RockKeepRadiusCm) is { } near
            && network.FeaturesOn(near.WayId).Any(f => f.Type.Kind == FeatureKind.FlatDrop && near.DistanceCm >= f.StartCm && near.DistanceCm <= f.EndCm);
    }

    private const int RockKeepRadiusCm = 800;

    /// <summary>Smaller rocks are the many small stones: a coarser mesh, and not drawn as far.</summary>
    private const int StoneMaxScale = 1000;

    /// <summary>A rock's turn and size as drawn; stones tumble.</summary>
    private static Basis RockBasis(ScatterInstance item, float scale, bool tumble)
    {
        var basis = new Basis(Vector3.Up, item.YawPermille / 1000f * Mathf.Tau).Scaled(Vector3.One * scale);
        if (tumble)
        {
            // Stones lie every which way.
            uint h = Hash((uint)item.XCm * 40503u ^ (uint)item.ZCm * 9973u);
            var axis = new Vector3(((h & 0xFF) / 255f) - 0.5f, 0, (((h >> 8) & 0xFF) / 255f) - 0.5f);
            if (axis.LengthSquared() > 0.0001f) basis = new Basis(axis.Normalized(), ((h >> 16) & 0xFF) / 255f * 0.6f) * basis;
        }
        return basis;
    }

    /// <summary>A stone (never a boulder or face, however small) drawn with the coarse mesh, tumbled and half sunk.</summary>
    private static bool IsSmallStone(ScatterInstance item) => RockClasses.Of(item) == RockClass.Stone && item.ScalePermille < StoneMaxScale;

    private const int PebblesPerStone = 4;

    /// <summary>Little pebbles round each stone of a stone field, from a hash of its position: looks only, never in the sim.</summary>
    private static List<ScatterInstance> Pebbles(TerrainGrid grid, List<ScatterInstance> rocks)
    {
        var pebbles = new List<ScatterInstance>();
        foreach (var stone in rocks)
        {
            if (RockClasses.Of(stone) != RockClass.Stone || grid.RockFormationAt(stone.XCm, stone.ZCm) != RockFormation.DenseStones) continue;
            for (int k = 0; k < PebblesPerStone; k++)
            {
                uint h = Hash((uint)stone.XCm * 73856093u ^ (uint)stone.ZCm * 19349663u ^ (uint)k * 83492791u);
                int x = stone.XCm + (int)(h % 160) - 80, z = stone.ZCm + (int)((h >> 8) % 160) - 80;
                if (x < 0 || z < 0 || x > grid.WidthMeters * 100 || z > grid.DepthMeters * 100) continue;
                pebbles.Add(new ScatterInstance(ScatterKind.Rock, x, z, grid.HeightAt(x, z), 150 + (int)((h >> 16) % 220), (int)((h >> 4) % 1000),
                    (byte)((h >> 24) % 4), (RockShape)((h >> 26) % RockShapes.LooseCount)));
            }
        }
        return pebbles;
    }

    private static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        return x ^ (x >> 16);
    }

    /// <summary>Stones vary more than big rocks: lighter and darker, a little warmer or cooler.</summary>
    private static Color StoneTint(ScatterInstance item)
    {
        uint h = Hash((uint)item.XCm * 2654435761u ^ (uint)item.ZCm);
        float light = 0.6f + (h & 0xFF) / 255f * 0.38f, warm = ((h >> 8) & 0xFF) / 255f * 0.1f - 0.05f;
        return new Color(light + warm, light, light - warm);
    }

    /// <summary>Red see-through copies of these rocks, on top of everything (the rock the trail tool is asking about).</summary>
    public Node3D BuildRockHighlight(IEnumerable<ScatterInstance> rocks, Material material)
    {
        var root = new Node3D { Name = "RockHighlight" };
        foreach (var rock in rocks)
        {
            float scale = rock.ScalePermille / 1000f * 1.04f;
            bool small = IsSmallStone(rock);
            float sink = small ? 0.05f + RockShapes.HeightCm(rock) / 100f * 0.33f : 0.15f;
            root.AddChild(new MeshInstance3D
            {
                Mesh = (small ? _stoneMeshes : _rockMeshes)[(int)rock.Shape],
                MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Transform = new Transform3D(RockBasis(rock, scale, small), new Vector3(rock.XCm / 100f, rock.YCm / 100f - sink, rock.ZCm / 100f)),
            });
        }
        return root;
    }

    private MultiMeshInstance3D BuildInstances(
        string name, Mesh mesh, List<ScatterInstance> items, Vector3 center, float drawDistance, Func<ScatterInstance, Color> tint, bool tumble = false)
    {
        var multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = mesh,
            InstanceCount = items.Count,
        };
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            float scale = item.ScalePermille / 1000f;
            var basis = RockBasis(item, scale, tumble);
            float sink = tumble ? 0.05f + RockShapes.HeightCm(item) / 100f * 0.33f : 0.15f; // a third of a stone in the ground
            var position = new Vector3(item.XCm / 100f, item.YCm / 100f - sink, item.ZCm / 100f) - center;
            multimesh.SetInstanceTransform(i, new Transform3D(basis, position));
            multimesh.SetInstanceColor(i, tint(item));
        }

        return new MultiMeshInstance3D
        {
            Name = name,
            Multimesh = multimesh,
            MaterialOverride = _scatterMaterial,
            VisibilityRangeEnd = drawDistance,
            VisibilityRangeEndMargin = 20f,
        };
    }

    private static Color TreeTint(ScatterInstance item) => item.Variant switch
    {
        0 => new Color(1f, 1f, 1f),
        1 => new Color(0.85f, 0.95f, 0.85f),
        2 => new Color(1.1f, 1.05f, 0.85f),
        _ => new Color(0.9f, 0.9f, 1f),
    };

    private static Color DesertRockTint(ScatterInstance item) => new(0.98f - item.Variant * 0.03f, 0.50f - item.Variant * 0.02f, 0.31f);

    private static Color RockTint(ScatterInstance item) => new(1f - item.Variant * 0.05f, 1f - item.Variant * 0.05f, 1f - item.Variant * 0.04f);
}
