using Bikepark.Game.Camera;
using Bikepark.Game.Terrain;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Ways;

/// <summary>
/// Editing built trails (picked in the Build menu). Renaturalize: click where the section starts, move along the trail
/// (the section to remove shows in red), click where it ends. Split: point at the trail (yellow mark) and click. Both
/// are validated every frame with the Sim's <see cref="WayEditing"/> checks; Esc drops the first click, or leaves the tool.
/// </summary>
public partial class TrailEditTool : Node3D
{
    public enum ToolMode
    {
        None,
        Renaturalize,
        Split,
    }

    private const float PickRadiusMeters = 10f;
    private static readonly Color Remove = new(1f, 0.25f, 0.15f, 0.8f);
    private static readonly Color Cut = new(1f, 0.85f, 0.2f, 0.9f);
    private static readonly Color Blocked = new(0.6f, 0.6f, 0.6f, 0.7f);

    private readonly SimHost _host;
    private readonly TerrainView _terrain;
    private readonly RtsCamera _camera;
    private MeshInstance3D _preview = null!;
    private (int WayId, long Cm)? _anchor;

    public TrailEditTool() : this(null!, null!, null!) { }

    public TrailEditTool(SimHost host, TerrainView terrain, RtsCamera camera)
    {
        _host = host;
        _terrain = terrain;
        _camera = camera;
        Name = "TrailEditTool";
    }

    public ToolMode Mode { get; private set; }

    public bool Active => Mode != ToolMode.None;

    /// <summary>What the tool would do now (or why not), for the HUD's tool panel.</summary>
    public string Status { get; private set; } = "";

    /// <summary>Why the current preview can't be applied, or null.</summary>
    public string? Problem { get; private set; }

    public override void _Ready()
    {
        _preview = new MeshInstance3D
        {
            Name = "Preview",
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                NoDepthTest = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
        AddChild(_preview);
    }

    public void SetMode(ToolMode mode)
    {
        Mode = mode;
        _anchor = null;
        Problem = null;
        Status = mode switch
        {
            ToolMode.Renaturalize => "Renaturalize: click where the section starts",
            ToolMode.Split => "Split: click the point on a trail where it should be cut",
            _ => "",
        };
        if (mode == ToolMode.None && _preview is not null) _preview.Visible = false; // (the HUD may call this before _Ready)
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Active) return;
        if (@event is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Escape })
        {
            if (_anchor is not null) SetMode(Mode); else SetMode(ToolMode.None);
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            Click();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (!Active || _host is null) return;
        var sim = _host.Sim;
        var network = sim.Network;
        var hover = Hover();
        _preview.Visible = false;
        Problem = null;

        if (Mode == ToolMode.Split)
        {
            if (hover is not { } h) { Status = "Split: point at a trail"; return; }
            var way = network.FindWay(h.WayId);
            Problem = WayEditing.CannotSplit(sim.State, network, way, h.Cm);
            long length = network.Geometry(h.WayId).LengthCm;
            Status = $"Split {way?.Name} at {h.Cm / 100} m: {h.Cm / 100} m + {(length - h.Cm) / 100} m";
            Draw(h.WayId, Math.Max(0, h.Cm - 150), Math.Min(length, h.Cm + 150), Problem is null ? Cut : Blocked, 3.5f);
            return;
        }

        // Renaturalize.
        if (_anchor is not { } anchor)
        {
            if (hover is { } h)
            {
                var way = network.FindWay(h.WayId);
                Problem = WayEditing.CannotEdit(sim.State, way);
                Status = $"Renaturalize {way?.Name}: click where the section starts ({h.Cm / 100} m)";
                Draw(h.WayId, Math.Max(0, h.Cm - 100), h.Cm + 100, Problem is null ? Remove : Blocked, 3f);
            }
            else Status = "Renaturalize: point at a trail";
            return;
        }
        if (network.FindWay(anchor.WayId) is not { } trail)
        {
            SetMode(Mode);
            return;
        }
        long to = hover is { } over && over.WayId == anchor.WayId ? over.Cm : anchor.Cm;
        long from = Math.Min(anchor.Cm, to), until = Math.Max(anchor.Cm, to);
        long total = network.Geometry(trail.Id).LengthCm;
        Problem = WayEditing.CannotRenaturalize(sim.State, network, trail, from, until);
        string what = from <= 0 && until >= total ? "the whole trail" : $"{from / 100}–{until / 100} m ({(until - from) / 100} m)";
        Status = $"Renaturalize {trail.Name}: {what} · click to remove, Esc to start over";
        Draw(trail.Id, from, Math.Max(until, from + 100), Problem is null ? Remove : Blocked, 3f);
    }

