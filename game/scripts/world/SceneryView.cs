using Bikepark.Game.Ways;
using Bikepark.Sim;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.World;

/// <summary>
/// A real map's scenery, built once per world and static from then on: lakes and streams (merged meshes with one animated
/// shader), streets (one merged ribbon), houses (two unit meshes instanced per chunk). Draws what
/// <see cref="TerrainScenery"/> says; nothing here is simulated.
/// </summary>
public partial class SceneryView : Node3D
{
    private const float BuildingDrawDistance = 1500f;
    private const int BuildingChunkMeters = 64;
    private const int StreamStep = 2; // samples are about 1 m apart
    private const float WaterOverGround = 0.05f;
    private const int StreetStepCm = 400;
    private const float StreetLift = 0.07f;

    private static readonly Color Tarmac = WayMeshes.Tarmac;

    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";

    private SimHost _host = null!;
    private Node3D? _content;
    private Mesh? _house, _shed;
    private StandardMaterial3D? _buildingMaterial;

    public override void _Ready()
    {
        _host = GetNode<SimHost>(SimHostPath);
        _host.SimulationReplaced += Rebuild;
        Rebuild(_host.Sim);
    }

    public override void _ExitTree() => _host.SimulationReplaced -= Rebuild;

    private void Rebuild(Simulation sim)
    {
        _content?.QueueFree();
        _content = null;
        var grid = sim.BaseTerrain;
        var scenery = sim.State.Terrain.Heightmap?.Scenery;
        if (scenery is null || grid.Scenery is not { } data) return;

        _content = new Node3D { Name = "Scenery" };
        AddChild(_content);
        AddWater(Streams(data, grid), "Streams", flowSpeed: 0.9f, rippleScale: 0.8f);
        AddWater(Lakes(data), "Lakes", flowSpeed: 0f, rippleScale: 0.28f);
        AddStreets(grid, scenery);
        AddBuildings(grid, scenery);
    }

