using System.Diagnostics;
using Bikepark.Sim;
using Bikepark.Sim.Terrain;
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
    private ArrayMesh _rockMesh = null!;
    private StandardMaterial3D _scatterMaterial = null!;
    private Node3D? _chunks;
    private readonly List<(Node3D Node, int X0, int Z0, int SizeX, int SizeZ)> _chunkInfo = [];
    private Func<int, int, bool>? _cleared;
    private SimHost _host = null!;

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
        _rockMesh = ScatterMeshes.CreateRock();
        _scatterMaterial = ScatterMeshes.CreateMaterial();

        _host = GetNode<SimHost>(SimHostPath);
        _host.SimulationReplaced += OnSimulationReplaced;
        OnSimulationReplaced(_host.Sim);
    }

    public override void _Process(double delta)
    {
        // Structures flatten the ground (terrain edits): rebuild the chunks whose samples changed.
        var grid = _host.Sim.Terrain;
        if (Grid is not null && !ReferenceEquals(grid, Grid) && grid.SizeMeters == Grid.SizeMeters)
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
        int xa = Math.Max(0, x0 - 1), xb = Math.Min(a.SizeMeters, x0 + sizeX + 1);
        for (int z = Math.Max(0, z0 - 1); z <= Math.Min(a.SizeMeters, z0 + sizeZ + 1); z++)
        {
            int start = a.Index(xa, z), length = xb - xa + 1;
            if (!a.Heights.Slice(start, length).SequenceEqual(b.Heights.Slice(start, length))) return true;
            if (!a.TreeDensity.Slice(start, length).SequenceEqual(b.TreeDensity.Slice(start, length))) return true;
            if (!a.Rock.Slice(start, length).SequenceEqual(b.Rock.Slice(start, length))) return true;
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
            if (p.X < -50 || p.Z < -50 || p.X > Grid.SizeMeters + 50 || p.Z > Grid.SizeMeters + 50) break;
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

        var scatter = new List<ScatterInstance>();
        int chunkCount = 0;
        for (int z0 = 0; z0 < grid.SizeMeters; z0 += ChunkSizeMeters)
        {
            for (int x0 = 0; x0 < grid.SizeMeters; x0 += ChunkSizeMeters)
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
        int sizeX = Math.Min(ChunkSizeMeters, grid.SizeMeters - x0);
        int sizeZ = Math.Min(ChunkSizeMeters, grid.SizeMeters - z0);
        float centerHeight = grid.HeightAtSample(x0 + sizeX / 2, z0 + sizeZ / 2) / 100f;
        var center = new Vector3(x0 + sizeX / 2f, centerHeight, z0 + sizeZ / 2f);

        var chunk = new Node3D { Name = $"Chunk_{x0}_{z0}", Position = center };

        var near = new MeshInstance3D
        {
            Name = "Detail",
            Mesh = TerrainMeshBuilder.Build(grid, x0, z0, sizeX, sizeZ, 1, center),
            MaterialOverride = _terrainMaterial,
            VisibilityRangeEnd = DetailDistance,
            VisibilityRangeEndMargin = 10f,
        };
        var far = new MeshInstance3D
        {
            Name = "Far",
            Mesh = TerrainMeshBuilder.Build(grid, x0, z0, sizeX, sizeZ, FarStep, center),
            MaterialOverride = _terrainMaterial,
            VisibilityRangeBegin = DetailDistance,
            VisibilityRangeBeginMargin = 10f,
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
        int maxX = x0 + sizeX >= grid.SizeMeters ? grid.SizeMeters + 1 : x0 + sizeX;
        int maxZ = z0 + sizeZ >= grid.SizeMeters ? grid.SizeMeters + 1 : z0 + sizeZ;
        scatter.Clear();
        TerrainScatter.Collect(grid, x0, z0, maxX, maxZ, scatter);
        if (_cleared is { } cleared)
            scatter.RemoveAll(s => cleared(s.XCm, s.ZCm));
        var treeItems = scatter.Where(s => s.Kind == ScatterKind.Tree).ToList();
        var rockItems = scatter.Where(s => s.Kind == ScatterKind.Rock).ToList();
        if (treeItems.Count > 0) chunk.AddChild(BuildInstances("Trees", _treeMesh, treeItems, center, TreeDrawDistance, TreeTint));
        if (rockItems.Count > 0) chunk.AddChild(BuildInstances("Rocks", _rockMesh, rockItems, center, RockDrawDistance, RockTint));
        _treeCount += treeItems.Count;
        _rockCount += rockItems.Count;
    }

    private MultiMeshInstance3D BuildInstances(
        string name, Mesh mesh, List<ScatterInstance> items, Vector3 center, float drawDistance, Func<ScatterInstance, Color> tint)
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
            var basis = new Basis(Vector3.Up, item.YawPermille / 1000f * Mathf.Tau).Scaled(Vector3.One * scale);
            var position = new Vector3(item.XCm / 100f, item.YCm / 100f - 0.15f, item.ZCm / 100f) - center;
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

    private static Color RockTint(ScatterInstance item) => new(1f - item.Variant * 0.05f, 1f - item.Variant * 0.05f, 1f - item.Variant * 0.04f);
}
