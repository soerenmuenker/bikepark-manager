using Bikepark.Sim.Weather;
using Godot;

namespace Bikepark.Game.World;

/// <summary>
/// Rain streaks while it rains in the sim: a particle box that follows the camera's focus, so rain is visible at any zoom.
/// Pure view.
/// </summary>
public partial class RainView : Node3D
{
    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";

    /// <summary>Half the size of the rain box around the point the camera looks at, meters.</summary>
    [Export] public float Extent { get; set; } = 90f;

    private SimHost _host = null!;
    private GpuParticles3D _particles = null!;

    public override void _Ready()
    {
        _host = GetNode<SimHost>(SimHostPath);
        var process = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(Extent, 2f, Extent),
            Direction = new Vector3(0.15f, -1f, 0.05f),
            Spread = 3f,
            InitialVelocityMin = 28f,
            InitialVelocityMax = 34f,
            Gravity = Vector3.Zero,
        };
        var drop = new BoxMesh { Size = new Vector3(0.05f, 1.6f, 0.05f) };
        drop.Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(0.78f, 0.84f, 0.95f, 0.45f),
        };
        _particles = new GpuParticles3D
        {
            Name = "Rain",
            Amount = 6000,
            Lifetime = 2.2,
            ProcessMaterial = process,
            DrawPass1 = drop,
            Emitting = false,
            VisibilityAabb = new Aabb(new Vector3(-Extent, -80f, -Extent), new Vector3(2 * Extent, 90f, 2 * Extent)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_particles);
    }

    public override void _Process(double delta)
    {
        var state = _host.Sim.State;
        bool raining = WeatherMath.IsRaining(state, state.Tick);
        if (_particles.Emitting != raining) _particles.Emitting = raining;
        if (!raining) return;

        // Hang the box ~60 m above the ground under the camera's line of sight.
        var camera = GetViewport().GetCamera3D();
        if (camera is null) return;
        var forward = -camera.GlobalBasis.Z;
        var focus = camera.GlobalPosition + forward * Math.Min(camera.GlobalPosition.Y, 250f);
        _particles.GlobalPosition = new Vector3(focus.X, focus.Y + 60f, focus.Z);
    }
}