    private void AddWater(ArrayMesh? mesh, string name, float flowSpeed, float rippleScale)
    {
        if (mesh is null) return;
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/water.gdshader") };
        material.SetShaderParameter("flow_speed", flowSpeed);
        material.SetShaderParameter("ripple_scale", rippleScale);
        _content!.AddChild(new MeshInstance3D { Name = name, Mesh = mesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    private static ArrayMesh? Streams(TerrainSceneryData data, TerrainGrid grid)
    {
        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        foreach (var stream in data.Streams)
        {
            // A bit wider than the bed, so the banks hide the edge.
            float half = Math.Max(stream.WidthCm, 100) / 200f + 0.6f;
            int first = vertices.Count, count = 0;
            float along = 0;
            for (int i = 0; ; i = Math.Min(i + StreamStep, stream.Points.Count - 1))
            {
                var p = stream.Points[i];
                var a = stream.Points[Math.Max(0, i - StreamStep)];
                var b = stream.Points[Math.Min(stream.Points.Count - 1, i + StreamStep)];
                var dir = new Vector2(b.X - a.X, b.Z - a.Z).Normalized();
                var side = new Vector2(-dir.Y, dir.X) * half;
                var center = new Vector2(p.X / 100f, p.Z / 100f);
                // On a steep bed the level runs ahead of the ground: the ribbon stays on it, it never hangs in the air.
                float Draped(Vector2 at) => Math.Min(p.LevelCm / 100f, grid.HeightAt((long)(at.X * 100), (long)(at.Y * 100)) / 100f + WaterOverGround);
                vertices.Add(new Vector3(center.X - side.X, Draped(center - side), center.Y - side.Y));
                vertices.Add(new Vector3(center.X + side.X, Draped(center + side), center.Y + side.Y));
                uvs.Add(new Vector2(along, 0));
                uvs.Add(new Vector2(along, half * 2));
                along += StreamStep;
                count++;
                if (i == stream.Points.Count - 1) break;
            }
            for (int k = 0; k + 1 < count; k++)
            {
                int l0 = first + k * 2;
                indices.AddRange([l0, l0 + 1, l0 + 2, l0 + 1, l0 + 3, l0 + 2]);
            }
        }
        return Mesh(vertices, uvs, indices);
    }

    private static ArrayMesh? Lakes(TerrainSceneryData data)
    {
        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        foreach (var lake in data.Lakes)
        {
            var outline = lake.Outline.Select(p => new Vector2(p.X / 100f, p.Z / 100f)).ToArray();
            int first = vertices.Count;
            foreach (var p in outline)
            {
                vertices.Add(new Vector3(p.X, lake.LevelCm / 100f, p.Y));
                uvs.Add(p);
            }
            foreach (int index in Geometry2D.TriangulatePolygon(outline))
                indices.Add(first + index);
        }
        return Mesh(vertices, uvs, indices);
    }

    private static ArrayMesh? Mesh(List<Vector3> vertices, List<Vector2> uvs, List<int> indices)
    {
        if (indices.Count == 0) return null;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Godot.Mesh.ArrayType.Normal] = Enumerable.Repeat(Vector3.Up, vertices.Count).ToArray();
        arrays[(int)Godot.Mesh.ArrayType.TexUV] = uvs.ToArray();
        arrays[(int)Godot.Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private void AddStreets(TerrainGrid grid, TerrainScenery scenery)
    {
        var vertices = new List<Vector3>();
        var colors = new List<Color>();
        var indices = new List<int>();
        foreach (var street in scenery.Streets)
        {
            float half = street.WidthCm / 200f;
            var line = Walk(street.Points);
            int first = vertices.Count;
            for (int i = 0; i < line.Count; i++)
            {
                var dir = (line[Math.Min(line.Count - 1, i + 1)] - line[Math.Max(0, i - 1)]).Normalized();
                var side = new Vector2(-dir.Y, dir.X) * half;
                foreach (var p in new[] { line[i] - side, line[i] + side })
                {
                    float y = grid.HeightAt((long)(p.X * 100), (long)(p.Y * 100)) / 100f + StreetLift;
                    vertices.Add(new Vector3(p.X, y, p.Y));
                    colors.Add(Tarmac);
                }
            }
            for (int k = 0; k + 1 < line.Count; k++)
            {
                int l0 = first + k * 2;
                indices.AddRange([l0, l0 + 1, l0 + 2, l0 + 1, l0 + 3, l0 + 2]);
            }
        }
        if (indices.Count == 0) return;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Godot.Mesh.ArrayType.Normal] = Enumerable.Repeat(Vector3.Up, vertices.Count).ToArray();
        arrays[(int)Godot.Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Godot.Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
        _content!.AddChild(new MeshInstance3D { Name = "Streets", Mesh = mesh, MaterialOverride = WayMeshes.CreateMaterial(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    private static List<Vector2> Walk(IReadOnlyList<PointCm> points)
    {
        var line = new List<Vector2> { new(points[0].X / 100f, points[0].Z / 100f) };
        for (int i = 1; i < points.Count; i++)
        {
            var a = new Vector2(points[i - 1].X, points[i - 1].Z);
            var b = new Vector2(points[i].X, points[i].Z);
            int steps = Math.Max(1, (int)(a.DistanceTo(b) / StreetStepCm));
            for (int s = 1; s <= steps; s++)
                line.Add(a.Lerp(b, s / (float)steps) / 100f);
        }
        return line;
    }

    private void AddBuildings(TerrainGrid grid, TerrainScenery scenery)
    {
        if (scenery.Buildings.Count == 0) return;
        _house ??= HouseMesh();
        _shed ??= ShedMesh();
        _buildingMaterial ??= new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.9f };

        foreach (var chunk in scenery.Buildings
                     .GroupBy(b => (b.CenterX / (BuildingChunkMeters * 100), b.CenterZ / (BuildingChunkMeters * 100), b.FlatRoof))
                     .OrderBy(g => g.Key))
        {
            var items = chunk.ToList();
            var center = new Vector3(chunk.Key.Item1 + 0.5f, 0, chunk.Key.Item2 + 0.5f) * BuildingChunkMeters;
            center.Y = grid.HeightAt((long)(center.X * 100), (long)(center.Z * 100)) / 100f;
            var multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = chunk.Key.FlatRoof ? _shed : _house,
                InstanceCount = items.Count,
            };
            for (int i = 0; i < items.Count; i++)
            {
                multimesh.SetInstanceTransform(i, Placed(grid, items[i], center));
                multimesh.SetInstanceColor(i, Tint(items[i]));
            }
            _content!.AddChild(new MultiMeshInstance3D
            {
                Name = "Buildings",
                Multimesh = multimesh,
                MaterialOverride = _buildingMaterial,
                Position = center,
                VisibilityRangeEnd = BuildingDrawDistance,
                VisibilityRangeEndMargin = 50f,
            });
        }
    }

    /// <summary>The unit mesh stretched over the footprint. The walls reach down to the lowest corner, so nothing floats on a slope.</summary>
    private static Transform3D Placed(TerrainGrid grid, SceneryBuilding b, Vector3 chunkCenter)
    {
        var axis = new Vector3(b.DirX, 0, b.DirZ).Normalized();
        var across = new Vector3(-axis.Z, 0, axis.X);
        var middle = new Vector3(b.CenterX, 0, b.CenterZ) / 100f;
        float low = float.MaxValue, high = float.MinValue;
        foreach (var (l, w) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
        {
            var corner = middle + axis * (l * b.HalfLengthCm / 100f) + across * (w * b.HalfWidthCm / 100f);
            float y = grid.HeightAt((long)(corner.X * 100), (long)(corner.Z * 100)) / 100f;
            low = Math.Min(low, y);
            high = Math.Max(high, y);
        }
        // Standing on the uphill corner's ground, the height counts from there.
        float height = high - low + b.HeightCm / 100f;
        var basis = new Basis(axis * (b.HalfLengthCm / 100f), Vector3.Up * height, across * (b.HalfWidthCm / 100f));
        return new Transform3D(basis, new Vector3(middle.X, low - 0.1f, middle.Z) - chunkCenter);
    }

    /// <summary>Slightly different per house, always the same for this house.</summary>
    private static Color Tint(SceneryBuilding b)
    {
        uint h = unchecked((uint)(b.CenterX * 73856093 ^ b.CenterZ * 19349663));
        float f = 0.86f + (h % 15) / 100f;
        return new Color(f, f * (0.97f + (h >> 8) % 4 / 100f), f * (0.92f + (h >> 12) % 8 / 100f));
    }

    private static readonly Color Wall = new Color(0.86f, 0.82f, 0.74f).SrgbToLinear();
    private static readonly Color Timber = new Color(0.45f, 0.31f, 0.20f).SrgbToLinear();
    private static readonly Color Roof = new Color(0.40f, 0.24f, 0.19f).SrgbToLinear();

    /// <summary>Unit house: x and z in -1..1, y in 0..1. Plaster below, timber above (the region's farmhouses), gable along x.</summary>
    private static ArrayMesh HouseMesh()
    {
        const float wall = 0.42f, eaves = 0.62f, over = 1.1f;
        var m = new FlatMeshBuilder();
        m.Box(new Vector3(-1, 0, -1), new Vector3(1, wall, 1), Wall);
        m.Box(new Vector3(-1, wall, -1), new Vector3(1, eaves, 1), Timber);
        var inside = new Vector3(0, eaves, 0);
        Vector3 Eave(float x, float z) => new(x, eaves, z);
        Vector3 Ridge(float x) => new(x, 1f, 0);
        foreach (float side in new[] { -over, over })
        {
            m.Triangle(Eave(-over, side), Eave(over, side), Ridge(over), Roof, inside);
            m.Triangle(Eave(-over, side), Ridge(over), Ridge(-over), Roof, inside);
        }
        foreach (float end in new[] { -1f, 1f })
            m.Triangle(Eave(end, -1), Eave(end, 1), Ridge(end), Timber, inside);
        return m.ToMesh();
    }

    /// <summary>Unit shed: a plain timber box with a flat roof.</summary>
    private static ArrayMesh ShedMesh()
    {
        var m = new FlatMeshBuilder();
        m.Box(new Vector3(-1, 0, -1), new Vector3(1, 0.92f, 1), Timber);
        m.Box(new Vector3(-1.06f, 0.92f, -1.06f), new Vector3(1.06f, 1, 1.06f), Roof);
        return m.ToMesh();
    }
}
