using Bikepark.Game.Terrain;
using Bikepark.Sim;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Lifts;

/// <summary>
/// Draws lifts and parking lots: platform surfaces, placeholder station buildings, ropes, towers, moving cabins
/// (bike-equipped cabins in orange, using the Sim's <see cref="LiftMath.IsBikeCarrier"/>) and parked cars by
/// occupancy. Cabins only move while the park is open. Pure view.
/// </summary>
public partial class LiftView : Node3D
{
    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";
    [Export] public NodePath TerrainPath { get; set; } = "../TerrainView";

    private static readonly Color BikeCabin = new(0.95f, 0.50f, 0.10f);
    private static readonly Color PlainCabin = new(0.88f, 0.88f, 0.90f);
    private static readonly Color[] CarColors =
        [new(0.75f, 0.15f, 0.15f), new(0.2f, 0.3f, 0.7f), new(0.85f, 0.85f, 0.85f), new(0.15f, 0.15f, 0.15f), new(0.3f, 0.55f, 0.3f)];

    private SimHost _host = null!;
    private TerrainView _terrain = null!;
    private Node3D? _static;
    private MultiMeshInstance3D _cabins = null!;
    private MultiMeshInstance3D _cars = null!;
    private StandardMaterial3D _material = null!;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _dirty = true;
    private int _carsShown = -1;

