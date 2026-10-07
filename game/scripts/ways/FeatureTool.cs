using Bikepark.Game.Camera;
using Bikepark.Game.Lifts;
using Bikepark.Game.Terrain;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Ways;

/// <summary>
/// Places trail features (picked in the Build menu). A ghost of the feature follows the nearest trail under the
/// cursor, centred on it and validated every frame with the Sim's <see cref="FeaturePlanner"/> (green = can go here,
/// red = see the tool panel). Left-click plans it for the crew (enqueues a <see cref="PlaceTrailFeatureCommand"/>), Delete removes
/// the feature under the cursor, Esc leaves the tool. Picking a path, trail or structure tool ends it.
/// </summary>
public partial class FeatureTool : Node3D
{
    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";
    [Export] public NodePath TerrainPath { get; set; } = "../TerrainView";
    [Export] public NodePath CameraPath { get; set; } = "../RtsCamera";
    [Export] public NodePath WayToolPath { get; set; } = "../WayTool";
    [Export] public NodePath StructureToolPath { get; set; } = "../StructureTool";

    /// <summary>How far from a trail the cursor still picks it.</summary>
    [Export] public float PickRadiusMeters { get; set; } = 10f;

    private static readonly Color Valid = new(0.25f, 0.95f, 0.35f, 0.75f);
    private static readonly Color Invalid = new(1f, 0.2f, 0.15f, 0.75f);
    private static readonly Color Highlight = new(1f, 0.85f, 0.2f, 0.8f);

    private SimHost _host = null!;
    private TerrainView _terrain = null!;
    private RtsCamera _camera = null!;
    private WayTool _ways = null!;
    private StructureTool _structures = null!;
    private MeshInstance3D _ghost = null!;
    private MeshInstance3D _highlight = null!;

    /// <summary>The feature type being placed (null = tool off).</summary>
    public string? TypeId { get; private set; }

    /// <summary>Plan for the ghost under the cursor (null when no trail is near).</summary>
    public FeaturePlan? Plan { get; private set; }

    /// <summary>The placed feature under the cursor (Delete removes it).</summary>
    public (int WayId, PlacedFeature Feature)? Hovered { get; private set; }

    /// <summary>Last action result, for the HUD.</summary>
    public string Status { get; private set; } = "";

    public bool Active => TypeId is not null;

    public override void _Ready()
    {
        _host = GetNode<SimHost>(SimHostPath);
        _terrain = GetNode<TerrainView>(TerrainPath);
        _camera = GetNode<RtsCamera>(CameraPath);
        _ways = GetNode<WayTool>(WayToolPath);
        _structures = GetNode<StructureTool>(StructureToolPath);
        var ghostMaterial = FeatureMeshes.CreateGhostMaterial();
        _ghost = new MeshInstance3D { Name = "Ghost", MaterialOverride = ghostMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _highlight = new MeshInstance3D { Name = "Highlight", MaterialOverride = ghostMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_ghost);
        AddChild(_highlight);
    }

    /// <summary>Selects a feature type to place, or null to leave the tool. Ends the other build tools.</summary>
    public void SetType(string? typeId)
    {
        TypeId = typeId;
        Plan = null;
        Hovered = null;
        if (typeId is not null)
        {
            _ways.SetMode(WayTool.ToolMode.None);
            _structures.SetMode(StructureTool.ToolMode.None);
        }
        var type = typeId is null ? null : TrailFeatures.FindType(_host.Sim.State.TrailFeatureTypes, typeId);
        Status = type is null ? "" : $"Placing: {type.Name}";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Active) return;
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            switch (key.PhysicalKeycode)
            {
                case Key.P or Key.T or Key.L or Key.K:
                    SetType(null);
                    return; // let the other tools take it
                case Key.Escape:
                    SetType(null);
                    break;
                case Key.Delete or Key.Backspace when Hovered is { } hovered:
                    _host.Enqueue(new RemoveTrailFeatureCommand(hovered.WayId, hovered.Feature.Feature.Id));
                    Status = $"Removed the {hovered.Feature.Type.Name.ToLowerInvariant()} at {hovered.Feature.StartCm / 100} m";
                    break;
                default:
                    return;
            }
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            Place();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        // Another build tool took over.
        if (Active && (_ways.Mode != WayTool.ToolMode.None || _structures.Mode != StructureTool.ToolMode.None))
            SetType(null);

        var grid = _terrain.Grid;
        var sim = _host.Sim;
        var type = TypeId is null ? null : TrailFeatures.FindType(sim.State.TrailFeatureTypes, TypeId);
        if (type is null || grid is null)
        {
            _ghost.Visible = _highlight.Visible = false;
            Plan = null;
            Hovered = null;
            return;
        }

        var network = sim.Network;
        var hit = CursorPoint() is { } c ? network.NearestTrail(c.X, c.Z, (int)(PickRadiusMeters * 100)) : null;
        if (hit is not { } h)
        {
            _ghost.Visible = _highlight.Visible = false;
            Plan = null;
            Hovered = null;
            return;
        }

        var geometry = network.Geometry(h.WayId);
        var features = network.FeaturesOn(h.WayId);
        Hovered = features.Where(f => f.Covers(h.DistanceCm)).Select(f => ((int, PlacedFeature)?)(h.WayId, f)).FirstOrDefault();

        // Centre the feature on the cursor, in half-meter steps.
        long start = Math.Max(0, (h.DistanceCm - type.LengthCm / 2) / 50 * 50);
        Plan = FeaturePlanner.Plan(network, sim.State.TrailRules, sim.State.TrailFeatureTypes, h.WayId, type.Id, start);
        _ghost.Mesh = FeatureMeshes.Build(grid, geometry, type, start, Plan.IsValid ? Valid : Invalid);
        _ghost.Visible = Hovered is null;

        if (Hovered is { } hovered)
        {
            _highlight.Mesh = FeatureMeshes.Build(grid, geometry, hovered.Feature.Type, hovered.Feature.StartCm, Highlight);
            _highlight.Visible = true;
        }
        else
        {
            _highlight.Visible = false;
        }
    }

    private void Place()
    {
        if (Hovered is not null)
        {
            Status = "There is already a feature here (Delete removes it).";
            return;
        }
        if (Plan is not { } plan)
        {
            Status = "Point at a trail.";
            return;
        }
        if (!plan.IsValid)
        {
            Status = plan.FirstError ?? "Not valid here.";
            return;
        }
        _host.Enqueue(new PlaceTrailFeatureCommand(plan.WayId, plan.Type!.Id, plan.StartCm, _host.InstantBuild));
        string trail = _host.Sim.Network.FindWay(plan.WayId)?.Name ?? "the trail";
        Status = $"{(_host.InstantBuild ? "Built" : "Planned")} a {plan.Type.Name.ToLowerInvariant()} on {trail} at {plan.StartCm / 100} m";
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
