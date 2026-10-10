using Bikepark.Game.Camera;
using Bikepark.Game.Terrain;
using Bikepark.Game.Ui;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Ways;

/// <summary>
/// Renaturalize (picked in the Build menu): point at a lift station, parking lot or gravel platform and click to tear it
/// down (lifts and parking lots cost a contractor's fee), or click where a section of a trail or gravel path starts, move
/// along it (the section shows in red) and click where it ends. Validated every frame with the Sim's own checks
/// (<see cref="WayEditing"/>, <see cref="StructureRemoval"/>); Esc drops the first click, or leaves the tool.
/// </summary>
public partial class TrailEditTool : Node3D
{
    public enum ToolMode
    {
        None,
        Renaturalize,
    }

    private const float PickRadiusMeters = 10f;
    private static readonly Color Remove = new(1f, 0.25f, 0.15f, 0.8f);
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
        Status = mode == ToolMode.Renaturalize ? "Renaturalize: point at a trail, gravel path, lift station, parking lot or platform" : "";
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
        _preview.Visible = false;
        Problem = null;

        if (_anchor is not { } anchor)
        {
            if (HoverStructure() is { } structure)
            {
                var (name, kind) = StructureRemoval.Describe(sim.State, structure)!.Value;
                long cost = StructureRemoval.CostCents(sim.State, structure);
                Problem = StructureRemoval.CannotRemove(sim.State, structure);
                Status = $"Renaturalize {name} ({kind}): {(cost > 0 ? $"a contractor tears it down for {UiTheme.Money(cost)}" : "free")} · " +
                         "paths and trails on it get a loose end";
                DrawPads(structure, Problem is null ? Remove : Blocked);
            }
            else if (Hover() is { } h)
            {
                var way = network.FindWay(h.WayId);
                Problem = WayEditing.CannotEdit(sim.State, way);
                Status = $"Renaturalize {way?.Label}: click where the section starts ({h.Cm / 100} m)";
                Draw(h.WayId, Math.Max(0, h.Cm - 100), h.Cm + 100, Problem is null ? Remove : Blocked, 3f);
            }
            else Status = "Renaturalize: point at a trail, gravel path, lift station, parking lot or platform";
            return;
        }
        if (network.FindWay(anchor.WayId) is not { } trail)
        {
            SetMode(Mode);
            return;
        }
        var hover = Hover(anchor.WayId);
        long to = hover is { } over && over.WayId == anchor.WayId ? over.Cm : anchor.Cm;
        long from = Math.Min(anchor.Cm, to), until = Math.Max(anchor.Cm, to);
        long total = network.Geometry(trail.Id).LengthCm;
        Problem = WayEditing.CannotRenaturalize(sim.State, network, trail, from, until);
        string what = from <= 0 && until >= total ? $"the whole {(trail.Kind == WayKind.Trail ? "trail" : "path")}"
            : $"{from / 100}–{until / 100} m ({(until - from) / 100} m)";
        Status = $"Renaturalize {trail.Label}: {what} · free · click to remove, Esc to start over";
        Draw(trail.Id, from, Math.Max(until, from + 100), Problem is null ? Remove : Blocked, 3f);
    }

    private void Click()
    {
        var sim = _host.Sim;
        var network = sim.Network;
        if (_anchor is null)
        {
            if (HoverStructure() is { } structure)
            {
                if (StructureRemoval.CannotRemove(sim.State, structure) is { } no) { Status = no; return; }
                _host.Enqueue(new RenaturalizeStructureCommand(structure));
                return;
            }
            if (Hover() is not { } start) return;
            if (WayEditing.CannotEdit(sim.State, network.FindWay(start.WayId)) is { } why) { Status = why; return; }
            _anchor = start;
            return;
        }
        var anchor = _anchor.Value;
        long to = Hover(anchor.WayId) is { } over && over.WayId == anchor.WayId ? over.Cm : anchor.Cm;
        long from = Math.Min(anchor.Cm, to), until = Math.Max(anchor.Cm, to);
        if (WayEditing.CannotRenaturalize(sim.State, network, network.FindWay(anchor.WayId), from, until) is { } problem)
        {
            Status = problem;
            return;
        }
        _host.Enqueue(new RenaturalizeTrailCommand(anchor.WayId, from, until));
        _anchor = null;
    }

    /// <summary>The terrain point under the cursor, in cm.</summary>
    private PointCm? Cursor()
    {
        var mouse = GetViewport().GetMousePosition();
        var camera = _camera.Camera;
        if (!_terrain.TryRaycast(camera.ProjectRayOrigin(mouse), camera.ProjectRayNormal(mouse), 4000f, out var hit)) return null;
        return new PointCm((int)MathF.Round(hit.X * 100), (int)MathF.Round(hit.Z * 100));
    }

    /// <summary>The way and the distance along it under the cursor, if one is close (while marking: that way only).</summary>
    private (int WayId, long Cm)? Hover(int? only = null)
    {
        if (Cursor() is not { } c) return null;
        var network = _host.Sim.Network;
        int radius = (int)(PickRadiusMeters * 100);
        if (only is { } id && network.TryGetGeometry(id, out var g))
        {
            // Closest sample of that way (so the section follows it even where other ways run close by).
            long best = long.MaxValue, at = 0;
            for (int i = 0; i < g.SampleCount; i++)
            {
                long dx = g.Xs[i] - c.X, dz = g.Zs[i] - c.Z, d = dx * dx + dz * dz;
                if (d < best) { best = d; at = g.Distances[i]; }
            }
            return best <= (long)radius * radius * 16 ? (id, at) : null;
        }
        return network.Nearest(c.X, c.Z, radius) is { } n ? (n.WayId, n.DistanceCm) : null;
    }

    /// <summary>The lift, parking lot or platform whose pad is under the cursor (0 m around it), or null.</summary>
    private int? HoverStructure()
    {
        if (Cursor() is not { } c || _host.Sim.Network.HubAt(c.X, c.Z, 0) is not { } hub) return null;
        int owner = StructureRemoval.OwnerOfHub(_host.Sim.State, hub.Id);
        return owner == 0 ? null : owner;
    }

    /// <summary>The structure's pads, drawn over everything.</summary>
    private void DrawPads(int structureId, Color color)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetColor(color);
        foreach (var edit in _host.Sim.State.TerrainEdits.Where(e => e.OwnerId == structureId))
        {
            float y = edit.Pad.TargetHeightCm / 100f + 0.5f;
            var c = edit.Pad.Corners().Select(p => new Vector3(p.X / 100f, y, p.Z / 100f)).ToArray();
            st.AddVertex(c[0]); st.AddVertex(c[1]); st.AddVertex(c[2]);
            st.AddVertex(c[0]); st.AddVertex(c[2]); st.AddVertex(c[3]);
        }
        _preview.Mesh = st.Commit();
        _preview.Visible = true;
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
