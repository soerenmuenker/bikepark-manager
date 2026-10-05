using Bikepark.Game.Camera;
using Bikepark.Game.Terrain;
using Bikepark.Sim;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Reporting;
using Godot;

namespace Bikepark.Game;

/// <summary>
/// Placeholder debug HUD: shows KPIs, terrain info under the cursor, the overlay mode, a short event log, and
/// issues commands. Pure view; never writes to WorldState.
/// </summary>
public partial class Hud : CanvasLayer
{
    private const int FeeStepCents = 250;
    private const int MaxLogLines = 8;

    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";
    [Export] public NodePath TerrainPath { get; set; } = "../TerrainView";
    [Export] public NodePath CameraPath { get; set; } = "../RtsCamera";

    private readonly Queue<string> _log = new();
    private readonly List<IDisposable> _subscriptions = [];
    private SimHost _host = null!;
    private TerrainView _terrain = null!;
    private RtsCamera _camera = null!;
    private Label _stats = null!;
    private Label _terrainInfo = null!;
    private Label _logLabel = null!;

    public override void _Ready()
    {
        _host = GetNode<SimHost>(SimHostPath);
        _terrain = GetNode<TerrainView>(TerrainPath);
        _camera = GetNode<RtsCamera>(CameraPath);
        BuildUi();
        _host.SimulationReplaced += Subscribe;
        Subscribe(_host.Sim);
    }

    public override void _ExitTree()
    {
        _host.SimulationReplaced -= Subscribe;
        Unsubscribe();
    }

    public override void _Process(double delta)
    {
        var state = _host.Sim.State;
        var kpi = KpiReport.From(state, includeHash: false);
        _stats.Text =
            $"{kpi.ParkName}   {GameTime.Format(state.Tick)}   speed {_host.Speed}x\n" +
            $"Money {Money(kpi.MoneyCents)}   Entry fee {Money(kpi.EntryFeeCents)}\n" +
            $"Guests in park {kpi.GuestsInPark}   happiness {kpi.AverageHappiness / 10}%   " +
            $"(exit avg {kpi.AverageExitHappiness / 10}%)\n" +
            $"Visitors {kpi.TotalVisitors}   turned away {kpi.TotalTurnedAway}   left unhappy {kpi.TotalLeftUnhappy}";
        _terrainInfo.Text = TerrainInfo();
    }

    private string TerrainInfo()
    {
        string overlay = $"[F1] overlay: {_terrain.Overlay}";
        var grid = _terrain.Grid;
        var mouse = GetViewport().GetMousePosition();
        var camera = _camera.Camera;
        if (grid is null || !_terrain.TryRaycast(camera.ProjectRayOrigin(mouse), camera.ProjectRayNormal(mouse), 4000f, out var hit))
            return overlay;

        var sample = grid.Sample((long)MathF.Round(hit.X * 100), (long)MathF.Round(hit.Z * 100));
        float degrees = Mathf.RadToDeg(MathF.Atan(sample.SlopePermille / 1000f));
        return $"{overlay}\n" +
               $"Cursor {hit.X:F1}, {hit.Z:F1} m   elevation {sample.HeightCm / 100f:F1} m   " +
               $"slope {sample.SlopePermille}‰ ({degrees:F1}°)   {sample.Surface}\n" +
               $"trees {sample.TreeDensity}  rock {sample.Rock}  roots {sample.Roots}  water {sample.WaterDepthCm} cm";
    }

    private void Subscribe(Simulation sim)
    {
        Unsubscribe();
        _subscriptions.Add(sim.Events.Subscribe<DayEnded>(e =>
            Log($"Day {e.Report.Day + 1} closed: {e.Report.Visitors} visitors, net {Money(e.Report.RevenueCents - e.Report.ExpensesCents)}")));
        _subscriptions.Add(sim.Events.Subscribe<ParkOpened>(e => Log($"{GameTime.Format(e.Tick)} park opened")));
        _subscriptions.Add(sim.Events.Subscribe<ParkClosed>(e => Log($"{GameTime.Format(e.Tick)} park closed")));
        _subscriptions.Add(sim.Events.Subscribe<CommandRejected>(e => Log($"Rejected: {e.Reason}")));
    }

    private void Unsubscribe()
    {
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
    }

    private void Log(string line)
    {
        GD.Print(line);
        _log.Enqueue(line);
        while (_log.Count > MaxLogLines)
            _log.Dequeue();
        _logLabel.Text = string.Join('\n', _log);
    }

    private void BuildUi()
    {
        var panel = new PanelContainer { Position = new Vector2(12, 12) };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.55f),
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 8, ContentMarginBottom = 8,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        });
        AddChild(panel);
        var root = new VBoxContainer();
        panel.AddChild(root);

        _stats = new Label();
        root.AddChild(_stats);
        _terrainInfo = new Label();
        root.AddChild(_terrainInfo);

        var speedRow = new HBoxContainer();
        root.AddChild(speedRow);
        for (int i = 0; i < SimHost.SpeedMultipliers.Length; i++)
        {
            int index = i;
            int speed = SimHost.SpeedMultipliers[i];
            AddButton(speedRow, speed == 0 ? "Pause" : $"{speed}x", () => _host.SetSpeedIndex(index));
        }

        var actionRow = new HBoxContainer();
        root.AddChild(actionRow);
        AddButton(actionRow, "Fee -", () => ChangeFee(-FeeStepCents));
        AddButton(actionRow, "Fee +", () => ChangeFee(FeeStepCents));
        AddButton(actionRow, "Save", _host.Save);
        AddButton(actionRow, "Load", () => Log(_host.Load() ? "Loaded save." : "No save found."));

        _logLabel = new Label();
        root.AddChild(_logLabel);
    }

    private void ChangeFee(long deltaCents) =>
        _host.Enqueue(new SetEntryFeeCommand(_host.Sim.State.Park.EntryFeeCents + deltaCents));

    private static void AddButton(Container parent, string text, Action onPressed)
    {
        var button = new Button { Text = text };
        button.Pressed += onPressed;
        parent.AddChild(button);
    }

    private static string Money(long cents) => $"{cents / 100.0:N2} €";
}
