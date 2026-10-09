using Bikepark.Game.Camera;
using Bikepark.Game.Terrain;
using Bikepark.Game.Ways;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Lifts;

/// <summary>
/// Debug placement tool for lifts (L: click the valley station, then the top; the plateau follows the line) and parking
/// lots (K: click the centre, then a point its length should point to). The preview is validated every frame with the
/// Sim's <see cref="StructurePlanner"/> (green = valid, red = not). Enter builds (enqueues a command), Backspace undoes
/// the last click, Esc cancels. Park-owned lifts built here carry bikes in every cabin.
/// </summary>
public partial class StructureTool : Node3D
{
    public enum ToolMode
    {
        None,
        Lift,
        Parking,

        /// <summary>A small square gravel platform to connect paths and trails (click the centre, then a direction).</summary>
        Platform,
    }

    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";
    [Export] public NodePath TerrainPath { get; set; } = "../TerrainView";
    [Export] public NodePath CameraPath { get; set; } = "../RtsCamera";
    [Export] public NodePath WayToolPath { get; set; } = "../WayTool";
    /// <summary>The lift type the lift mode places (picked in the Build menu).</summary>
    [Export] public string LiftTypeId { get; set; } = "tbar";
    [Export] public int ParkingSpaces { get; set; } = 60;

    private SimHost _host = null!;
    private TerrainView _terrain = null!;
    private RtsCamera _camera = null!;
    private WayTool _wayTool = null!;
    private MeshInstance3D _preview = null!;
    private ImmediateMesh _mesh = null!;
    private readonly List<PointCm> _points = [];

    public ToolMode Mode { get; private set; }

    /// <summary>Plan for the placed point(s) plus the cursor.</summary>
    public LiftPlan? LiftPlan { get; private set; }
    public ParkingPlan? ParkingPlan { get; private set; }

    public string Status { get; private set; } = "";

