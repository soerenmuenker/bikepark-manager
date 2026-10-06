using Bikepark.Game.Camera;
using Bikepark.Game.Lifts;
using Bikepark.Game.Riders;
using Bikepark.Game.Terrain;
using Bikepark.Game.Ways;
using Bikepark.Sim;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Reporting;
using Bikepark.Sim.Trails;
using Godot;
using Gradient = Bikepark.Sim.Trails.Gradient;

namespace Bikepark.Game;

/// <summary>
/// Placeholder debug HUD: KPIs, lifts (queue, wait, bike access tier; [ and ] book a tier), terrain info under the
/// cursor, build tool panels, followed rider, a list of paths and trails with their stats, and a short event log.
/// Issues commands; pure view, never writes to WorldState.
/// </summary>
public partial class Hud : CanvasLayer
{
    private const int FeeStepCents = 250;
    private const int MaxLogLines = 6;

    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";
    [Export] public NodePath TerrainPath { get; set; } = "../TerrainView";
    [Export] public NodePath CameraPath { get; set; } = "../RtsCamera";
    [Export] public NodePath WayToolPath { get; set; } = "../WayTool";
    [Export] public NodePath RiderViewPath { get; set; } = "../RiderView";
    [Export] public NodePath StructureToolPath { get; set; } = "../StructureTool";

    private readonly Queue<string> _log = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<(int WayId, Label Label)> _wayRows = [];
    private SimHost _host = null!;
    private TerrainView _terrain = null!;
    private RtsCamera _camera = null!;
    private WayTool _tool = null!;
    private RiderView _riders = null!;
    private StructureTool _structures = null!;
    private Label _stats = null!;
    private Label _liftInfo = null!;
    private Label _terrainInfo = null!;
    private Label _toolInfo = null!;
    private Label _logLabel = null!;
    private VBoxContainer _wayList = null!;
    private bool _waysDirty = true;

    public override void _Ready()
    {
        _host = GetNode<SimHost>(SimHostPath);
        _terrain = GetNode<TerrainView>(TerrainPath);
        _camera = GetNode<RtsCamera>(CameraPath);
        _tool = GetNode<WayTool>(WayToolPath);
        _riders = GetNode<RiderView>(RiderViewPath);
        _structures = GetNode<StructureTool>(StructureToolPath);
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
            $"Guests {kpi.GuestsInPark} ({_riders.VisibleRiders} riding)   mood {kpi.AverageHappiness / 10}%   " +
            $"(exit avg {kpi.AverageExitHappiness / 10}%)\n" +
            $"Visitors {kpi.TotalVisitors}   runs {kpi.RunsCompleted}   avg run fun {kpi.AverageRunFun / 10}%" +
            (state.ParkingLots.Count > 0 ? $"\nParking {kpi.ParkingOccupancyPermille / 10}% full   turned away (full) {kpi.TotalTurnedAwayParkingFull}" : "");
        _liftInfo.Text = LiftInfo();
        _liftInfo.Visible = _liftInfo.Text.Length > 0;
        _terrainInfo.Text = TerrainInfo();
        _toolInfo.Text = ToolInfo();
        _toolInfo.Visible = _toolInfo.Text.Length > 0;

        if (_waysDirty) RebuildWayList();
        UpdateWayRows();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        int step = key.PhysicalKeycode switch { Key.Bracketleft => -1, Key.Bracketright => 1, _ => 0 };
        if (step == 0) return;
        ChangeBikeTier(step);
        GetViewport().SetInputAsHandled();
    }

    /// <summary>Books the next lower/higher bike access tier on the first lift run by a lift company (from the next opening).</summary>
    private void ChangeBikeTier(int step)
    {
        var state = _host.Sim.State;
        var lift = state.Lifts.FirstOrDefault(l => l.BikeAccess is not null);
        if (lift is null || LiftNetwork.FindOperator(state, lift.OperatorId) is not { } op) return;
        int current = lift.BikeAccess!.PendingTierIndex ?? lift.BikeAccess.TierIndex;
        int next = Math.Clamp(current + step, 0, op.BikeAccessTiers.Count - 1);
        if (next != current) _host.Enqueue(new SetLiftBikeAccessCommand(lift.Id, next));
    }

