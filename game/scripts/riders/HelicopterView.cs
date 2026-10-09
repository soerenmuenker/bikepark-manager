using Bikepark.Sim.State;
using Godot;

namespace Bikepark.Game.Riders;

/// <summary>
/// The rescue helicopter (pure view). For each seriously injured rider it flies in over the last minutes before
/// <see cref="Guest.RescueAtTick"/>, hovers over the crash, and flies off once the rider is gone. Positions come from
/// <see cref="RiderView"/> (where the rider lies); timing from the sim clock.
/// </summary>
public partial class HelicopterView : Node3D
{
    /// <summary>Game minutes the approach takes (it arrives at the rescue tick minus <see cref="HoverMinutes"/>).</summary>
    private const float ApproachMinutes = 4f;
    private const float HoverMinutes = 3f;
    private const float HoverHeight = 18f;
    private const float DepartSeconds = 8f;
    private static readonly Vector3 ApproachOffset = new(-260f, 140f, -260f);

    private readonly SimHost _host;
    private readonly RiderView _riders;
    private readonly Dictionary<int, (Node3D Model, Vector3 Hover)> _active = [];
    private readonly List<(Node3D Model, Vector3 From, float Time)> _departing = [];
    private float _rotor;

    public HelicopterView() : this(null!, null!) { }

    public HelicopterView(SimHost host, RiderView riders)
    {
        _host = host;
        _riders = riders;
        Name = "Helicopters";
    }

    public override void _Process(double delta)
    {
        if (_host is null) return;
        var state = _host.Sim.State;
        float now = state.Tick + _host.InterpolationAlpha;
        _rotor += (float)delta * 25f;

        var waiting = new HashSet<int>();
        foreach (var guest in state.Guests)
        {
            if (guest.Activity != RiderActivity.Injured || !_riders.TryGetPosition(guest.Id, out var crash)) continue;
            float arrive = guest.RescueAtTick - HoverMinutes;
            float t = 1f - Math.Clamp((arrive - now) / ApproachMinutes, 0f, 1f);
            if (t <= 0f) continue; // still on its way, out of sight
            waiting.Add(guest.Id);
            var hover = crash + Vector3.Up * HoverHeight;
            if (!_active.TryGetValue(guest.Id, out var heli))
            {
                heli = (CreateModel(), hover);
                AddChild(heli.Model);
            }
            _active[guest.Id] = (heli.Model, hover);
            var from = hover + ApproachOffset;
            float eased = t * t * (3f - 2f * t);
            var position = from.Lerp(hover, eased);
            Place(heli.Model, position, hover - from);
        }

        // Gone from the injured list: the rider is on board, the helicopter flies off.
        foreach (int id in _active.Keys.Where(id => !waiting.Contains(id)).ToList())
        {
            var (model, hover) = _active[id];
            _active.Remove(id);
            _departing.Add((model, hover, 0f));
        }
        for (int i = _departing.Count - 1; i >= 0; i--)
        {
            var (model, from, time) = _departing[i];
            time += (float)delta;
            if (time >= DepartSeconds)
            {
                model.QueueFree();
                _departing.RemoveAt(i);
                continue;
            }
            float t = time / DepartSeconds;
            var to = from - ApproachOffset;
            Place(model, from.Lerp(to, t * t), to - from);
            _departing[i] = (model, from, time);
        }
    }

    private void Place(Node3D model, Vector3 position, Vector3 heading)
    {
        model.Position = position;
        var flat = new Vector3(heading.X, 0, heading.Z);
        if (flat.LengthSquared() > 0.01f) model.LookAt(position + flat, Vector3.Up);
        if (model.GetNodeOrNull<Node3D>("Rotor") is { } rotor) rotor.Rotation = new Vector3(0, _rotor, 0);
    }

    /// <summary>A red-and-white rescue helicopter, drawn larger than life like the riders.</summary>
    private static Node3D CreateModel()
    {
        var root = new Node3D();
        var red = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.12f, 0.10f), Roughness = 0.5f };
        var white = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.95f, 0.95f), Roughness = 0.5f };
        var dark = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.12f, 0.14f), Roughness = 0.6f };
        void Box(string name, Vector3 size, Vector3 at, Material material, Node3D? parent = null) =>
            (parent ?? root).AddChild(new MeshInstance3D { Name = name, Mesh = new BoxMesh { Size = size }, Position = at, MaterialOverride = material });

        Box("Cabin", new Vector3(3.2f, 3.0f, 6.0f), Vector3.Zero, red);
        Box("Window", new Vector3(3.0f, 1.4f, 1.2f), new Vector3(0, 0.6f, -2.6f), dark);
        Box("Stripe", new Vector3(3.3f, 0.6f, 4.0f), new Vector3(0, -0.6f, 0.5f), white);
        Box("Tail", new Vector3(0.7f, 0.8f, 7.0f), new Vector3(0, 0.6f, 6.2f), red);
        Box("Fin", new Vector3(0.3f, 2.4f, 1.2f), new Vector3(0, 1.6f, 9.4f), white);
        Box("SkidL", new Vector3(0.25f, 0.25f, 6.5f), new Vector3(-1.5f, -2.1f, 0), dark);
        Box("SkidR", new Vector3(0.25f, 0.25f, 6.5f), new Vector3(1.5f, -2.1f, 0), dark);
        var rotor = new Node3D { Name = "Rotor", Position = new Vector3(0, 2.0f, 0) };
        root.AddChild(rotor);
        Box("BladeA", new Vector3(16f, 0.12f, 0.6f), Vector3.Zero, dark, rotor);
        Box("BladeB", new Vector3(0.6f, 0.12f, 16f), Vector3.Zero, dark, rotor);
        return root;
    }
}
