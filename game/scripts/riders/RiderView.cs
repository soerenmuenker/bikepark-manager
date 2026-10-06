using Bikepark.Game.Camera;
using Bikepark.Game.Ways;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Riders;

/// <summary>
/// Draws riders that are on the way network as instanced low-poly bike + rider models, smoothly interpolated between
/// simulation ticks (previous route progress is captured in <see cref="SimHost.BeforeStep"/>). Jersey color shows
/// skill (green/blue/red/black like trail ratings). F makes the camera follow the next riding rider.
/// </summary>
public partial class RiderView : Node3D
{
    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";
    [Export] public NodePath CameraPath { get; set; } = "../RtsCamera";

    /// <summary>Riders are drawn larger than life so they read from an RTS camera.</summary>
    [Export] public float ModelScale { get; set; } = 3.5f;

    private SimHost _host = null!;
    private RtsCamera _camera = null!;
    private MultiMeshInstance3D _instances = null!;
    private readonly Dictionary<int, (List<RouteLeg> Route, long Progress)> _previous = [];
    private readonly Dictionary<int, Vector3> _positions = [];
    private int _followId;

    public int VisibleRiders { get; private set; }

    public string? FollowedRider { get; private set; }

    public override void _Ready()
    {
        _host = GetNode<SimHost>(SimHostPath);
        _camera = GetNode<RtsCamera>(CameraPath);
        _instances = new MultiMeshInstance3D
        {
            Name = "Riders",
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = CreateRiderMesh(),
            },
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.8f },
        };
        AddChild(_instances);
        _host.BeforeStep += Snapshot;
    }

    public override void _ExitTree() => _host.BeforeStep -= Snapshot;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F })
        {
            FollowNext();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>Starts following the next rider on the network (by id), or stops if there is none.</summary>
    public void FollowNext()
    {
        var riding = _host.Sim.State.Guests.Where(IsOnNetwork).Select(g => g.Id).Order().ToList();
        if (riding.Count == 0)
        {
            StopFollowing();
            return;
        }
        _followId = riding.FirstOrDefault(id => id > _followId, riding[0]);
        _camera.Follow = () => _positions.TryGetValue(_followId, out var p) ? p : null;
    }

    public void StopFollowing()
    {
        _followId = 0;
        _camera.Follow = null;
    }

    private void Snapshot()
    {
        _previous.Clear();
        foreach (var g in _host.Sim.State.Guests)
            if (IsOnNetwork(g))
                _previous[g.Id] = (g.Route, g.RouteProgressCm);
    }

    public override void _Process(double delta)
    {
        var sim = _host.Sim;
        var network = sim.Network;
        float alpha = _host.InterpolationAlpha;
        var mm = _instances.Multimesh;
        _positions.Clear();

        int count = 0;
        foreach (var guest in sim.State.Guests)
        {
            if (!IsOnNetwork(guest)) continue;
            long progress = guest.RouteProgressCm;
            if (_previous.TryGetValue(guest.Id, out var prev) && ReferenceEquals(prev.Route, guest.Route))
                progress = prev.Progress + (long)((guest.RouteProgressCm - prev.Progress) * alpha);

            if (!TryLocate(network, guest.Route, progress, out var position, out var forward)) continue;

            if (count >= mm.InstanceCount)
                mm.InstanceCount = Math.Max(64, mm.InstanceCount * 2);
            var basis = Basis.LookingAt(forward, Vector3.Up).Scaled(Vector3.One * ModelScale);
            mm.SetInstanceTransform(count, new Transform3D(basis, position));
            mm.SetInstanceColor(count, SkillColor(guest.Skill));
            _positions[guest.Id] = position;
            count++;
        }
        mm.VisibleInstanceCount = count;
        VisibleRiders = count;

        if (_followId != 0 && !_positions.ContainsKey(_followId))
            StopFollowing();
        var followed = _followId == 0 ? null : sim.State.Guests.FirstOrDefault(g => g.Id == _followId);
        FollowedRider = followed is null ? null
            : $"Rider #{followed.Id} · skill {followed.Skill / 10} · {followed.Style} · {followed.Activity}" +
              (followed.TrailId != 0 ? $" → {network.FindWay(followed.TrailId)?.Name}" : "") +
              $" · energy {followed.Energy / 10}% · mood {followed.Happiness / 10}%";
    }

    private static bool IsOnNetwork(Guest g) =>
        g.Activity is RiderActivity.Climbing or RiderActivity.Descending && g.Route.Count > 0;

    /// <summary>World position and travel direction at a progress along a route.</summary>
    private static bool TryLocate(WayNetwork network, List<RouteLeg> route, long progress, out Vector3 position, out Vector3 forward)
    {
        position = default;
        forward = Vector3.Forward;
        long remaining = progress;
        for (int i = 0; i < route.Count; i++)
        {
            var leg = route[i];
            if (remaining > leg.LengthCm && i < route.Count - 1)
            {
                remaining -= leg.LengthCm;
                continue;
            }
            if (!network.TryGetGeometry(leg.WayId, out var g)) return false;
            int dir = leg.ToCm >= leg.FromCm ? 1 : -1;
            long d = leg.FromCm + dir * Math.Min(remaining, leg.LengthCm);
            position = WayMeshes.ToWorld(g.PositionAt(d));
            var ahead = WayMeshes.ToWorld(g.PositionAt(d + dir * 150));
            var behind = WayMeshes.ToWorld(g.PositionAt(d - dir * 150));
            var f = ahead - behind;
            if (f.LengthSquared() > 0.0001f) forward = f.Normalized();
            return true;
        }
        return false;
    }

    private static Color SkillColor(int skill) =>
        WayMeshes.RatingColor(skill switch
        {
            < WayGeometry.GreenMaxDifficulty => TrailRating.Green,
            < WayGeometry.BlueMaxDifficulty => TrailRating.Blue,
            < WayGeometry.RedMaxDifficulty => TrailRating.Red,
            _ => TrailRating.Black,
        }).Lerp(Colors.White, 0.15f);

    /// <summary>Low-poly bike + rider, facing -Z, about 1.8 m long. Bike parts are dark, the jersey white (tinted per instance).</summary>
    private static ArrayMesh CreateRiderMesh()
    {
        var b = new FlatMeshBuilder();
        var frame = new Color(0.12f, 0.12f, 0.13f);
        var tyre = new Color(0.05f, 0.05f, 0.05f);
        var jersey = new Color(1f, 1f, 1f);
        var skin = new Color(0.85f, 0.68f, 0.55f);
        var helmet = new Color(0.25f, 0.25f, 0.28f);

        b.Wheel(new Vector3(0, 0.36f, -0.56f), 0.36f, 0.08f, 10, tyre);
        b.Wheel(new Vector3(0, 0.36f, 0.56f), 0.36f, 0.08f, 10, tyre);
        b.Box(new Vector3(-0.03f, 0.40f, -0.50f), new Vector3(0.03f, 0.62f, 0.50f), frame);   // frame
        b.Box(new Vector3(-0.32f, 0.98f, -0.50f), new Vector3(0.32f, 1.02f, -0.44f), frame);  // handlebar
        b.Box(new Vector3(-0.12f, 0.62f, -0.15f), new Vector3(0.12f, 0.95f, 0.20f), frame);   // legs (dark shorts)
        b.Box(new Vector3(-0.19f, 0.95f, -0.32f), new Vector3(0.19f, 1.40f, 0.12f), jersey);  // torso, leaning forward
        b.Box(new Vector3(-0.22f, 1.00f, -0.48f), new Vector3(-0.15f, 1.30f, -0.30f), jersey); // arms
        b.Box(new Vector3(0.15f, 1.00f, -0.48f), new Vector3(0.22f, 1.30f, -0.30f), jersey);
        b.Box(new Vector3(-0.11f, 1.40f, -0.36f), new Vector3(0.11f, 1.60f, -0.14f), skin);   // head
        b.Box(new Vector3(-0.13f, 1.55f, -0.40f), new Vector3(0.13f, 1.68f, -0.10f), helmet); // helmet
        return b.ToMesh();
    }
}
