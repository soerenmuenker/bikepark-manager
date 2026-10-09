using Bikepark.Game.Terrain;
using Bikepark.Sim;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.State;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Lifts;

/// <summary>
/// Draws lifts and parking lots: platform surfaces, placeholder station buildings, ropes, towers, moving carriers by lift
/// type (gondola cabins, open chairs with bikes hung on the back, T-bar hangers reaching down to the riders on the
/// ground; bike-equipped ones in orange, using the Sim's <see cref="LiftMath.IsBikeCarrier"/>) and parked cars by
/// occupancy. A T-bar has its gravel track on the ground under the line; each rider being pulled up hangs on a hanger
/// that reaches from the rope down to them (following the terrain), empty hangers ride retracted. Carriers only move while the lift runs. A derelict lift is drawn rusty without carriers, one under
/// construction as bare towers. Pure view.
/// </summary>
public partial class LiftView : Node3D
{
    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";
    [Export] public NodePath TerrainPath { get; set; } = "../TerrainView";
    [Export] public NodePath RiderViewPath { get; set; } = "../RiderView";

    private static readonly Color BikeCabin = new(0.95f, 0.50f, 0.10f);
    private static readonly Color PlainCabin = new(0.88f, 0.88f, 0.90f);
    private static readonly Color[] CarColors =
        [new(0.75f, 0.15f, 0.15f), new(0.2f, 0.3f, 0.7f), new(0.85f, 0.85f, 0.85f), new(0.15f, 0.15f, 0.15f), new(0.3f, 0.55f, 0.3f)];

    private SimHost _host = null!;
    private TerrainView _terrain = null!;
    private Node3D? _static;
    private MultiMeshInstance3D _cabins = null!, _chairs = null!, _poles = null!, _bars = null!;
    private Riders.RiderView _riders = null!;

    /// <summary>Length of an empty T-bar hanger (retracted on its spring box).</summary>
    private const float RetractedHanger = 1.8f;
    private MultiMeshInstance3D _cars = null!;
    private StandardMaterial3D _material = null!;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _dirty = true;
    private int _carsShown = -1;

    public override void _Ready()
    {
        _host = GetNode<SimHost>(SimHostPath);
        _terrain = GetNode<TerrainView>(TerrainPath);
        _riders = GetNode<Riders.RiderView>(RiderViewPath);
        ProcessPriority = 10; // after the riders have been placed this frame, so hangers stay attached to them
        _material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.85f };
        _cabins = Instances("Cabins", CabinMesh());
        _chairs = Instances("Chairs", ChairMesh());
        _poles = Instances("HangerPoles", PoleMesh());
        _bars = Instances("HangerBars", BarMesh());
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
        _subscriptions.Add(sim.Events.Subscribe<LiftConstructionStarted>(_ => _dirty = true));
        _subscriptions.Add(sim.Events.Subscribe<LiftReady>(_ => _dirty = true));
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

            var kind = LiftNetwork.FindType(state, lift.TypeId)?.Kind ?? LiftKind.Gondola;
            bool building = lift.ReadyTick > 0 && !lift.Derelict;
            // Rusty while derelict (also while being restored), bare scaffold-orange towers while being built.
            Color Tint(Color c) => lift.Derelict ? c.Lerp(Rust, 0.65f) : c;
            AddMesh(Surface(valley, building ? Gravel : new Color(0.62f, 0.60f, 0.56f)), $"{lift.Name} valley platform");
            AddMesh(Surface(mountain, building ? Gravel : new Color(0.62f, 0.60f, 0.56f)), $"{lift.Name} plateau");
            if (!building)
            {
                AddMesh(Station(valley, kind, Tint), $"{lift.Name} valley station");
                AddMesh(Station(mountain, kind, Tint), $"{lift.Name} mountain station");
                AddMesh(Ropes(valley, mountain, Tint(new Color(0.12f, 0.12f, 0.12f))), $"{lift.Name} ropes");
            }
            AddMesh(Towers(grid, valley, mountain, kind, building ? Scaffold : Tint(new Color(0.45f, 0.47f, 0.50f))), $"{lift.Name} towers");
            if (kind == LiftKind.TBar)
                AddMesh(Track(grid, valley, mountain, lift.Derelict ? Gravel.Lerp(new Color(0.35f, 0.5f, 0.25f), 0.4f) : Gravel), $"{lift.Name} track");
            string owner = LiftNetwork.FindOperator(state, lift.OperatorId)?.Name ?? "park";
            string status = lift.Derelict ? lift.ReadyTick > 0 ? "\nbeing restored" : "\nrusty, out of service" : building ? "\nunder construction" : "";
            AddLabel($"{lift.Name}\n{owner}{status}", LiftShapes.Center(valley, 14f));
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