    public override void _Ready()
    {
        _host = GetNode<SimHost>(SimHostPath);
        _terrain = GetNode<TerrainView>(TerrainPath);
        _material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.85f };
        _cabins = Instances("Cabins", CabinMesh());
        _cars = Instances("Cars", CarMesh());
        _terrain.TerrainBuilt += _ => _dirty = true;
        _host.SimulationReplaced += OnSimulationReplaced;
        OnSimulationReplaced(_host.Sim);
    }

    public override void _ExitTree()
    {
        _host.SimulationReplaced -= OnSimulationReplaced;
        foreach (var s in _subscriptions) s.Dispose();
    }

    private void OnSimulationReplaced(Simulation sim)
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        _subscriptions.Add(sim.Events.Subscribe<LiftBuilt>(_ => _dirty = true));
        _subscriptions.Add(sim.Events.Subscribe<LiftDeleted>(_ => _dirty = true));
        _subscriptions.Add(sim.Events.Subscribe<ParkingLotBuilt>(_ => _dirty = true));
        _subscriptions.Add(sim.Events.Subscribe<ParkingLotDeleted>(_ => _dirty = true));
        _dirty = true;
    }

    public override void _Process(double delta)
    {
        var sim = _host.Sim;
        if (_dirty)
        {
            _dirty = false;
            RebuildStatic(sim);
            _carsShown = -1;
        }
        UpdateCabins(sim);
        UpdateCars(sim);
    }

    // ---------------------------------------------------------------- static parts

    private void RebuildStatic(Simulation sim)
    {
        _static?.QueueFree();
        _static = new Node3D { Name = "Structures" };
        AddChild(_static);
        var state = sim.State;
        var grid = sim.Terrain;

        foreach (var lot in state.ParkingLots)
        {
            if (LiftNetwork.Pad(state, lot.TerrainEditId) is not { } pad) continue;
            AddMesh(Surface(pad, new Color(0.22f, 0.22f, 0.24f)), lot.Name);
            AddLabel($"{lot.Name}\n{lot.Spaces} spaces", LiftShapes.Center(pad, 6f));
        }

        foreach (var lift in state.Lifts)
        {
            var valley = LiftNetwork.Pad(state, lift.Valley.TerrainEditId);
            var mountain = LiftNetwork.Pad(state, lift.Mountain.TerrainEditId);
            if (valley is null || mountain is null) continue;

            AddMesh(Surface(valley, new Color(0.62f, 0.60f, 0.56f)), $"{lift.Name} valley platform");
            AddMesh(Surface(mountain, new Color(0.62f, 0.60f, 0.56f)), $"{lift.Name} plateau");
            AddMesh(Station(valley), $"{lift.Name} valley station");
            AddMesh(Station(mountain), $"{lift.Name} mountain station");
            AddMesh(Ropes(valley, mountain), $"{lift.Name} ropes");
            AddMesh(Towers(grid, valley, mountain), $"{lift.Name} towers");
            string owner = LiftNetwork.FindOperator(state, lift.OperatorId)?.Name ?? "park";
            AddLabel($"{lift.Name}\n{owner}", LiftShapes.Center(valley, 14f));
            AddLabel("Plateau", LiftShapes.Center(mountain, 12f));
        }
    }

    /// <summary>A flat quad over the pad's flat area.</summary>
    private static ArrayMesh Surface(TerrainPad pad, Color color)
    {
        var c = pad.Corners();
        float y = pad.TargetHeightCm / 100f + 0.06f;
        var v = c.Select(p => new Vector3(p.X / 100f, y, p.Z / 100f)).ToArray();
        var b = new FlatMeshBuilder();
        var inside = v[0] + Vector3.Down;
        b.Triangle(v[0], v[1], v[2], color, inside);
        b.Triangle(v[0], v[2], v[3], color, inside);
        return b.ToMesh();
    }

    /// <summary>Placeholder station: a hall over the rope end with a bull-wheel housing on top.</summary>
    private static ArrayMesh Station(TerrainPad pad)
    {
        var b = new FlatMeshBuilder();
        b.Box(new Vector3(-6, 0, -5), new Vector3(6, 6.5f, 5), new Color(0.80f, 0.78f, 0.72f));
        b.Box(new Vector3(-7, 6.5f, -6), new Vector3(7, 7.2f, 6), new Color(0.35f, 0.18f, 0.12f));
        b.Box(new Vector3(-2, 7.2f, -4), new Vector3(2, LiftShapes.CableHeight + 0.6f, 4), new Color(0.3f, 0.3f, 0.32f));
        return Placed(b.ToMesh(), pad);
    }

    private static ArrayMesh Ropes(TerrainPad valley, TerrainPad mountain)
    {
        var b = new FlatMeshBuilder();
        var color = new Color(0.12f, 0.12f, 0.12f);
        foreach (bool up in new[] { true, false })
        {
            var a = LiftShapes.OnRope(valley, mountain, 0f, up);
            var c = LiftShapes.OnRope(valley, mountain, 1f, up);
            var side = LiftShapes.Right(valley) * 0.06f;
            b.Triangle(a - side, a + side, c + side, color, a + Vector3.Down);
            b.Triangle(a - side, c + side, c - side, color, a + Vector3.Down);
            b.Triangle(a + Vector3.Down * 0.08f, a, c, color, a + side);
            b.Triangle(a + Vector3.Down * 0.08f, c, c + Vector3.Down * 0.08f, color, a + side);
        }
        return b.ToMesh();
    }

    /// <summary>A tower about every 100 m, reaching from the ground up to the ropes.</summary>
    private static ArrayMesh Towers(TerrainGrid grid, TerrainPad valley, TerrainPad mountain)
    {
        var b = new FlatMeshBuilder();
        var color = new Color(0.45f, 0.47f, 0.50f);
        float length = LiftShapes.Center(valley).DistanceTo(LiftShapes.Center(mountain));
        int count = Math.Max(1, (int)(length / 100f));
        var right = LiftShapes.Right(valley);
        for (int i = 1; i <= count; i++)
        {
            float t = i / (count + 1f);
            var top = LiftShapes.Center(valley, LiftShapes.CableHeight).Lerp(LiftShapes.Center(mountain, LiftShapes.CableHeight), t);
            float ground = grid.HeightAt((long)(top.X * 100), (long)(top.Z * 100)) / 100f;
            var foot = new Vector3(top.X, ground - 0.5f, top.Z);
            BoxBetween(b, foot - right * 0.5f - new Vector3(0, 0, 0), top + Vector3.Up * 0.5f, 0.6f, color);
            BoxBetween(b, top - right * (LiftShapes.RopeOffset + 0.5f), top + right * (LiftShapes.RopeOffset + 0.5f) + Vector3.Up * 0.5f, 0.4f, color);
        }
        return b.ToMesh();
    }

    private static void BoxBetween(FlatMeshBuilder b, Vector3 a, Vector3 c, float thickness, Color color)
    {
        var min = new Vector3(Math.Min(a.X, c.X), Math.Min(a.Y, c.Y), Math.Min(a.Z, c.Z)) - new Vector3(thickness, 0, thickness) / 2;
        var max = new Vector3(Math.Max(a.X, c.X), Math.Max(a.Y, c.Y), Math.Max(a.Z, c.Z)) + new Vector3(thickness, 0, thickness) / 2;
        b.Box(min, max, color);
    }

    /// <summary>Moves a mesh built around the origin (X along the pad, Z across) onto the pad.</summary>
    private static ArrayMesh Placed(ArrayMesh mesh, TerrainPad pad)
    {
        var arrays = mesh.SurfaceGetArrays(0);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        var forward = LiftShapes.Forward(pad);
        var right = LiftShapes.Right(pad);
        var origin = LiftShapes.Center(pad);
        for (int i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            vertices[i] = origin + forward * v.X + Vector3.Up * v.Y + right * v.Z;
            var n = normals[i];
            normals[i] = (forward * n.X + Vector3.Up * n.Y + right * n.Z).Normalized();
        }
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        var placed = new ArrayMesh();
        placed.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return placed;
    }

    // ---------------------------------------------------------------- moving parts

    private void UpdateCabins(Simulation sim)
    {
        var state = sim.State;
        var transforms = new List<(Transform3D Transform, Color Color)>();

        foreach (var lift in state.Lifts)
        {
            float alpha = ParkSchedule.LiftRunning(state, lift, state.Tick) ? _host.InterpolationAlpha : 0f;
            var type = LiftNetwork.FindType(state, lift.TypeId);
            var valley = LiftNetwork.Pad(state, lift.Valley.TerrainEditId);
            var mountain = LiftNetwork.Pad(state, lift.Mountain.TerrainEditId);
            if (type is null || valley is null || mountain is null) continue;

            float length = LiftMath.Line(valley, mountain).LengthCm / 100f;
            float spacing = type.SpeedCmPerS / 100f * type.IntervalSeconds;
            int perSide = Math.Max(1, (int)(length / spacing));
            // Distance the newest cabin has travelled since it left the valley station.
            float intervalMs = type.IntervalSeconds * 1000f;
            float sinceDispatch = (alpha * 60_000f + lift.DispatchRemainderMs) % intervalMs / 1000f * type.SpeedCmPerS / 100f;
            var basis = Basis.LookingAt(LiftShapes.Forward(valley), Vector3.Up);

            for (int j = 0; j < 2 * perSide; j++)
            {
                float s = sinceDispatch + j * spacing; // along the loop: up the line, then back down
                bool up = s <= length;
                float t = up ? s / length : 2f - s / length;
                if (t < 0f) continue;
                var position = LiftShapes.OnRope(valley, mountain, t, up) + Vector3.Down * LiftShapes.CabinDrop;
                bool bikes = LiftMath.IsBikeCarrier(lift.CarriersDispatched - 1 - j, lift.BikeCarrierPermille);
                transforms.Add((new Transform3D(basis, position), bikes ? BikeCabin : PlainCabin));
            }
        }
        Fill(_cabins, transforms);
    }

    private void UpdateCars(Simulation sim)
    {
        var state = sim.State;
        int capacity = LiftMath.ParkingCapacity(state) ?? 0;
        int cars = capacity == 0 ? 0 : (int)Math.Ceiling(state.Guests.Count * 1000.0 / state.LiftRules.GuestsPerCarPermille);
        if (cars == _carsShown) return;
        _carsShown = cars;

        var transforms = new List<(Transform3D Transform, Color Color)>();
        int remaining = cars;
        foreach (var lot in state.ParkingLots)
        {
            if (LiftNetwork.Pad(state, lot.TerrainEditId) is not { } pad) continue;
            var forward = LiftShapes.Forward(pad);
            var right = LiftShapes.Right(pad);
            var basis = Basis.LookingAt(right, Vector3.Up);
            int columns = Math.Max(1, (int)(pad.HalfLengthCm * 2 / 100f / 2.7f));
            int rows = Math.Max(1, (int)(pad.HalfWidthCm * 2 / 100f / 6.5f));
            for (int slot = 0; slot < Math.Min(lot.Spaces, columns * rows) && remaining > 0; slot++, remaining--)
            {
                float along = -pad.HalfLengthCm / 100f + 1.5f + slot % columns * 2.7f;
                float across = -pad.HalfWidthCm / 100f + 3.2f + slot / columns * 6.5f;
                var position = LiftShapes.Center(pad, 0.1f) + forward * along + right * across;
                transforms.Add((new Transform3D(basis, position), CarColors[(slot * 7 + lot.Id) % CarColors.Length]));
            }
        }
        Fill(_cars, transforms);
    }

    // ---------------------------------------------------------------- helpers

    private MultiMeshInstance3D Instances(string name, Mesh mesh)
    {
        var instances = new MultiMeshInstance3D
        {
            Name = name,
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = mesh },
            MaterialOverride = _material,
        };
        AddChild(instances);
        return instances;
    }

    private static void Fill(MultiMeshInstance3D instances, List<(Transform3D Transform, Color Color)> items)
    {
        var mm = instances.Multimesh;
        if (mm.InstanceCount < items.Count)
            mm.InstanceCount = Math.Max(items.Count, Math.Max(32, mm.InstanceCount * 2));
        for (int i = 0; i < items.Count; i++)
        {
            mm.SetInstanceTransform(i, items[i].Transform);
            mm.SetInstanceColor(i, items[i].Color);
        }
        mm.VisibleInstanceCount = items.Count;
    }

    private void AddMesh(ArrayMesh mesh, string name) =>
        _static!.AddChild(new MeshInstance3D { Name = name, Mesh = mesh, MaterialOverride = _material });

    private void AddLabel(string text, Vector3 position) =>
        _static!.AddChild(new Label3D
        {
            Text = text,
            Position = position,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize = 48,
            PixelSize = 0.03f,
            OutlineSize = 12,
            NoDepthTest = true,
            VisibilityRangeEnd = 900f,
        });

    /// <summary>Cabin hanging from its grip, about 2.4 m tall (white body, tinted per instance; dark grip).</summary>
    private static ArrayMesh CabinMesh()
    {
        var b = new FlatMeshBuilder();
        b.Box(new Vector3(-1.1f, -1.2f, -1.0f), new Vector3(1.1f, 1.2f, 1.0f), Colors.White);
        b.Box(new Vector3(-0.1f, 1.2f, -0.1f), new Vector3(0.1f, LiftShapes.CabinDrop, 0.1f), new Color(0.2f, 0.2f, 0.2f));
        return b.ToMesh();
    }

    private static ArrayMesh CarMesh()
    {
        var b = new FlatMeshBuilder();
        b.Box(new Vector3(-0.9f, 0.2f, -2.2f), new Vector3(0.9f, 0.9f, 2.2f), Colors.White);
        b.Box(new Vector3(-0.8f, 0.9f, -1.1f), new Vector3(0.8f, 1.45f, 1.0f), Colors.White);
        return b.ToMesh();
    }
}
