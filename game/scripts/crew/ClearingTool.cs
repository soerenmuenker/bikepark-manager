using Bikepark.Game.Camera;
using Bikepark.Game.Lifts;
using Bikepark.Game.Terrain;
using Bikepark.Game.Ways;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Crew;

/// <summary>
/// Marks a felling area (Build → Fell trees): click the centre, move the mouse to size the circle, click again to queue
/// a <see cref="FellTreesCommand"/>. The preview ring and the markers on the trees that would be cut come from the
/// Sim's <see cref="ClearingPlanner"/> (green = valid, red = see the tool panel). Esc drops the centre, then leaves the
/// tool; picking another build tool ends it.
/// </summary>
public partial class ClearingTool : Node3D
{
    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";
    [Export] public NodePath TerrainPath { get; set; } = "../TerrainView";
    [Export] public NodePath CameraPath { get; set; } = "../RtsCamera";
    [Export] public NodePath WayToolPath { get; set; } = "../WayTool";
    [Export] public NodePath StructureToolPath { get; set; } = "../StructureTool";
    [Export] public NodePath FeatureToolPath { get; set; } = "../FeatureTool";

    private static readonly Color Valid = new(0.25f, 0.95f, 0.35f, 0.9f);
    private static readonly Color Invalid = new(1f, 0.2f, 0.15f, 0.9f);
    private static readonly Color Marker = new(1f, 0.55f, 0.1f, 0.95f);

    private SimHost _host = null!;
    private TerrainView _terrain = null!;
    private RtsCamera _camera = null!;
    private WayTool _ways = null!;
    private StructureTool _structures = null!;
    private FeatureTool _features = null!;
    private MeshInstance3D _ring = null!;
    private MultiMeshInstance3D _markers = null!;
    private PointCm? _center;
    private (PointCm Center, int Radius, int Revision, int Jobs)? _planKey;

    public bool Active { get; private set; }

    /// <summary>The area under the cursor (null until a centre is placed).</summary>
    public ClearingPlan? Plan { get; private set; }

    public string Status { get; private set; } = "";

    public override void _Ready()
    {
        _host = GetNode<SimHost>(SimHostPath);
        _terrain = GetNode<TerrainView>(TerrainPath);
        _camera = GetNode<RtsCamera>(CameraPath);
        _ways = GetNode<WayTool>(WayToolPath);
        _structures = GetNode<StructureTool>(StructureToolPath);
        _features = GetNode<FeatureTool>(FeatureToolPath);
        var material = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            NoDepthTest = true,
        };
        _ring = new MeshInstance3D { Name = "Ring", MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_ring);
        _markers = new MultiMeshInstance3D
        {
            Name = "Trees",
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = new SphereMesh { Radius = 0.7f, Height = 1.4f, RadialSegments = 8, Rings = 4 },
            },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_markers);
    }

    public void SetActive(bool active)
    {
        Active = active;
        _center = null;
        Plan = null;
        _planKey = null;
        if (active)
        {
            _ways.SetMode(WayTool.ToolMode.None);
            _structures.SetMode(StructureTool.ToolMode.None);
            _features.SetType(null);
        }
        Status = active ? "Fell trees: click the centre of the area" : "";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Active) return;
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            switch (key.PhysicalKeycode)
            {
                case Key.P or Key.T or Key.L or Key.K:
                    SetActive(false);
                    return; // let the other tools take it
                case Key.Escape:
                    if (_center is not null) SetActive(true); else SetActive(false);
                    break;
                default:
                    return;
            }
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            if (_center is null)
            {
                if (CursorPoint() is { } c)
                {
                    _center = c;
                    Status = "Fell trees: move to size the area, click to mark it";
                }
            }
            else if (Plan is { IsValid: true } plan)
            {
                _host.Enqueue(new FellTreesCommand(plan.Center, plan.RadiusCm));
                Status = $"Marked {plan.Trees.Count} trees for felling (+{plan.Estimate.WoodGained} wood). Click the next centre or press Esc.";
                _center = null;
                Plan = null;
            }
            else
            {
                Status = Plan?.FirstError ?? "Not valid here.";
            }
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        // Another build tool took over.
        if (Active && (_ways.Mode != WayTool.ToolMode.None || _structures.Mode != StructureTool.ToolMode.None || _features.Active))
            SetActive(false);

        var grid = _terrain.Grid;
        if (!Active || grid is null || _center is not { } center || CursorPoint() is not { } cursor)
        {
            _ring.Visible = false;
            _markers.Multimesh.VisibleInstanceCount = 0;
            _planKey = null;
            if (_center is null) Plan = null;
            return;
        }

        var sim = _host.Sim;
        var rules = sim.State.CrewRules;
        long dx = cursor.X - center.X, dz = cursor.Z - center.Z;
        int radius = (int)Math.Clamp(Math.Sqrt(dx * dx + dz * dz) / 50 * 50, rules.MinClearingRadiusMeters * 100, rules.MaxClearingRadiusMeters * 100);
        var key = (center, radius, sim.State.WaysRevision, sim.State.Jobs.Count);
        if (_planKey != key)
        {
            _planKey = key;
            Plan = ClearingPlanner.Plan(grid, sim.Network, sim.State, center, radius);
            UpdateMarkers(grid, Plan);
        }
        _ring.Mesh = Ring(grid, center, radius, Plan!.IsValid ? Valid : Invalid);
        _ring.Visible = true;
    }

    private void UpdateMarkers(TerrainGrid grid, ClearingPlan plan)
    {
        var mm = _markers.Multimesh;
        if (mm.InstanceCount < plan.Trees.Count) mm.InstanceCount = plan.Trees.Count + 64;
        for (int i = 0; i < plan.Trees.Count; i++)
        {
            var t = plan.Trees[i];
            var position = new Vector3(t.X / 100f, grid.HeightAt(t.X, t.Z) / 100f + 9f, t.Z / 100f);
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, position));
            mm.SetInstanceColor(i, Marker);
        }
        mm.VisibleInstanceCount = plan.Trees.Count;
    }

    /// <summary>A flat ring on the ground around the area.</summary>
    private static ArrayMesh Ring(TerrainGrid grid, PointCm center, int radiusCm, Color color)
    {
        const int segments = 72;
        float r = radiusCm / 100f, cx = center.X / 100f, cz = center.Z / 100f;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetColor(color);
        Vector3 At(float angle, float radius)
        {
            float x = cx + MathF.Cos(angle) * radius, z = cz + MathF.Sin(angle) * radius;
            return new Vector3(x, grid.HeightAt((long)(x * 100), (long)(z * 100)) / 100f + 0.4f, z);
        }
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathF.Tau / segments, a1 = (i + 1) * MathF.Tau / segments;
            Vector3 i0 = At(a0, r - 0.4f), o0 = At(a0, r + 0.4f), i1 = At(a1, r - 0.4f), o1 = At(a1, r + 0.4f);
            st.AddVertex(i0); st.AddVertex(o0); st.AddVertex(o1);
            st.AddVertex(i0); st.AddVertex(o1); st.AddVertex(i1);
        }
        return st.Commit();
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
