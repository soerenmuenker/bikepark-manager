using Bikepark.Game.Ways;
using Bikepark.Sim;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Crew;

/// <summary>
/// Draws the crew as small workers in orange vests at their job: at the tree being felled, at the dug end of a way under
/// construction, or on the feature being built; idle workers wait at the base during work hours. Workers walk smoothly
/// to a new spot and swing their shovel while working. Pure view.
/// </summary>
public partial class CrewView : Node3D
{
    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";

    /// <summary>Same larger-than-life scale as the riders, so they read from an RTS camera.</summary>
    [Export] public float ModelScale { get; set; } = 3.5f;

    /// <summary>Walking speed between spots, meters per real second (fast: the camera is far away).</summary>
    [Export] public float MoveSpeed { get; set; } = 40f;

    private SimHost _host = null!;
    private MultiMeshInstance3D _instances = null!;
    private readonly Dictionary<int, Vector3> _positions = [];
    private float _time;

    public override void _Ready()
    {
        _host = GetNode<SimHost>(SimHostPath);
        _instances = new MultiMeshInstance3D
        {
            Name = "Crew",
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = CreateWorkerMesh() },
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.8f },
        };
        AddChild(_instances);
    }

    public override void _Process(double delta)
    {
        _time += (float)delta;
        var sim = _host.Sim;
        var state = sim.State;
        var mm = _instances.Multimesh;
        if (mm.InstanceCount < state.Crew.Count) mm.InstanceCount = state.Crew.Count + 4;

        int minute = Bikepark.Sim.Core.GameTime.MinuteOfDay(state.Tick);
        bool workHours = minute >= state.CrewRules.WorkStartMinute && minute < state.CrewRules.WorkEndMinute;
        var slots = new Dictionary<int, int>(); // job id → workers placed so far
        int count = 0;
        foreach (var member in state.Crew)
        {
            Vector3? target;
            bool working = member.JobId != 0 && Jobs.Find(state, member.JobId) is not null;
            if (working)
            {
                var job = Jobs.Find(state, member.JobId)!;
                int slot = slots.TryGetValue(job.Id, out int n) ? n : 0;
                slots[job.Id] = slot + 1;
                target = JobPosition(sim, job) is { } p ? p + Offset(slot) : null;
            }
            else
            {
                int slot = slots.TryGetValue(0, out int n) ? n : 0;
                slots[0] = slot + 1;
                target = workHours ? BasePosition(sim) is { } b ? b + Offset(slot) * 1.5f : null : null;
            }
            if (target is not { } t)
            {
                _positions.Remove(member.Id);
                continue;
            }

            var position = _positions.TryGetValue(member.Id, out var current) && current.DistanceTo(t) < 300f
                ? current.MoveToward(t, MoveSpeed * (float)delta)
                : t;
            _positions[member.Id] = position;
            var ground = new Vector3(position.X, sim.Terrain.HeightAt((long)(position.X * 100), (long)(position.Z * 100)) / 100f, position.Z);

            // Face the work spot; swing while working there.
            var facing = (t - position) with { Y = 0 };
            if (facing.LengthSquared() < 0.01f) facing = -Offset(member.Id % 3) with { Y = 0 };
            if (facing.LengthSquared() < 0.01f) facing = Vector3.Forward;
            var basis = Basis.LookingAt(facing.Normalized(), Vector3.Up);
            if (working && position.DistanceTo(t) < 0.5f)
                basis = basis.Rotated(basis.X, 0.25f * MathF.Sin(_time * 5f + member.Id));
            mm.SetInstanceTransform(count++, new Transform3D(basis.Scaled(Vector3.One * ModelScale), ground));
        }
        mm.VisibleInstanceCount = count;
    }

    /// <summary>Where the job's work happens now (meters), or null if it has nothing to show.</summary>
    private static Vector3? JobPosition(Simulation sim, Job job)
    {
        if (job.IsFelling)
        {
            var tree = job.Trees[job.TreesFelled];
            return new Vector3(tree.X / 100f, 0, tree.Z / 100f);
        }
        var network = sim.Network;
        if (!network.TryGetGeometry(job.WayId, out var g)) return null;
        long done = job.WorkMinutes <= 0 ? 0 : Math.Min(job.Progress, job.WorkMinutes * 1000) * 1000 / (job.WorkMinutes * 1000);
        long at = job.Kind switch
        {
            JobKind.BuildWay => g.LengthCm * done / 1000,
            JobKind.BuildFeature when network.FeaturesOn(job.WayId).FirstOrDefault(f => f.Feature.Id == job.FeatureId) is { Type: not null } f
                => f.StartCm + (f.EndCm - f.StartCm) * done / 1000,
            _ => -1,
        };
        return at < 0 ? null : WayMeshes.ToWorld(g.PositionAt(at));
    }

    private static Vector3? BasePosition(Simulation sim)
    {
        var network = sim.Network;
        if (network.BaseHub is { } hub) return new Vector3(hub.Pad.CenterX / 100f, 0, hub.Pad.CenterZ / 100f);
        return network.BaseWay is { } way ? WayMeshes.ToWorld(network.Geometry(way.Id).PositionAt(0)) : null;
    }

    /// <summary>Workers on one job stand around the spot.</summary>
    private static Vector3 Offset(int slot)
    {
        float angle = slot * 2.1f;
        float radius = 1.6f + slot / 3 * 1.2f;
        return new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
    }

    private static ArrayMesh CreateWorkerMesh()
    {
        var b = new FlatMeshBuilder();
        var trousers = new Color(0.2f, 0.24f, 0.32f);
        var vest = new Color(1f, 0.45f, 0.05f);
        var skin = new Color(0.85f, 0.68f, 0.55f);
        var hat = new Color(1f, 0.85f, 0.1f);
        var wood = new Color(0.55f, 0.38f, 0.2f);
        var steel = new Color(0.6f, 0.62f, 0.65f);

        b.Box(new Vector3(-0.16f, 0f, -0.08f), new Vector3(-0.03f, 0.75f, 0.08f), trousers); // legs
        b.Box(new Vector3(0.03f, 0f, -0.08f), new Vector3(0.16f, 0.75f, 0.08f), trousers);
        b.Box(new Vector3(-0.2f, 0.75f, -0.12f), new Vector3(0.2f, 1.3f, 0.12f), vest);      // torso
        b.Box(new Vector3(-0.27f, 0.85f, -0.35f), new Vector3(-0.2f, 1.25f, -0.05f), vest);  // arms reaching forward
        b.Box(new Vector3(0.2f, 0.85f, -0.35f), new Vector3(0.27f, 1.25f, -0.05f), vest);
        b.Box(new Vector3(-0.1f, 1.3f, -0.1f), new Vector3(0.1f, 1.52f, 0.1f), skin);        // head
        b.Box(new Vector3(-0.14f, 1.5f, -0.16f), new Vector3(0.14f, 1.6f, 0.14f), hat);      // hard hat
        b.Box(new Vector3(-0.025f, 0.2f, -0.55f), new Vector3(0.025f, 1.0f, -0.5f), wood);   // shovel handle
        b.Box(new Vector3(-0.12f, 0.02f, -0.6f), new Vector3(0.12f, 0.24f, -0.52f), steel);  // blade
        return b.ToMesh();
    }
}