    private static readonly Color Rust = new(0.55f, 0.30f, 0.15f);
    private static readonly Color Scaffold = new(0.95f, 0.60f, 0.15f);
    private static readonly Color Gravel = new(0.55f, 0.50f, 0.42f);

    /// <summary>
    /// Placeholder station: a hall over the rope end with a bull-wheel housing on top (gondola, chairlift), or a small
    /// hut next to an open bull-wheel frame (T-bar).
    /// </summary>
    private static ArrayMesh Station(TerrainPad pad, LiftKind kind, Func<Color, Color> tint)
    {
        var b = new FlatMeshBuilder();
        if (kind == LiftKind.TBar)
        {
            b.Box(new Vector3(-1.5f, 0, -4.5f), new Vector3(1.5f, 2.6f, -2.0f), tint(new Color(0.55f, 0.40f, 0.25f)));
            b.Box(new Vector3(-1.7f, 2.6f, -4.7f), new Vector3(1.7f, 3.0f, -1.8f), tint(new Color(0.35f, 0.18f, 0.12f)));
            b.Box(new Vector3(-0.3f, 0, -0.3f), new Vector3(0.3f, LiftShapes.CableHeight + 0.4f, 0.3f), tint(new Color(0.45f, 0.47f, 0.50f)));
            b.Box(new Vector3(-0.6f, LiftShapes.CableHeight - 0.2f, -LiftShapes.RopeOffset - 0.4f),
                new Vector3(0.6f, LiftShapes.CableHeight + 0.4f, LiftShapes.RopeOffset + 0.4f), tint(new Color(0.3f, 0.3f, 0.32f)));
            return Placed(b.ToMesh(), pad);
        }
        float wall = kind == LiftKind.Chairlift ? 4.5f : 6.5f;
        b.Box(new Vector3(-6, 0, -5), new Vector3(6, wall, 5), tint(new Color(0.80f, 0.78f, 0.72f)));
        b.Box(new Vector3(-7, wall, -6), new Vector3(7, wall + 0.7f, 6), tint(new Color(0.35f, 0.18f, 0.12f)));
        b.Box(new Vector3(-2, wall + 0.7f, -4), new Vector3(2, LiftShapes.CableHeight + 0.6f, 4), tint(new Color(0.3f, 0.3f, 0.32f)));
        return Placed(b.ToMesh(), pad);
    }