    private string LiftInfo()
    {
        var state = _host.Sim.State;
        var lines = new List<string>();
        foreach (var lift in state.Lifts)
        {
            var type = LiftNetwork.FindType(state, lift.TypeId);
            if (type is null) continue;
            int wait = LiftMath.ExpectedWaitMinutes(type, lift.BikeCarrierPermille, lift.Queue.Count);
            double avg = lift.Stats.Riders == 0 ? 0 : (double)lift.Stats.SumWaitMinutes / lift.Stats.Riders;
            lines.Add($"{lift.Name}: queue {lift.Queue.Count} ({(wait < 0 ? "no bikes" : $"~{wait} min")})   " +
                      $"{LiftMath.BikeRidersPerHour(type, lift.BikeCarrierPermille)} riders/h   today {lift.Stats.RidersToday} rides, " +
                      $"max queue {lift.Stats.MaxQueueToday}, avg wait {avg:F1} min");
            if (lift.BikeAccess is { } access && LiftNetwork.FindOperator(state, lift.OperatorId) is { } op)
            {
                var tier = op.BikeAccessTiers[access.TierIndex];
                string pending = access.PendingTierIndex is { } p
                    ? $"   → {op.BikeAccessTiers[p].Name} ({Money(op.BikeAccessTiers[p].DailyFeeCents)}/day) from next opening"
                    : "";
                lines.Add($"   {op.Name}: {tier.Name} ({tier.BikeCarrierPermille / 10}% of cabins), {Money(tier.DailyFeeCents)}/day{pending}   [ / ] change");
            }
        }
        if (state.Lifts.Count > 0)
            lines.Add($"Lift fees paid {Money(state.Finance.TotalLiftFeesCents)}");
        return string.Join('\n', lines);
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
               $"slope {Gradient.Format(Gradient.FromPermille(sample.SlopePermille)).TrimStart('+')} ({degrees:F0}°)   {sample.Surface}\n" +
               $"trees {sample.TreeDensity}  rock {sample.Rock}  roots {sample.Roots}  water {sample.WaterDepthCm} cm";
    }

    private string ToolInfo()
    {
        var lines = new List<string>();
        if (_riders.FollowedRider is { } rider)
            lines.Add($"Following: {rider}   [F] next, pan to stop");

        if (_structures.Mode != StructureTool.ToolMode.None)
        {
            lines.Add(_structures.Status);
            lines.Add("Click to place · Backspace undo · Enter build · Esc cancel");
            if (_structures.LiftPlan is { } lp)
            {
                if (lp.LengthCm > 0)
                    lines.Add($"{lp.HorizontalCm / 100} m long, +{lp.RiseCm / 100} m, ride {lp.RideSeconds / 60.0:F1} min");
                foreach (var issue in lp.Issues.Take(3)) lines.Add($"✗ {issue.Message}");
                if (lp.IsValid) lines.Add("✓ Valid — press Enter to build");
            }
            if (_structures.ParkingPlan is { } pp)
            {
                foreach (var issue in pp.Issues.Take(3)) lines.Add($"✗ {issue.Message}");
                if (pp.IsValid) lines.Add("✓ Valid — press Enter to build");
            }
        }

        if (_tool.Mode != WayTool.ToolMode.None)
        {
            lines.Add($"{_tool.Status}   points {_tool.PointCount}");
            lines.Add("Click or drag to place points · Backspace undo · Enter build · Esc cancel");
            if (_tool.Plan is { Geometry: { } g } plan)
            {
                string kind = plan.Kind == WayKind.Trail ? $"   {g.Rating} (difficulty {g.DifficultyScore})" : "";
                lines.Add($"{plan.LengthCm / 100} m   {(plan.DropCm >= 0 ? "drop" : "climb")} {Math.Abs(plan.DropCm) / 100} m   " +
                          $"steepest {Gradient.Format(-g.MaxDropGradient)} / {Gradient.Format(g.MaxClimbGradient)}{kind}");
                lines.Add("Gradient: -10 = vertical drop · 0 = flat · +10 = vertical wall (green easy → red too steep)");
                foreach (var issue in plan.Issues.Take(3))
                    lines.Add($"{(issue.Severity == IssueSeverity.Error ? "✗" : "!")} {issue.Message}");
                if (plan.IsValid) lines.Add("✓ Valid — press Enter to build");
            }
        }
        return string.Join('\n', lines);
    }

