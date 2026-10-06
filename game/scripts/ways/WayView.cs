using Bikepark.Game.Terrain;
using Bikepark.Sim;
using Bikepark.Sim.Events;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Ways;

/// <summary>
/// Draws the built way network: gravel paths, dirt trails with a rating stripe, name labels and the base marker.
/// Rebuilds when ways, structures or the terrain change and tells the terrain to clear trees and rocks from the corridors. Pure view.
/// </summary>
public partial class WayView : Node3D
{
    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";
    [Export] public NodePath TerrainPath { get; set; } = "../TerrainView";

    private SimHost _host = null!;
    private TerrainView _terrain = null!;
    private ShaderMaterial _material = null!;
    private Node3D? _content;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _dirty = true;

    public override void _Ready()
    {
        _host = GetNode<SimHost>(SimHostPath);
        _terrain = GetNode<TerrainView>(TerrainPath);
        _material = WayMeshes.CreateMaterial();
        _terrain.TerrainBuilt += _ => _dirty = true;
        _host.SimulationReplaced += OnSimulationReplaced;
        OnSimulationReplaced(_host.Sim);
    }

    public override void _ExitTree()
    {
        _host.SimulationReplaced -= OnSimulationReplaced;
        foreach (var s in _subscriptions) s.Dispose();
    }

    public override void _Process(double delta)
    {
        if (!_dirty) return;
        _dirty = false;
        Rebuild(_host.Sim);
    }

    private void OnSimulationReplaced(Simulation sim)
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        _subscriptions.Add(sim.Events.Subscribe<WayBuilt>(_ => _dirty = true));
        _subscriptions.Add(sim.Events.Subscribe<WayDeleted>(_ => _dirty = true));
        _subscriptions.Add(sim.Events.Subscribe<LiftBuilt>(_ => _dirty = true));
        _subscriptions.Add(sim.Events.Subscribe<LiftDeleted>(_ => _dirty = true));
        _subscriptions.Add(sim.Events.Subscribe<ParkingLotBuilt>(_ => _dirty = true));
        _subscriptions.Add(sim.Events.Subscribe<ParkingLotDeleted>(_ => _dirty = true));
        _dirty = true;
    }

    private void Rebuild(Simulation sim)
    {
        _content?.QueueFree();
        _content = new Node3D { Name = "Ways" };
        AddChild(_content);

        var network = sim.Network;
        _terrain.SetScatterFilter(network.IsEmpty ? null : network.IsInCorridor);
        if (network.IsEmpty) return;
        var grid = sim.Terrain;

        foreach (var way in network.Ways)
        {
            var g = network.Geometry(way.Id);
            if (way.Kind == WayKind.AccessPath)
            {
                AddRibbon(WayMeshes.Ribbon(grid, g, 3.0f, 0.10f, followGround: false, _ => WayMeshes.Gravel), way.Name);
            }
            else
            {
                AddRibbon(WayMeshes.Ribbon(grid, g, 1.4f, 0.06f, followGround: true, _ => WayMeshes.Dirt), way.Name);
                var stripe = WayMeshes.RatingColor(g.Rating);
                AddRibbon(WayMeshes.Ribbon(grid, g, 0.35f, 0.09f, followGround: true, _ => stripe), way.Name + " stripe");
                AddLabel($"{way.Name}\n{g.Rating} · {g.LengthCm / 100} m", WayMeshes.ToWorld(g.PositionAt(0)) + Vector3.Up * 4f, stripe);
            }
        }

        Vector3? basePosition = network.BaseHub is { } hub
            ? new Vector3(hub.Pad.CenterX / 100f, hub.Pad.TargetHeightCm / 100f, hub.Pad.CenterZ / 100f)
            : network.BaseWay is { } baseWay ? WayMeshes.ToWorld(network.Geometry(baseWay.Id).PositionAt(0)) : null;
        if (basePosition is { } b)
        {
            var pole = new MeshInstance3D
            {
                Name = "Base",
                Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.3f, Height = 8f },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.55f, 0.1f) },
                Position = b + Vector3.Up * 4f,
            };
            _content.AddChild(pole);
            AddLabel("Base", b + Vector3.Up * 9.5f, new Color(0.95f, 0.55f, 0.1f));
        }
    }

    private void AddRibbon(ArrayMesh? mesh, string name)
    {
        if (mesh is null) return;
        _content!.AddChild(new MeshInstance3D { Name = name, Mesh = mesh, MaterialOverride = _material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    private void AddLabel(string text, Vector3 position, Color color)
    {
        _content!.AddChild(new Label3D
        {
            Text = text,
            Position = position,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize = 48,
            PixelSize = 0.03f,
            Modulate = color.Lerp(Colors.White, 0.35f),
            OutlineSize = 12,
            NoDepthTest = true,
            VisibilityRangeEnd = 700f,
        });
    }
}