    private static ArrayMesh Ropes(TerrainPad valley, TerrainPad mountain, Color color)
    {
        var b = new FlatMeshBuilder();
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

    /// <summary>The T-bar's gravel track: a 3 m band on the ground under the up-going rope, from station to station.</summary>
    private static ArrayMesh Track(TerrainGrid grid, TerrainPad valley, TerrainPad mountain, Color color)
    {
        var b = new FlatMeshBuilder();
        var right = LiftShapes.Right(valley);
        var a = LiftShapes.Center(valley) + right * LiftShapes.RopeOffset;
        var c = LiftShapes.Center(mountain) + right * LiftShapes.RopeOffset;
        float length = new Vector2(c.X - a.X, c.Z - a.Z).Length();
        int steps = Math.Max(1, (int)(length / 3f));
        Vector3 Ground(Vector3 p) => new(p.X, grid.HeightAt((long)(p.X * 100), (long)(p.Z * 100)) / 100f + 0.12f, p.Z);
        var half = right * 1.5f;
        for (int i = 0; i < steps; i++)
        {
            var p0 = a.Lerp(c, i / (float)steps);
            var p1 = a.Lerp(c, (i + 1) / (float)steps);
            Vector3 l0 = Ground(p0 - half), r0 = Ground(p0 + half), l1 = Ground(p1 - half), r1 = Ground(p1 + half);
            b.Triangle(l0, r0, r1, color, l0 + Vector3.Down);
            b.Triangle(l0, r1, l1, color, l0 + Vector3.Down);
        }
        return b.ToMesh();
    }

    /// <summary>A tower about every 100 m (T-bar: every 60 m), reaching from the ground up to the ropes.</summary>
    private static ArrayMesh Towers(TerrainGrid grid, TerrainPad valley, TerrainPad mountain, LiftKind kind, Color color)
    {
        var b = new FlatMeshBuilder();
        float length = LiftShapes.Center(valley).DistanceTo(LiftShapes.Center(mountain));
        int count = Math.Max(1, (int)(length / (kind == LiftKind.TBar ? 60f : 100f)));
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
        var chairs = new List<(Transform3D Transform, Color Color)>();
        var poles = new List<(Transform3D Transform, Color Color)>();
        var bars = new List<(Transform3D Transform, Color Color)>();

        foreach (var lift in state.Lifts)
        {
            if (!lift.InService) continue; // rusty or with the contractor: no carriers on the rope
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

            // T-bar: riders being pulled up, each on their own hanger reaching down to them (where the rider is drawn).
            var towed = new List<float>(); // their distance along the line from the valley station
            if (type.Kind == LiftKind.TBar)
            {
                var from = LiftShapes.Center(valley);
                var line = LiftShapes.Center(mountain) - from;
                float lineLength2 = Math.Max(1f, line.X * line.X + line.Z * line.Z);
                foreach (var guest in state.Guests)
                {
                    if (guest.Activity != RiderActivity.OnLift || guest.Route.Count == 0) continue;
                    var leg = guest.Route[Math.Clamp(guest.LegIndex, 0, guest.Route.Count - 1)];
                    if (leg.Kind != LegKind.Lift || leg.WayId != lift.Id || !_riders.TryGetPosition(guest.Id, out var rider)) continue;
                    float t = Math.Clamp(((rider.X - from.X) * line.X + (rider.Z - from.Z) * line.Z) / lineLength2, 0f, 1f);
                    var rope = LiftShapes.OnRope(valley, mountain, t, up: true);
                    var grip = rider + Vector3.Up * (_riders.ModelScale * 0.8f); // behind the saddle
                    Hanger(poles, bars, basis, rope, Math.Max(0.5f, rope.Y - grip.Y), BikeCabin);
                    towed.Add(t * length);
                }
            }

            for (int j = 0; j < 2 * perSide; j++)
            {
                float s = sinceDispatch + j * spacing; // along the loop: up the line, then back down
                bool up = s <= length;
                float t = up ? s / length : 2f - s / length;
                if (t < 0f) continue;
                var position = LiftShapes.OnRope(valley, mountain, t, up) + Vector3.Down * LiftShapes.CabinDrop;
                bool bikes = LiftMath.IsBikeCarrier(lift.CarriersDispatched - 1 - j, lift.BikeCarrierPermille);
                if (type.Kind == LiftKind.TBar)
                {
                    // Empty hangers ride retracted; the ones carrying a rider are drawn with the rider above.
                    if (up && towed.Any(d => Math.Abs(d - s) < spacing * 0.6f)) continue;
                    Hanger(poles, bars, basis, LiftShapes.OnRope(valley, mountain, t, up), RetractedHanger, PlainCabin);
                    continue;
                }
                var item = (new Transform3D(basis, position), bikes ? BikeCabin : PlainCabin);
                (type.Kind == LiftKind.Chairlift ? chairs : transforms).Add(item);
            }
        }
        Fill(_cabins, transforms);
        Fill(_chairs, chairs);
        Fill(_poles, poles);
        Fill(_bars, bars);
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

    /// <summary>An open four-seat chair on its hanger, with bike hooks on the back (tinted per instance).</summary>
    private static ArrayMesh ChairMesh()
    {
        var b = new FlatMeshBuilder();
        var dark = new Color(0.2f, 0.2f, 0.2f);
        b.Box(new Vector3(-0.08f, 0.2f, -0.08f), new Vector3(0.08f, LiftShapes.CabinDrop, 0.08f), dark); // hanger
        b.Box(new Vector3(-0.5f, -0.9f, -1.6f), new Vector3(0.5f, -0.75f, 1.6f), Colors.White); // seat
        b.Box(new Vector3(0.4f, -0.75f, -1.6f), new Vector3(0.5f, 0.2f, 1.6f), Colors.White); // back
        b.Box(new Vector3(0.5f, -1.6f, -1.5f), new Vector3(0.7f, 0.0f, 1.5f), dark); // bike rack
        return b.ToMesh();
    }

    /// <summary>A T-bar hanger hanging from <paramref name="rope"/>: a pole of the given length and the crossbar at its end.</summary>
    private static void Hanger(List<(Transform3D, Color)> poles, List<(Transform3D, Color)> bars, Basis basis, Vector3 rope, float length, Color color)
    {
        poles.Add((new Transform3D(basis.Scaled(new Vector3(1, length, 1)), rope), color));
        bars.Add((new Transform3D(basis, rope + Vector3.Down * length), color));
    }

    /// <summary>A unit pole hanging down from the origin (scaled to the hanger's length).</summary>
    private static ArrayMesh PoleMesh()
    {
        var b = new FlatMeshBuilder();
        b.Box(new Vector3(-0.06f, -1f, -0.06f), new Vector3(0.06f, 0f, 0.06f), new Color(0.25f, 0.25f, 0.25f));
        return b.ToMesh();
    }

    /// <summary>The T-bar's crossbar (across the line), tinted per instance.</summary>
    private static ArrayMesh BarMesh()
    {
        var b = new FlatMeshBuilder();
        b.Box(new Vector3(-0.6f, -0.1f, -0.1f), new Vector3(0.6f, 0.1f, 0.1f), Colors.White);
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
