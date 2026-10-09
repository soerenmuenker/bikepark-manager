using Bikepark.Sim.Commands;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// The "features need repair" pop-up: raised when a feature falls below the warning level (or its trail closes because it
/// wore out), or opened from the Trails menu. Lists the trail's worn features, lets the player pick how many workers to
/// send and queues the repair. The trail is closed while the crew repairs a feature. Reads the sim, issues commands only.
/// </summary>
internal sealed partial class RepairDialog : PanelContainer
{
    private readonly HudContext _ctx;
    private readonly Queue<int> _waiting = new();
    private readonly HashSet<int> _dismissed = [];
    private bool _pausedByWarning;

    /// <summary>Opened from the Trails menu: shows every built feature (status overview), doesn't pause, stays open.</summary>
    private bool _overview;
    private int _wayId;
    private int _workers = 3;
    private Button _later = null!;
    private Label _title = null!, _subtitle = null!, _workersLabel = null!;
    private VBoxContainer _rows = null!;
    private string _signature = "";

    public RepairDialog(HudContext ctx)
    {
        _ctx = ctx;
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        var style = UiTheme.Box(new Color(UiTheme.Panel, 1f), 12, 16, 12, UiTheme.Warn, 2);
        AddThemeStyleboxOverride("panel", style);

        var box = new VBoxContainer { CustomMinimumSize = new Vector2(430, 0) };
        box.AddThemeConstantOverride("separation", 8);
        AddChild(box);
        var titleRow = new HBoxContainer();
        _title = UiTheme.Label("", 16, UiTheme.Warn, bold: true);
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        titleRow.AddChild(_title);
        titleRow.AddChild(new RoundButton(UiIcon.Close, 26, "", "Close (decide later)", () => Close(byPlayer: true)) { SizeFlagsVertical = SizeFlags.ShrinkCenter });
        box.AddChild(titleRow);
        _subtitle = UiTheme.Label("", 12, UiTheme.TextDim);
        _subtitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_subtitle);

        var workers = new HBoxContainer();
        workers.AddChild(UiTheme.Label("Workers to send", 13, bold: true));
        workers.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        workers.AddChild(UiTheme.Button("−", () => SetWorkers(_workers - 1), "Fewer workers"));
        _workersLabel = UiTheme.Label("", 14, bold: true);
        _workersLabel.CustomMinimumSize = new Vector2(28, 0);
        _workersLabel.HorizontalAlignment = HorizontalAlignment.Center;
        workers.AddChild(_workersLabel);
        workers.AddChild(UiTheme.Button("+", () => SetWorkers(_workers + 1), "More workers (faster repair)"));
        box.AddChild(workers);

        _rows = new VBoxContainer();
        _rows.AddThemeConstantOverride("separation", 4);
        box.AddChild(_rows);