    public override void _Ready()
    {
        _host = GetNode<SimHost>(SimHostPath);
        _terrain = GetNode<TerrainView>(TerrainPath);
        _camera = GetNode<RtsCamera>(CameraPath);
        _wayTool = GetNode<WayTool>(WayToolPath);
        _mesh = new ImmediateMesh();
        _preview = new MeshInstance3D
        {
            Name = "Preview",
            Mesh = _mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true },
        };
        AddChild(_preview);
    }

    public void SetMode(ToolMode mode)
    {
        Mode = mode;
        _points.Clear();
        LiftPlan = null;
        ParkingPlan = null;
        if (mode != ToolMode.None) _wayTool.SetMode(WayTool.ToolMode.None);
        Status = mode switch
        {
            ToolMode.Lift => "Lift: click the valley station, then the top station",
            ToolMode.Parking => $"Parking lot ({ParkingSpaces} spaces): click the centre, then where it should point",
            ToolMode.Platform => "Gravel platform: click the centre, then where one side should point",
            _ => "",
        };
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            switch (key.PhysicalKeycode)
            {
                case Key.L:
                    SetMode(Mode == ToolMode.Lift ? ToolMode.None : ToolMode.Lift);
                    break;
                case Key.K:
                    SetMode(Mode == ToolMode.Parking ? ToolMode.None : ToolMode.Parking);
                    break;
                case Key.P or Key.T when Mode != ToolMode.None:
                    SetMode(ToolMode.None);
                    return; // let the way tool take it
                case Key.Escape when Mode != ToolMode.None:
                    if (_points.Count > 0) _points.Clear(); else SetMode(ToolMode.None);
                    break;
                case Key.Backspace when Mode != ToolMode.None && _points.Count > 0:
                    _points.RemoveAt(_points.Count - 1);
                    break;
                case Key.Enter or Key.KpEnter when Mode != ToolMode.None:
                    Build();
                    break;
                default:
                    return;
            }
            GetViewport().SetInputAsHandled();
            return;
        }

        if (Mode == ToolMode.None) return;
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            if (CursorPoint() is { } p)
            {
                if (_points.Count == 2) _points.RemoveAt(1);
                _points.Add(p);
            }
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        _mesh.ClearSurfaces();
        var grid = _terrain.Grid;
        if (Mode == ToolMode.None || grid is null)
            return;

        var candidate = _points.ToList();
        if (candidate.Count == 1 && CursorPoint() is { } cursor) candidate.Add(cursor);
        var sim = _host.Sim;
        LiftPlan = null;
        ParkingPlan = null;
        if (candidate.Count < 2) return;

        if (Mode == ToolMode.Lift)
        {
            LiftPlan = StructurePlanner.PlanLift(grid, sim.Network, sim.State, LiftTypeId, candidate[0], candidate[1]);
            var color = LiftPlan.IsValid && Locked() is null ? new Color(0.2f, 1f, 0.4f) : new Color(1f, 0.25f, 0.2f);
            if (LiftPlan.ValleyPad is { } v && LiftPlan.MountainPad is { } m)
            {
                _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
                Outline(v, color);
                Outline(m, color);
                Line(LiftShapes.Center(v, LiftShapes.CableHeight), LiftShapes.Center(m, LiftShapes.CableHeight), color);
                _mesh.SurfaceEnd();
            }
        }
        else
        {
            ParkingPlan = Mode == ToolMode.Platform
                ? StructurePlanner.PlanPlatform(grid, sim.Network, sim.State, candidate[0], candidate[1])
                : StructurePlanner.PlanParking(grid, sim.Network, sim.State, candidate[0], candidate[1], ParkingSpaces);
            var color = ParkingPlan.IsValid ? new Color(0.2f, 1f, 0.4f) : new Color(1f, 0.25f, 0.2f);
            if (ParkingPlan.Pad is { } pad)
            {
                _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
                Outline(pad, color);
                _mesh.SurfaceEnd();
            }
        }
    }

    private void Build()
    {
        if (_points.Count < 2)
        {
            Status = "Place both points first.";
            return;
        }
        var sim = _host.Sim;
        var grid = sim.Terrain;
        if (Mode == ToolMode.Lift)
        {
            var plan = StructurePlanner.PlanLift(grid, sim.Network, sim.State, LiftTypeId, _points[0], _points[1]);
            if (Locked() is { } locked) { Status = locked; return; }
            if (!plan.IsValid) { Status = plan.FirstError ?? "Not valid."; return; }
            _host.Enqueue(new BuildLiftCommand(LiftTypeId, "", _points[0], _points[1], Instant: _host.InstantBuild));
            Status = _host.InstantBuild
                ? $"Built a {plan.Type?.Name} ({plan.LengthCm / 100} m, {plan.RideSeconds / 60} min ride). Connect trails to its plateau."
                : $"Ordered a {plan.Type?.Name} ({plan.LengthCm / 100} m): the contractor needs {plan.Type?.BuildDays} days. Connect trails to its plateau.";
        }
        else if (Mode == ToolMode.Platform)
        {
            var plan = StructurePlanner.PlanPlatform(grid, sim.Network, sim.State, _points[0], _points[1]);
            if (!plan.IsValid) { Status = plan.FirstError ?? "Not valid."; return; }
            _host.Enqueue(new BuildPlatformCommand("", _points[0], _points[1]));
            Status = "Built a gravel platform: start or end paths and trails on it.";
        }
        else
        {
            var plan = StructurePlanner.PlanParking(grid, sim.Network, sim.State, _points[0], _points[1], ParkingSpaces);
            if (!plan.IsValid) { Status = plan.FirstError ?? "Not valid."; return; }
            _host.Enqueue(new BuildParkingLotCommand("", _points[0], _points[1], ParkingSpaces));
            Status = "Built a parking lot.";
        }
        _points.Clear();
    }

    /// <summary>Why the chosen lift type can't be built now (level, money), or null; debug instant builds are never locked.</summary>
    public string? Locked()
    {
        var sim = _host.Sim;
        if (_host.InstantBuild || LiftNetwork.FindType(sim.State, LiftTypeId) is not { } type) return null;
        return LiftWorks.CannotBuild(sim.State, Bikepark.Sim.Reputation.ParkProgress.CurrentLevel(sim.State, sim.Network), type);
    }

    private void Outline(TerrainPad pad, Color color)
    {
        var corners = pad.Corners().Select(c => new Vector3(c.X / 100f, pad.TargetHeightCm / 100f + 0.5f, c.Z / 100f)).ToArray();
        for (int i = 0; i < 4; i++)
            Line(corners[i], corners[(i + 1) % 4], color);
    }

    private void Line(Vector3 a, Vector3 b, Color color)
    {
        _mesh.SurfaceSetColor(color);
        _mesh.SurfaceAddVertex(a);
        _mesh.SurfaceSetColor(color);
        _mesh.SurfaceAddVertex(b);
    }

    private PointCm? CursorPoint()
    {
        var mouse = GetViewport().GetMousePosition();
        var camera = _camera.Camera;
        if (!_terrain.TryRaycast(camera.ProjectRayOrigin(mouse), camera.ProjectRayNormal(mouse), 4000f, out var hit))
            return null;
        return new PointCm((int)MathF.Round(hit.X * 100), (int)MathF.Round(hit.Z * 100));
    }
}
