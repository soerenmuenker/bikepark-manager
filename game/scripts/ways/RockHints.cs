using Bikepark.Game.Camera;
using Bikepark.Game.Terrain;
using Bikepark.Sim.Terrain;
using Godot;

namespace Bikepark.Game.Ways;

/// <summary>
/// While a trail, path or road is being drawn, the rocks around the cursor show what they mean for it: boulders amber (a
/// trail takes a flat drop off them, a path goes around), rock faces red (every way goes around), the stones of a stone field
/// yellow (they stay on a trail and make it black). Looks only: the rules read the same rocks (<see cref="RockClasses"/>).
/// </summary>
public partial class RockHints : Node3D
{
    [Export] public NodePath TerrainPath { get; set; } = "../TerrainView";
    [Export] public NodePath CameraPath { get; set; } = "../RtsCamera";
    [Export] public NodePath WayToolPath { get; set; } = "../WayTool";

    public static readonly Color BoulderColor = new(1f, 0.62f, 0.1f, 0.55f);
    public static readonly Color FaceColor = new(1f, 0.12f, 0.08f, 0.5f);
    public static readonly Color FieldColor = new(1f, 0.92f, 0.2f, 0.6f);

    private const int ReachMeters = 40, StepMeters = 8;

    private TerrainView _terrain = null!;
    private RtsCamera _camera = null!;
    private WayTool _wayTool = null!;
    private Node3D? _shown;
    private (int X, int Z)? _shownAt;

    public override void _Ready()
    {
        _terrain = GetNode<TerrainView>(TerrainPath);
        _camera = GetNode<RtsCamera>(CameraPath);
        _wayTool = GetNode<WayTool>(WayToolPath);
    }

    public override void _Process(double delta)
    {
        var grid = _terrain.Grid;
        if (_wayTool.Mode == WayTool.ToolMode.None || grid is null || !grid.HasRockFormations || Cursor() is not { } at)
        {
            Clear();
            return;
        }
        var cell = ((int)(at.X / StepMeters), (int)(at.Y / StepMeters));
        if (_shownAt == cell) return;
        Clear();
        _shownAt = cell;

        int cx = cell.Item1 * StepMeters + StepMeters / 2, cz = cell.Item2 * StepMeters + StepMeters / 2;
        var rocks = new List<ScatterInstance>();
        TerrainScatter.Collect(grid, Math.Max(0, cx - ReachMeters), Math.Max(0, cz - ReachMeters), cx + ReachMeters, cz + ReachMeters, rocks, rocksOnly: true);
        rocks.RemoveAll(r => r.Kind != ScatterKind.Rock);
        _shown = new Node3D { Name = "RockHints" };
        Add(rocks.Where(r => RockClasses.Of(r) == RockClass.Boulder), BoulderColor);
        Add(rocks.Where(r => RockClasses.Of(r) == RockClass.Face), FaceColor);
        Add(rocks.Where(r => RockClasses.Of(r) == RockClass.Stone && grid.RockFormationAt(r.XCm, r.ZCm) == RockFormation.DenseStones), FieldColor);
        AddChild(_shown);
    }

    private void Add(IEnumerable<ScatterInstance> rocks, Color color)
    {
        var list = rocks.ToList();
        if (list.Count == 0) return;
        var material = new StandardMaterial3D
        {
            AlbedoColor = color,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Back,
        };
        _shown!.AddChild(_terrain.BuildRockHighlight(list, material));
    }

    private void Clear()
    {
        _shown?.QueueFree();
        _shown = null;
        _shownAt = null;
    }

    private Vector2? Cursor()
    {
        var mouse = GetViewport().GetMousePosition();
        var camera = _camera.Camera;
        return _terrain.TryRaycast(camera.ProjectRayOrigin(mouse), camera.ProjectRayNormal(mouse), 4000f, out var hit) ? new Vector2(hit.X, hit.Z) : null;
    }
}