        var buttons = new HBoxContainer();
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        buttons.AddChild(UiTheme.Button("Repair all", RepairAll, "Send the crew to every worn feature of this trail, the worst first"));
        _later = UiTheme.Button("Later", () => Close(byPlayer: true), "Decide later (click the trail in the Trails menu to open this again)");
        buttons.AddChild(_later);
        box.AddChild(buttons);
    }

    /// <summary>True while the pop-up shows a trail.</summary>
    public bool IsShowing => Visible;

    /// <summary>
    /// A warning came in: show it now (pausing the game), or after the ones before it. A trail the player closed the
    /// pop-up for stays quiet until <paramref name="force"/> (its trail closed because a feature wore out).
    /// </summary>
    /// <returns>False if it was ignored, already showing or already waiting.</returns>
    public bool Request(int wayId, bool force = false)
    {
        if (force) _dismissed.Remove(wayId);
        if (_dismissed.Contains(wayId)) return false;
        if (Visible && _wayId == wayId && _overview)
        {
            _overview = false; // a warning for the trail being looked at: now it counts
            PauseForWarning();
            _signature = "";
            return true;
        }
        if (Visible && _wayId == wayId || _waiting.Contains(wayId)) return false;
        if (Visible) _waiting.Enqueue(wayId);
        else Open(wayId, fromWarning: true);
        return true;
    }

    /// <summary>Open the status overview of a trail (click on it in the Trails menu); warnings pause the game, this doesn't.</summary>
    public void Open(int wayId) => Open(wayId, fromWarning: false);

    private void Open(int wayId, bool fromWarning)
    {
        _wayId = wayId;
        _workers = Math.Clamp(_ctx.Sim.State.Crew.Count, 1, Math.Max(1, _ctx.Sim.State.CrewRules.MaxWorkersPerJob));
        _signature = "";
        _overview = !fromWarning;
        if (fromWarning) PauseForWarning();
        Visible = true;
        Refresh();
    }

    private void PauseForWarning()
    {
        if (_pausedByWarning || _ctx.Host.Speed == 0) return;
        _ctx.Host.SetSpeedIndex(0);
        _pausedByWarning = true;
    }

    public void Clear()
    {
        _waiting.Clear();
        _dismissed.Clear();
        _pausedByWarning = false;
        Visible = false;
    }

    /// <summary>Closes the pop-up and shows the next waiting trail; when none is left, a game it paused goes on at 1x.</summary>
    private void Close(bool byPlayer)
    {
        if (byPlayer && !_overview) _dismissed.Add(_wayId);
        else _dismissed.Remove(_wayId);
        Visible = false;
        while (_waiting.Count > 0)
        {
            int next = _waiting.Dequeue();
            if (NeedsAttention(next))
            {
                Open(next, fromWarning: true);
                return;
            }
        }
        if (_pausedByWarning)
        {
            _pausedByWarning = false;
            _ctx.Host.SetSpeedIndex(1);
        }
    }

    private void RepairAll()
    {
        var state = _ctx.Sim.State;
        if (state.Ways.FirstOrDefault(w => w.Id == _wayId) is not { } way) return;
        // Each repair goes to the front of the queue, so queue the least worn first: the worst ends up on top.
        foreach (var feature in way.Features.Where(f => f.Built && f.Condition < TrailCondition.Perfect && Jobs.ForRepair(state, f.Id) is null)
                     .OrderByDescending(f => f.Condition))
            _ctx.Host.Enqueue(new RepairFeatureCommand(_wayId, feature.Id, _workers));
    }

    private void SetWorkers(int workers)
    {
        _workers = Math.Clamp(workers, 1, _ctx.Sim.State.CrewRules.MaxWorkersPerJob);
        _signature = "";
        Refresh();
    }

    private bool NeedsAttention(int wayId) =>
        _ctx.Sim.State.Ways.FirstOrDefault(w => w.Id == wayId) is { } way && way.Features.Any(f => f.Built && f.Condition < TrailCondition.Perfect);

    /// <summary>Called a few times a second: keeps the numbers current and closes the pop-up when nothing is left to repair.</summary>
    public void Refresh()
    {
        if (!Visible) return;
        var state = _ctx.Sim.State;
        var way = state.Ways.FirstOrDefault(w => w.Id == _wayId);
        if (way is null || !_overview && !NeedsAttention(_wayId))
        {
            Close(byPlayer: false);
            return;
        }

        var built = way.Features.Where(f => f.Built).OrderBy(f => f.Condition).ToList();
        _title.Text = _overview ? $"{way.Name}: features" : $"⚠ {way.Name}: feature needs repair";
        bool urgent = built.Any(f => TrailCondition.Permille(f) < state.WearRules.WarnBelowPermille) || way.WornOut;
        _title.AddThemeColorOverride("font_color", _overview && !urgent ? UiTheme.Text : UiTheme.Warn);
        _later.Text = _overview ? "Close" : "Later";
        _subtitle.Text = built.Count == 0 ? "No built features yet: only features wear (Build → trail features)."
            : way.WornOut ? "A feature is worn out: the trail is closed until it is repaired."
            : way.Repairing && Jobs.HasRidersOn(state, way.Id) ? "The trail is closed for the crew: they start once the last riders have left it."
            : way.Repairing ? "The crew is at work on a feature: the trail is closed until it is done."
            : "Repairs close the trail while the crew works on it. At 0 % the trail closes by itself.";
        _workersLabel.Text = _workers.ToString();

        var worn = _overview ? built : built.Where(f => f.Condition < TrailCondition.Perfect).ToList();
        string signature = string.Join(';', worn.Select(f =>
            $"{f.Id}:{f.Condition / 20_000}:{Jobs.ForRepair(state, f.Id)?.Progress / 20_000}")) + $"|{_workers}|{state.Crew.Count}";
        if (signature == _signature) return;
        _signature = signature;

        foreach (var child in _rows.GetChildren()) child.QueueFree();
        foreach (var feature in worn)
            _rows.AddChild(Row(state, way, feature));
    }

    private Control Row(Bikepark.Sim.State.WorldState state, Way way, TrailFeature feature)
    {
        var type = TrailFeatures.FindType(state.TrailFeatureTypes, feature.TypeId)!;
        int permille = TrailCondition.Permille(feature);
        var job = Jobs.ForRepair(state, feature.Id);
        var color = permille < state.WearRules.WarnBelowPermille ? UiTheme.Bad : permille < state.WearRules.RoughBelowPermille ? UiTheme.Warn : UiTheme.Text;

        var row = new HBoxContainer();
        var texts = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        texts.AddThemeConstantOverride("separation", 0);
        texts.AddChild(UiTheme.Label($"{type.Name} at {feature.DistanceCm / 100} m", 13, bold: true));

        string detail;
        if (feature.Condition >= TrailCondition.Perfect && job is null)
            detail = "like new";
        else if (job is not null)
            detail = $"repair {WorkCosts.ProgressPermille(state.CrewRules, job) / 10} % done · {(job.Workers > 0 ? job.Workers : state.CrewRules.MaxWorkersPerJob)} workers";
        else
        {
            long minutes = WorkCosts.RepairMinutes(state.CrewRules, type, feature);
            int speed = WorkCosts.SpeedPermille(state, WorkCosts.RepairWorkType(type));
            long perWorker = Math.Max(1, minutes * 1000 / speed);
            long minutesWithCrew = (perWorker + _workers - 1) / _workers;
            detail = $"{HudContext.CrewTime(minutes)} · about {Duration(minutesWithCrew)} with {_workers} workers";
        }
        if (feature.Crashes > 0)
            detail += $" · {feature.Crashes} crash{(feature.Crashes == 1 ? "" : "es")} here";
        texts.AddChild(UiTheme.Label(detail, 11, feature.Crashes > 0 ? UiTheme.Warn : UiTheme.TextDim));
        row.AddChild(texts);

        row.AddChild(UiTheme.Label($"{permille / 10} %", 15, color, bold: true));

        int wayId = way.Id, featureId = feature.Id, workers = _workers;
        var button = UiTheme.Button(job is not null ? "Repairing…" : "Repair",
            () => _ctx.Host.Enqueue(new RepairFeatureCommand(wayId, featureId, workers)),
            "Send the crew: the repair goes to the front of the queue, the trail is closed while they work");
        button.Disabled = job is not null || state.Crew.Count == 0 || feature.Condition >= TrailCondition.Perfect;
        if (state.Crew.Count == 0) button.TooltipText = "Hire workers first (Crew menu)";
        row.AddChild(button);
        return row;
    }

    private static string Duration(long minutes) => minutes >= 90 ? $"{(minutes + 30) / 60} h" : $"{minutes} min";
}