    private void Subscribe(Simulation sim)
    {
        Unsubscribe();
        _subscriptions.Add(sim.Events.Subscribe<DayEnded>(e =>
            Log($"Day {e.Report.Day + 1} closed: {e.Report.Visitors} visitors, {e.Report.LiftRides} lift rides, " +
                $"lift fees {Money(e.Report.LiftFeesCents)}, net {Money(e.Report.RevenueCents - e.Report.ExpensesCents)}")));
        _subscriptions.Add(sim.Events.Subscribe<ParkOpened>(e => Log($"{GameTime.Format(e.Tick)} park opened")));
        _subscriptions.Add(sim.Events.Subscribe<ParkClosed>(e => Log($"{GameTime.Format(e.Tick)} park closed")));
        _subscriptions.Add(sim.Events.Subscribe<CommandRejected>(e => Log($"Rejected: {e.Reason}")));
        _subscriptions.Add(sim.Events.Subscribe<WayBuilt>(e =>
        {
            _waysDirty = true;
            Log($"Built {_host.Sim.Network.FindWay(e.WayId)?.Name}");
        }));
        _subscriptions.Add(sim.Events.Subscribe<WayDeleted>(_ => _waysDirty = true));
        _subscriptions.Add(sim.Events.Subscribe<LiftBuilt>(e => Log($"Built {_host.Sim.State.Lifts.FirstOrDefault(l => l.Id == e.LiftId)?.Name}")));
        _subscriptions.Add(sim.Events.Subscribe<ParkingLotBuilt>(_ => Log("Built a parking lot")));
        _subscriptions.Add(sim.Events.Subscribe<BikeAccessBooked>(e => Log($"Booked bike access tier {e.TierIndex} (from next opening)")));
        _subscriptions.Add(sim.Events.Subscribe<BikeAccessChanged>(e => Log($"{GameTime.Format(e.Tick)} bike access now {e.BikeCarrierPermille / 10}% of cabins")));
        _waysDirty = true;
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

    private void RebuildWayList()
    {
        _waysDirty = false;
        foreach (var child in _wayList.GetChildren())
            child.QueueFree();
        _wayRows.Clear();

        var ways = _host.Sim.State.Ways;
        _wayList.AddChild(new Label { Text = ways.Count == 0 ? "No paths or trails yet.\n[T] draw a trail down from the plateau." : "Paths and trails" });
        foreach (var way in ways)
        {
            var row = new HBoxContainer();
            var label = new Label { CustomMinimumSize = new Vector2(330, 0) };
            row.AddChild(label);
            int id = way.Id;
            if (way.Origin == WayOrigin.Player)
                AddButton(row, "Delete", () => _host.Enqueue(new DeleteWayCommand(id)));
            _wayList.AddChild(row);
            _wayRows.Add((id, label));
        }
    }

    private void UpdateWayRows()
    {
        var network = _host.Sim.Network;
        foreach (var (id, label) in _wayRows)
        {
            var way = network.FindWay(id);
            if (way is null || !network.TryGetGeometry(id, out var g)) continue;
            if (way.Kind == WayKind.AccessPath)
            {
                label.Text = $"{way.Name} · gravel · {g.LengthCm / 100} m · +{(g.EndHeightCm - g.StartHeightCm) / 100} m";
                continue;
            }
            var s = way.Stats;
            string avg = s.Runs == 0 ? "–" : $"{(double)s.SumRunMinutes / s.Runs:F1} min, fun {s.SumFun / s.Runs / 10}%";
            label.Text = $"{way.Name} · {g.Rating} · {g.LengthCm / 100} m, -{(g.StartHeightCm - g.EndHeightCm) / 100} m\n" +
                         $"   runs {s.Runs} (today {s.RunsToday}) · {avg}";
            label.Modulate = WayMeshes.RatingColor(g.Rating).Lerp(Colors.White, 0.55f);
        }
    }

    private void BuildUi()
    {
        var root = new VBoxContainer();
        AddChild(Panel(root, new Vector2(12, 12)));

        _stats = new Label();
        root.AddChild(_stats);
        _liftInfo = new Label();
        root.AddChild(_liftInfo);
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

        var skipRow = new HBoxContainer();
        root.AddChild(skipRow);
        AddButton(skipRow, "Skip to opening hours", _host.SkipToOpeningHours);
        AddButton(skipRow, "Turbo till closing hours", _host.TurboToClosingHours);

        var buildRow = new HBoxContainer();
        root.AddChild(buildRow);
        AddButton(buildRow, "Gravel path [P]", () => _tool.SetMode(_tool.Mode == WayTool.ToolMode.AccessPath ? WayTool.ToolMode.None : WayTool.ToolMode.AccessPath));
        AddButton(buildRow, "Trail [T]", () => _tool.SetMode(_tool.Mode == WayTool.ToolMode.Trail ? WayTool.ToolMode.None : WayTool.ToolMode.Trail));
        AddButton(buildRow, "Lift [L]", () => _structures.SetMode(_structures.Mode == StructureTool.ToolMode.Lift ? StructureTool.ToolMode.None : StructureTool.ToolMode.Lift));
        AddButton(buildRow, "Parking [K]", () => _structures.SetMode(_structures.Mode == StructureTool.ToolMode.Parking ? StructureTool.ToolMode.None : StructureTool.ToolMode.Parking));
        AddButton(buildRow, "Demo trails", _host.LoadDemoNetwork);
        AddButton(buildRow, "Follow rider [F]", _riders.FollowNext);

        var actionRow = new HBoxContainer();
        root.AddChild(actionRow);
        AddButton(actionRow, "Fee -", () => ChangeFee(-FeeStepCents));
        AddButton(actionRow, "Fee +", () => ChangeFee(FeeStepCents));
        AddButton(actionRow, "Bikes - [", () => ChangeBikeTier(-1));
        AddButton(actionRow, "Bikes + ]", () => ChangeBikeTier(1));
        AddButton(actionRow, "Save", _host.Save);
        AddButton(actionRow, "Load", () => Log(_host.Load() ? "Loaded save." : "No save found."));

        _toolInfo = new Label();
        root.AddChild(_toolInfo);
        _logLabel = new Label();
        root.AddChild(_logLabel);

        _wayList = new VBoxContainer();
        var wayPanel = Panel(_wayList, Vector2.Zero);
        wayPanel.AnchorLeft = wayPanel.AnchorRight = 1;
        wayPanel.GrowHorizontal = Control.GrowDirection.Begin;
        wayPanel.OffsetRight = -12;
        wayPanel.OffsetTop = 12;
        AddChild(wayPanel);
    }

    private static PanelContainer Panel(Control content, Vector2 position)
    {
        var panel = new PanelContainer { Position = position };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.55f),
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 8, ContentMarginBottom = 8,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        });
        panel.AddChild(content);
        return panel;
    }

    private void ChangeFee(long deltaCents) =>
        _host.Enqueue(new SetEntryFeeCommand(_host.Sim.State.Park.EntryFeeCents + deltaCents));

    private static void AddButton(Container parent, string text, Action onPressed)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        button.Pressed += onPressed;
        parent.AddChild(button);
    }

    private static string Money(long cents) => $"{cents / 100.0:N2} €";
}