    private void Click()
    {
        var sim = _host.Sim;
        var network = sim.Network;
        var hover = Hover();
        if (Mode == ToolMode.Split)
        {
            if (hover is not { } h) return;
            if (WayEditing.CannotSplit(sim.State, network, network.FindWay(h.WayId), h.Cm) is { } why) { Status = why; return; }
            _host.Enqueue(new SplitTrailCommand(h.WayId, h.Cm));
            return;
        }
        if (_anchor is null)
        {
            if (hover is not { } start) return;
            if (WayEditing.CannotEdit(sim.State, network.FindWay(start.WayId)) is { } why) { Status = why; return; }
            _anchor = start;
            return;
        }
        var anchor = _anchor.Value;
        long to = hover is { } over && over.WayId == anchor.WayId ? over.Cm : anchor.Cm;
        long from = Math.Min(anchor.Cm, to), until = Math.Max(anchor.Cm, to);
        if (WayEditing.CannotRenaturalize(sim.State, network, network.FindWay(anchor.WayId), from, until) is { } problem)
        {
            Status = problem;
            return;
        }
        _host.Enqueue(new RenaturalizeTrailCommand(anchor.WayId, from, until));
        _anchor = null;
    }

    /// <summary>The trail and the distance along it under the cursor, if a trail is close.</summary>
    private (int WayId, long Cm)? Hover()
    {
        var mouse = GetViewport().GetMousePosition();
        var camera = _camera.Camera;
        if (!_terrain.TryRaycast(camera.ProjectRayOrigin(mouse), camera.ProjectRayNormal(mouse), 4000f, out var hit)) return null;
        var near = _host.Sim.Network.NearestTrail((int)MathF.Round(hit.X * 100), (int)MathF.Round(hit.Z * 100), (int)(PickRadiusMeters * 100));
        // Only built trails can be edited; prefer the anchor's trail while marking a section.
        return near is { } n ? (n.WayId, n.DistanceCm) : null;
    }

    /// <summary>A band along the trail between two distances, drawn over everything.</summary>
    private void Draw(int wayId, long fromCm, long toCm, Color color, float width)
    {
        if (!_host.Sim.Network.TryGetGeometry(wayId, out var geometry)) return;
        toCm = Math.Min(toCm, geometry.LengthCm);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetColor(color);
        long step = 200;
        Vector3 At(long d) => WayMeshes.ToWorld(geometry.PositionAt(Math.Clamp(d, 0, geometry.LengthCm))) + Vector3.Up * 0.5f;
        for (long d = fromCm; d < toCm; d += step)
        {
            long e = Math.Min(toCm, d + step);
            var a = At(d);
            var b = At(e);
            var dir = b - a;
            if (dir.LengthSquared() < 0.0001f) continue;
            var side = new Vector3(-dir.Z, 0, dir.X).Normalized() * (width / 2);
            st.AddVertex(a - side); st.AddVertex(a + side); st.AddVertex(b + side);
            st.AddVertex(a - side); st.AddVertex(b + side); st.AddVertex(b - side);
        }
        _preview.Mesh = st.Commit();
        _preview.Visible = true;
    }
}
