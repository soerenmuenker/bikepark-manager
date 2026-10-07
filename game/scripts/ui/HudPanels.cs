using Bikepark.Game.Camera;
using Bikepark.Game.Lifts;
using Bikepark.Game.Riders;
using Bikepark.Game.Terrain;
using Bikepark.Game.Ways;
using Bikepark.Sim;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Reporting;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;
using Godot;
using Gradient = Bikepark.Sim.Trails.Gradient;

namespace Bikepark.Game.Ui;

/// <summary>What the HUD panels work with. Panels read the simulation and issue commands; they never write state.</summary>
internal sealed class HudContext
{
    public required SimHost Host { get; init; }
    public required WayTool Ways { get; init; }
    public required StructureTool Structures { get; init; }
    public required FeatureTool Features { get; init; }
    public required RiderView Riders { get; init; }
    public required TerrainView Terrain { get; init; }
    public required RtsCamera Camera { get; init; }

    /// <summary>Closed days seen this session (from <see cref="DayEnded"/>), for the finance chart.</summary>
    public List<DayReport> Days { get; } = [];

    public Simulation Sim => Host.Sim;

    public KpiReport Kpi { get; set; } = null!;

    public const int FeeStepCents = 250;

    public void ChangeFee(long delta) => Host.Enqueue(new SetEntryFeeCommand(Math.Max(0, Sim.State.Park.EntryFeeCents + delta)));

    /// <summary>Books the next lower/higher tier on the first operator-run lift (from the next opening).</summary>
    public void StepBikeTier(int step)
    {
        var state = Sim.State;
        var lift = state.Lifts.FirstOrDefault(l => l.BikeAccess is not null);
        if (lift is null || LiftNetwork.FindOperator(state, lift.OperatorId) is not { } op) return;
        int current = lift.BikeAccess!.PendingTierIndex ?? lift.BikeAccess.TierIndex;
        int next = Math.Clamp(current + step, 0, op.BikeAccessTiers.Count - 1);
        if (next != current) Host.Enqueue(new SetLiftBikeAccessCommand(lift.Id, next));
    }
}

/// <summary>A menu panel that opens above the bar: header with title and close button, then the body.</summary>
public abstract partial class HudPanel : PanelContainer
{
    private protected HudContext Ctx = null!;
    protected VBoxContainer Body = null!;

    public event Action? CloseRequested;

    public abstract string Title { get; }

    internal void Init(HudContext ctx)
    {
        Ctx = ctx;
        AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, 14, 18, 14, new Color(UiTheme.Accent, 0.35f), 1));
        MouseFilter = MouseFilterEnum.Stop;
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        AddChild(root);

        var header = new HBoxContainer();
        root.AddChild(header);
        var title = UiTheme.Label(Title.ToUpperInvariant(), 14, UiTheme.Accent, bold: true);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        header.AddChild(new RoundButton(UiIcon.Close, 24, "", "Close (Esc)", () => CloseRequested?.Invoke()));

        Body = new VBoxContainer();
        Body.AddThemeConstantOverride("separation", 10);
        root.AddChild(Body);
        Build();
    }

    protected abstract void Build();

    /// <summary>Called a few times per second while open.</summary>
    public abstract void Refresh();

    protected static HBoxContainer Row(int separation = 18)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", separation);
        return row;
    }
}

/// <summary>A clickable card with an icon, a name and a hint (build tools).</summary>
public partial class ToolCard : PanelContainer
{
    private readonly StyleBoxFlat _normal, _hover, _active;
    private bool _isActive, _isHover;

    public ToolCard() { _normal = _hover = _active = new StyleBoxFlat(); }

    public ToolCard(UiIcon icon, string name, string hint, string tooltip, Action onPressed)
    {
        _normal = UiTheme.Box(UiTheme.Card, 10, 10, 10);
        _hover = UiTheme.Box(UiTheme.CardHover, 10, 10, 10);
        _active = UiTheme.Box(UiTheme.AccentDark, 10, 10, 10, UiTheme.Accent, 2);
        foreach (var box in new[] { _normal, _hover, _active }) box.ShadowSize = 0;
        CustomMinimumSize = new Vector2(118, 112);
        TooltipText = tooltip;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        AddThemeStyleboxOverride("panel", _normal);

        var box2 = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        box2.AddThemeConstantOverride("separation", 4);
        AddChild(box2);
        var iconView = new IconView(icon, 42) { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        box2.AddChild(iconView);
        var title = UiTheme.Label(name, 14, bold: true);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.MouseFilter = MouseFilterEnum.Ignore;
        box2.AddChild(title);
        var hintLabel = UiTheme.Label(hint, 11, UiTheme.TextDim);
        hintLabel.HorizontalAlignment = HorizontalAlignment.Center;
        hintLabel.MouseFilter = MouseFilterEnum.Ignore;
        box2.AddChild(hintLabel);

        MouseEntered += () => { _isHover = true; UpdateStyle(); };
        MouseExited += () => { _isHover = false; UpdateStyle(); };
        GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            {
                onPressed();
                AcceptEvent();
            }
        };
    }

    public bool Active
    {
        get => _isActive;
        set { if (_isActive != value) { _isActive = value; UpdateStyle(); } }
    }

    private void UpdateStyle() => AddThemeStyleboxOverride("panel", _isActive ? _active : _isHover ? _hover : _normal);
}

// ==================================================================== Build

public partial class BuildPanel : HudPanel
{
    private ToolCard _path = null!, _trail = null!, _lift = null!, _parking = null!;
    private readonly List<(string TypeId, ToolCard Card)> _features = [];
    private Label _demoStatus = null!;

    public override string Title => "Build";

    protected override void Build()
    {
        var cards = Row(10);
        Body.AddChild(cards);
        _path = new ToolCard(UiIcon.Path, "Gravel path", "P", "Two-way access path riders can pedal up (max gradient ±4.0)",
            () => ToggleWay(WayTool.ToolMode.AccessPath));
        _trail = new ToolCard(UiIcon.Trail, "Trail", "T", "One-way downhill trail; start it on a plateau or path",
            () => ToggleWay(WayTool.ToolMode.Trail));
        _lift = new ToolCard(UiIcon.Lift, "Lift", "L", "Gondola: click the valley station, then the top (debug, free)",
            () => ToggleStructure(StructureTool.ToolMode.Lift));
        _parking = new ToolCard(UiIcon.Parking, "Parking lot", "K", "Parking next to a valley station (debug, free)",
            () => ToggleStructure(StructureTool.ToolMode.Parking));
        var demo = new ToolCard(UiIcon.Demo, "Demo trails", "", "Builds two example trails from the plateau", Ctx.Host.LoadDemoNetwork);
        var demoFeatures = new ToolCard(UiIcon.Demo, "Demo features", "", "Puts berms, jumps and wood features on the demo trails",
            () =>
            {
                _demoStatus.Text = Ctx.Host.LoadDemoFeatures() ? "" : "Build the demo trails first.";
                _demoStatus.Visible = _demoStatus.Text.Length > 0;
            });
        foreach (var card in new[] { _path, _trail, _lift, _parking, demo, demoFeatures }) cards.AddChild(card);
        _demoStatus = UiTheme.Label("", 12, UiTheme.Warn);
        _demoStatus.Visible = false;
        Body.AddChild(_demoStatus);
        Body.AddChild(UiTheme.Label("Click or drag to place points · Backspace undo · Enter build · Esc cancel", 12, UiTheme.TextDim));

        Body.AddChild(UiTheme.Separator());
        Body.AddChild(UiTheme.Label("TRAIL FEATURES", 13, UiTheme.Accent, bold: true));
        var types = Ctx.Sim.State.TrailFeatureTypes;
        if (types.Count == 0)
        {
            Body.AddChild(UiTheme.Label("No trail features in this scenario.", 12, UiTheme.TextDim));
            return;
        }
        var groups = Row(22);
        Body.AddChild(groups);
        foreach (var (material, caption) in new[] { (FeatureMaterial.Dirt, "Dirt"), (FeatureMaterial.Wood, "Wood") })
        {
            var group = new VBoxContainer();
            group.AddThemeConstantOverride("separation", 4);
            group.AddChild(UiTheme.Label(caption, 12, UiTheme.TextDim, bold: true));
            var row = Row(8);
            group.AddChild(row);
            foreach (var type in types.Where(t => t.Material == material))
            {
                string id = type.Id;
                var card = new ToolCard(FeatureIcon(type.Kind), type.Name, $"{type.LengthMeters} m · {DifficultyWord(type.Difficulty)}",
                    FeatureTooltip(type), () => ToggleFeature(id)) { CustomMinimumSize = new Vector2(100, 104) };
                row.AddChild(card);
                _features.Add((id, card));
            }
            if (row.GetChildCount() > 0) groups.AddChild(group);
        }
        Body.AddChild(UiTheme.Label("Point at a trail and click to place · Delete removes the feature under the cursor · Esc stops", 12, UiTheme.TextDim));
    }

    internal static UiIcon FeatureIcon(FeatureKind kind) => kind switch
    {
        FeatureKind.Berm => UiIcon.Berm,
        FeatureKind.Rollers => UiIcon.Rollers,
        FeatureKind.Table => UiIcon.Table,
        FeatureKind.Double => UiIcon.Double,
        FeatureKind.WallRide => UiIcon.WallRide,
        FeatureKind.Kicker => UiIcon.Kicker,
        _ => UiIcon.Drop,
    };

    private static string DifficultyWord(int difficulty) => difficulty switch
    {
        < WayGeometry.GreenMaxDifficulty => "green",
        < WayGeometry.BlueMaxDifficulty => "blue",
        < WayGeometry.RedMaxDifficulty => "red",
        _ => "black",
    };

    private static string FeatureTooltip(TrailFeatureType type)
    {
        string where = type.MaxGradient < 0
            ? $"needs a drop between {Gradient.Format(type.MaxGradient)} and {Gradient.Format(type.MinGradient)}"
            : $"gradient {Gradient.Format(type.MinGradient)} to {Gradient.Format(type.MaxGradient)}";
        string bend = type.MinTurn > 0 ? ", on a bend" : "";
        return $"{type.Name}: {type.LengthMeters} m, difficulty {type.Difficulty} ({DifficultyWord(type.Difficulty)}); {where}{bend}. " +
               $"Flow riders {type.FlowAffinity / 10}%, technical riders {type.TechAffinity / 10}%.";
    }

    private void ToggleWay(WayTool.ToolMode mode)
    {
        Ctx.Structures.SetMode(StructureTool.ToolMode.None);
        Ctx.Features.SetType(null);
        Ctx.Ways.SetMode(Ctx.Ways.Mode == mode ? WayTool.ToolMode.None : mode);
    }

    private void ToggleStructure(StructureTool.ToolMode mode)
    {
        Ctx.Features.SetType(null);
        Ctx.Structures.SetMode(Ctx.Structures.Mode == mode ? StructureTool.ToolMode.None : mode);
    }

    private void ToggleFeature(string typeId) => Ctx.Features.SetType(Ctx.Features.TypeId == typeId ? null : typeId);

    public override void Refresh()
    {
        _path.Active = Ctx.Ways.Mode == WayTool.ToolMode.AccessPath;
        _trail.Active = Ctx.Ways.Mode == WayTool.ToolMode.Trail;
        _lift.Active = Ctx.Structures.Mode == StructureTool.ToolMode.Lift;
        _parking.Active = Ctx.Structures.Mode == StructureTool.ToolMode.Parking;
        foreach (var (id, card) in _features)
            card.Active = Ctx.Features.TypeId == id;
    }
}

// ==================================================================== Trails

public partial class TrailsPanel : HudPanel
{
    private VBoxContainer _list = null!;
    private readonly List<(int Id, Label Main, Label Detail)> _rows = [];
    private const int MaxListHeight = 420;
    private string _signature = "";

    public override string Title => "Trails & paths";

    protected override void Build()
    {
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(560, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_list);
        Body.AddChild(scroll);
    }

    public override void Refresh()
    {
        var ways = Ctx.Sim.State.Ways;
        if (Signature(ways) != _signature) Rebuild(ways);
        var scroll = (ScrollContainer)_list.GetParent();
        scroll.CustomMinimumSize = new Vector2(560, Math.Min(MaxListHeight, Math.Max(40, _list.GetCombinedMinimumSize().Y)));

        var network = Ctx.Sim.Network;
        foreach (var (id, main, detail) in _rows)
        {
            var way = network.FindWay(id);
            if (way is null || !network.TryGetGeometry(id, out var g)) continue;
            if (way.Kind == WayKind.AccessPath)
            {
                main.Text = $"{way.Name}";
                detail.Text = $"Gravel path · {g.LengthCm / 100} m · +{(g.EndHeightCm - g.StartHeightCm) / 100} m" +
                              (way.Origin == WayOrigin.Scenario ? " · existing hiking route" : "");
                continue;
            }
            var s = way.Stats;
            string avg = s.Runs == 0 ? "no runs yet" : $"{(double)s.SumRunMinutes / s.Runs:F1} min · fun {s.SumFun / s.Runs / 10}%";
            main.Text = way.Name;
            detail.Text = $"{g.Rating} · {g.LengthCm / 100} m · -{(g.StartHeightCm - g.EndHeightCm) / 100} m · steepest {Gradient.Format(-g.MaxDropGradient)}\n" +
                          $"{s.Runs} runs ({s.RunsToday} today) · {avg}\n" +
                          FeatureSummary(network.FeaturesOn(id));
        }
    }

    /// <summary>"2 berms · Tabletop · Drop" (in the order types first appear along the trail).</summary>
    private static string FeatureSummary(IReadOnlyList<PlacedFeature> features)
    {
        if (features.Count == 0) return "No features yet (Build → trail features)";
        var parts = features.GroupBy(f => f.Type.Id).Select(g => g.Count() == 1 ? g.First().Type.Name : $"{g.Count()} × {g.First().Type.Name}");
        return $"Features: {string.Join(" · ", parts)}";
    }

    /// <summary>Ways and their features; the list is rebuilt when it changes.</summary>
    private static string Signature(List<Way> ways) =>
        string.Join(';', ways.Select(w => $"{w.Id}:{string.Join(',', w.Features.Select(f => f.Id))}"));

    private void Rebuild(List<Way> ways)
    {
        _signature = Signature(ways);
        foreach (var child in _list.GetChildren()) child.QueueFree();
        _rows.Clear();
        if (ways.Count == 0)
        {
            _list.AddChild(UiTheme.Label("Nothing built yet. Open Build and draw a trail down from the plateau.", 13, UiTheme.TextDim));
            return;
        }
        var network = Ctx.Sim.Network;
        foreach (var way in ways)
        {
            var row = new PanelContainer();
            var box = UiTheme.Box(UiTheme.Card, 8, 10, 6);
            box.ShadowSize = 0;
            row.AddThemeStyleboxOverride("panel", box);
            var h = Row(10);
            row.AddChild(h);
            var color = way.Kind == WayKind.AccessPath ? new Color(0.8f, 0.8f, 0.8f)
                : network.TryGetGeometry(way.Id, out var g) ? WayMeshes.RatingColor(g.Rating) : Colors.Gray;
            h.AddChild(new ColorRect { Color = color, CustomMinimumSize = new Vector2(6, 38) });
            var texts = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            texts.AddThemeConstantOverride("separation", 0);
            var main = UiTheme.Label("", 14, bold: true);
            var detail = UiTheme.Label("", 12, UiTheme.TextDim);
            texts.AddChild(main);
            texts.AddChild(detail);
            h.AddChild(texts);
            int id = way.Id;
            var placed = network.FeaturesOn(id);
            if (placed.Count > 0)
            {
                // One small chip per feature; clicking removes it.
                var chips = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                chips.AddThemeConstantOverride("h_separation", 4);
                chips.AddThemeConstantOverride("v_separation", 4);
                foreach (var feature in placed)
                {
                    int featureId = feature.Feature.Id;
                    var chip = UiTheme.Button($"{feature.Type.Name} {feature.StartCm / 100} m  ✕",
                        () => Ctx.Host.Enqueue(new RemoveTrailFeatureCommand(id, featureId)), "Remove this feature");
                    chip.AddThemeFontSizeOverride("font_size", 11);
                    chips.AddChild(chip);
                }
                texts.AddChild(chips);
            }
            if (way.Origin == WayOrigin.Player)
            {
                var delete = UiTheme.Button("Delete", () => Ctx.Host.Enqueue(new DeleteWayCommand(id)), "Remove this way (not while others attach to it)");
                delete.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                var danger = UiTheme.Box(new Color(UiTheme.Bad, 0.12f), 6, 10, 4, new Color(UiTheme.Bad, 0.7f), 1);
                danger.ShadowSize = 0;
                var dangerHover = UiTheme.Box(new Color(UiTheme.Bad, 0.35f), 6, 10, 4, UiTheme.Bad, 1);
                dangerHover.ShadowSize = 0;
                delete.AddThemeStyleboxOverride("normal", danger);
                delete.AddThemeStyleboxOverride("hover", dangerHover);
                delete.AddThemeStyleboxOverride("pressed", dangerHover);
                delete.AddThemeFontSizeOverride("font_size", 12);
                h.AddChild(delete);
            }
            _list.AddChild(row);
            _rows.Add((id, main, detail));
        }
    }
}

// ==================================================================== Riders

public partial class RidersPanel : HudPanel
{
    private Label _inPark = null!, _riding = null!, _queuing = null!, _onLift = null!, _walking = null!;
    private Label _mood = null!, _exitMood = null!, _fun = null!, _runs = null!, _visitors = null!, _unhappy = null!, _turned = null!;
    private ProgressBar _moodBar = null!;
    private Label _follow = null!;

    public override string Title => "Riders";

    protected override void Build()
    {
        var now = Row();
        foreach (var (caption, assign) in new (string, Action<Label>)[]
                 {
                     ("In park", l => _inPark = l), ("On trails", l => _riding = l), ("Queuing", l => _queuing = l),
                     ("On the lift", l => _onLift = l), ("Walking / pedalling", l => _walking = l),
                 })
        {
            now.AddChild(UiTheme.StatTile(caption, out var value));
            assign(value);
        }
        Body.AddChild(now);
        Body.AddChild(UiTheme.Separator());

        var moodRow = Row();
        moodRow.AddChild(UiTheme.StatTile("Mood now", out _mood, 24));
        _moodBar = new ProgressBar { MinValue = 0, MaxValue = 1000, ShowPercentage = false, CustomMinimumSize = new Vector2(180, 10), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        moodRow.AddChild(_moodBar);
        moodRow.AddChild(UiTheme.StatTile("Mood when leaving", out _exitMood));
        moodRow.AddChild(UiTheme.StatTile("Avg run fun", out _fun));
        Body.AddChild(moodRow);

        var totals = Row();
        totals.AddChild(UiTheme.StatTile("Runs", out _runs, 16));
        totals.AddChild(UiTheme.StatTile("Visitors", out _visitors, 16));
        totals.AddChild(UiTheme.StatTile("Left unhappy", out _unhappy, 16));
        totals.AddChild(UiTheme.StatTile("Turned away", out _turned, 16));
        Body.AddChild(totals);
        Body.AddChild(UiTheme.Separator());

        var follow = Row(10);
        follow.AddChild(UiTheme.Button("Follow a rider  [F]", Ctx.Riders.FollowNext));
        _follow = UiTheme.Label("", 12, UiTheme.TextDim);
        _follow.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        follow.AddChild(_follow);
        Body.AddChild(follow);
    }

    public override void Refresh()
    {
        var k = Ctx.Kpi;
        var guests = Ctx.Sim.State.Guests;
        _inPark.Text = $"{k.GuestsInPark}";
        _riding.Text = $"{k.RidersOnTrails}";
        _queuing.Text = $"{k.GuestsQueuing}";
        _onLift.Text = $"{k.RidersOnLifts}";
        _walking.Text = $"{guests.Count(g => g.Activity is RiderActivity.Walking or RiderActivity.Climbing)}";
        _mood.Text = guests.Count == 0 ? "–" : $"{k.AverageHappiness / 10}%";
        _mood.AddThemeColorOverride("font_color", guests.Count == 0 ? UiTheme.Text : UiTheme.MoodColor(k.AverageHappiness));
        _moodBar.Value = k.AverageHappiness;
        _moodBar.AddThemeStyleboxOverride("fill", Fill(UiTheme.MoodColor(k.AverageHappiness)));
        _exitMood.Text = k.AverageExitHappiness == 0 ? "–" : $"{k.AverageExitHappiness / 10}%";
        _fun.Text = k.RunsCompleted == 0 ? "–" : $"{k.AverageRunFun / 10}%";
        _runs.Text = $"{k.RunsCompleted:N0}";
        _visitors.Text = $"{k.TotalVisitors:N0}";
        _unhappy.Text = $"{k.TotalLeftUnhappy:N0}";
        _turned.Text = k.TotalTurnedAwayParkingFull > 0 ? $"{k.TotalTurnedAway:N0} ({k.TotalTurnedAwayParkingFull:N0} parking full)" : $"{k.TotalTurnedAway:N0}";
        _follow.Text = Ctx.Riders.FollowedRider ?? "Camera follows a rider; pan to stop.";
    }

    private static StyleBoxFlat Fill(Color color)
    {
        var box = UiTheme.Box(color, 4, 0, 0);
        box.ShadowSize = 0;
        return box;
    }
}

// ==================================================================== Lifts

public partial class LiftsPanel : HudPanel
{
    private VBoxContainer _list = null!;
    private string _signature = "";
    private readonly List<Action> _updates = [];

    public override string Title => "Lifts";

    protected override void Build()
    {
        _list = new VBoxContainer();
        _list.AddThemeConstantOverride("separation", 12);
        Body.AddChild(_list);
    }

    public override void Refresh()
    {
        var state = Ctx.Sim.State;
        string signature = string.Join(',', state.Lifts.Select(l => $"{l.Id}:{l.BikeAccess?.TierIndex}:{l.BikeAccess?.PendingTierIndex}"));
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild();
        }
        foreach (var update in _updates) update();
    }

    private void Rebuild()
    {
        foreach (var child in _list.GetChildren()) child.QueueFree();
        _updates.Clear();
        var state = Ctx.Sim.State;
        if (state.Lifts.Count == 0)
        {
            _list.AddChild(UiTheme.Label("No lifts. Riders pedal up the gravel paths.", 13, UiTheme.TextDim));
            return;
        }

        foreach (var lift in state.Lifts)
        {
            int liftId = lift.Id;
            var type = LiftNetwork.FindType(state, lift.TypeId);
            var op = LiftNetwork.FindOperator(state, lift.OperatorId);
            if (type is null) continue;

            var header = Row(10);
            header.AddChild(new IconView(UiIcon.Lift, 30, UiTheme.Accent));
            var names = new VBoxContainer();
            names.AddThemeConstantOverride("separation", 0);
            names.AddChild(UiTheme.Label(lift.Name, 17, bold: true));
            names.AddChild(UiTheme.Label($"{type.Name} · run by {op?.Name ?? "the park"}", 12, UiTheme.TextDim));
            header.AddChild(names);
            _list.AddChild(header);

            var tiles = Row();
            tiles.AddChild(UiTheme.StatTile("Queue", out var queue, 24));
            tiles.AddChild(UiTheme.StatTile("Wait now", out var wait, 24));
            tiles.AddChild(UiTheme.StatTile("Bike capacity", out var capacity));
            tiles.AddChild(UiTheme.StatTile("Rides today", out var today));
            tiles.AddChild(UiTheme.StatTile("Avg wait", out var avg));
            tiles.AddChild(UiTheme.StatTile("Longest queue today", out var longest));
            _list.AddChild(tiles);
            var bar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 8) };
            _list.AddChild(bar);

            _updates.Add(() =>
            {
                var l = Ctx.Sim.State.Lifts.FirstOrDefault(x => x.Id == liftId);
                if (l is null) return;
                int minutes = LiftMath.ExpectedWaitMinutes(type, l.BikeCarrierPermille, l.Queue.Count);
                queue.Text = $"{l.Queue.Count}";
                wait.Text = minutes < 0 ? "no bikes" : minutes == 0 ? "none" : $"~{minutes} min";
                wait.AddThemeColorOverride("font_color", minutes < 0 || minutes > 20 ? UiTheme.Bad : minutes > 8 ? UiTheme.Warn : UiTheme.Good);
                capacity.Text = $"{LiftMath.BikeRidersPerHour(type, l.BikeCarrierPermille)}/h";
                today.Text = $"{l.Stats.RidersToday}";
                avg.Text = l.Stats.Riders == 0 ? "–" : $"{(double)l.Stats.SumWaitMinutes / l.Stats.Riders:F1} min";
                longest.Text = $"{l.Stats.MaxQueueToday}";
                bar.MaxValue = Math.Max(60, l.Stats.MaxQueue);
                bar.Value = l.Queue.Count;
            });

            if (op is not null && lift.BikeAccess is { } access)
            {
                _list.AddChild(UiTheme.Label($"BIKE ACCESS — rented from {op.Name}, changes start at the next opening   [ / ]", 11, UiTheme.TextDim, bold: true));
                var tiers = Row(8);
                for (int i = 0; i < op.BikeAccessTiers.Count; i++)
                {
                    var tier = op.BikeAccessTiers[i];
                    int index = i;
                    string mark = i == access.TierIndex ? "  ● now" : i == access.PendingTierIndex ? "  ◌ booked" : "";
                    var button = new Button
                    {
                        Text = $"{tier.Name}{mark}\n{tier.BikeCarrierPermille / 10}% of cabins · {UiTheme.Money(tier.DailyFeeCents)}/day",
                        ToggleMode = true,
                        ButtonPressed = i == (access.PendingTierIndex ?? access.TierIndex),
                        FocusMode = FocusModeEnum.None,
                        CustomMinimumSize = new Vector2(150, 48),
                        TooltipText = i == access.TierIndex ? "In effect today" : "Book from the next opening",
                    };
                    button.AddThemeFontSizeOverride("font_size", 12);
                    button.Pressed += () => Ctx.Host.Enqueue(new SetLiftBikeAccessCommand(liftId, index));
                    tiers.AddChild(button);
                }
                _list.AddChild(tiers);
            }
        }
    }
}

// ==================================================================== Finance

public partial class FinancePanel : HudPanel
{
    private Label _money = null!, _revenue = null!, _expenses = null!, _net = null!;
    private Label _totalRevenue = null!, _totalExpenses = null!, _liftFees = null!, _fee = null!;
    private DayChart _chart = null!;

    public override string Title => "Finances";

    protected override void Build()
    {
        var top = Row(24);
        top.AddChild(UiTheme.StatTile("Balance", out _money, 28));
        top.AddChild(UiTheme.StatTile("Revenue today", out _revenue));
        top.AddChild(UiTheme.StatTile("Expenses today", out _expenses));
        top.AddChild(UiTheme.StatTile("Net today", out _net));
        Body.AddChild(top);

        var totals = Row(24);
        totals.AddChild(UiTheme.StatTile("Total revenue", out _totalRevenue, 16));
        totals.AddChild(UiTheme.StatTile("Total expenses", out _totalExpenses, 16));
        totals.AddChild(UiTheme.StatTile("Lift fees paid", out _liftFees, 16));
        Body.AddChild(totals);
        Body.AddChild(UiTheme.Separator());

        var fee = Row(8);
        fee.AddChild(UiTheme.Label("DAY TICKET", 11, UiTheme.TextDim, bold: true));
        fee.AddChild(UiTheme.Button("−", () => Ctx.ChangeFee(-HudContext.FeeStepCents), "Lower the entry fee"));
        _fee = UiTheme.Label("", 18, bold: true);
        _fee.CustomMinimumSize = new Vector2(80, 0);
        _fee.HorizontalAlignment = HorizontalAlignment.Center;
        fee.AddChild(_fee);
        fee.AddChild(UiTheme.Button("+", () => Ctx.ChangeFee(HudContext.FeeStepCents), "Raise the entry fee (fewer guests come)"));
        Body.AddChild(fee);

        Body.AddChild(UiTheme.Label("LAST DAYS — revenue and expenses", 11, UiTheme.TextDim, bold: true));
        _chart = new DayChart { CustomMinimumSize = new Vector2(520, 110) };
        Body.AddChild(_chart);
    }

    public override void Refresh()
    {
        var f = Ctx.Sim.State.Finance;
        _money.Text = UiTheme.MoneyExact(f.MoneyCents);
        _money.AddThemeColorOverride("font_color", f.MoneyCents < 0 ? UiTheme.Bad : UiTheme.Text);
        _revenue.Text = UiTheme.Money(f.RevenueTodayCents);
        _expenses.Text = UiTheme.Money(f.ExpensesTodayCents);
        long net = f.RevenueTodayCents - f.ExpensesTodayCents;
        _net.Text = UiTheme.Money(net);
        _net.AddThemeColorOverride("font_color", net >= 0 ? UiTheme.Good : UiTheme.Bad);
        _totalRevenue.Text = UiTheme.Money(f.TotalRevenueCents);
        _totalExpenses.Text = UiTheme.Money(f.TotalExpensesCents);
        _liftFees.Text = UiTheme.Money(f.TotalLiftFeesCents);
        _fee.Text = UiTheme.MoneyExact(Ctx.Sim.State.Park.EntryFeeCents);
        _chart.Days = Ctx.Days;
        _chart.QueueRedraw();
    }
}

/// <summary>Revenue (green) and expense (red) bars for the last 14 closed days.</summary>
public partial class DayChart : Control
{
    internal List<DayReport> Days { get; set; } = [];

    public override void _Draw()
    {
        var days = Days.TakeLast(14).ToList();
        var font = UiTheme.Regular;
        if (days.Count == 0)
        {
            DrawString(font, new Vector2(0, 20), "No day closed yet.", HorizontalAlignment.Left, -1, 13, UiTheme.TextDim);
            return;
        }
        float max = Math.Max(1, days.Max(d => Math.Max(d.RevenueCents, d.ExpensesCents)));
        float slot = Size.X / 14f;
        float bottom = Size.Y - 16;
        DrawLine(new Vector2(0, bottom), new Vector2(Size.X, bottom), UiTheme.Line, 1);
        for (int i = 0; i < days.Count; i++)
        {
            var d = days[i];
            float x = i * slot + slot * 0.15f;
            float bar = slot * 0.32f;
            float hr = d.RevenueCents / max * (bottom - 6);
            float he = d.ExpensesCents / max * (bottom - 6);
            DrawRect(new Rect2(x, bottom - hr, bar, hr), UiTheme.Good);
            DrawRect(new Rect2(x + bar + 2, bottom - he, bar, he), UiTheme.Bad);
            DrawString(font, new Vector2(i * slot, Size.Y - 2), $"D{d.Day + 1}", HorizontalAlignment.Center, slot, 10, UiTheme.TextDim);
        }
    }
}

// ==================================================================== Map

public partial class MapPanel : HudPanel
{
    private readonly List<(TerrainView.OverlayMode Mode, Button Button)> _modes = [];
    private Label _cursor = null!;

    public override string Title => "Map overlays";

    protected override void Build()
    {
        var row = Row(8);
        foreach (var mode in Enum.GetValues<TerrainView.OverlayMode>())
        {
            var m = mode;
            var button = new Button { Text = mode.ToString(), ToggleMode = true, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(110, 36) };
            button.Pressed += () => Ctx.Terrain.SetOverlay(m);
            row.AddChild(button);
            _modes.Add((mode, button));
        }
        Body.AddChild(row);
        Body.AddChild(UiTheme.Label("F1 cycles the overlay", 11, UiTheme.TextDim));
        Body.AddChild(UiTheme.Separator());
        _cursor = UiTheme.Label("", 13);
        _cursor.CustomMinimumSize = new Vector2(480, 0);
        Body.AddChild(_cursor);
    }

    public override void Refresh()
    {
        foreach (var (mode, button) in _modes) button.SetPressedNoSignal(Ctx.Terrain.Overlay == mode);
        _cursor.Text = CursorInfo(Ctx);
    }

    internal static string CursorInfo(HudContext ctx)
    {
        var grid = ctx.Terrain.Grid;
        var mouse = ctx.Terrain.GetViewport().GetMousePosition();
        var camera = ctx.Camera.Camera;
        if (grid is null || !ctx.Terrain.TryRaycast(camera.ProjectRayOrigin(mouse), camera.ProjectRayNormal(mouse), 4000f, out var hit))
            return "Point at the terrain for details.";
        var sample = grid.Sample((long)MathF.Round(hit.X * 100), (long)MathF.Round(hit.Z * 100));
        float degrees = Mathf.RadToDeg(MathF.Atan(sample.SlopePermille / 1000f));
        return $"Elevation {sample.HeightCm / 100f:F0} m   slope {Gradient.Format(Gradient.FromPermille(sample.SlopePermille)).TrimStart('+')} ({degrees:F0}°)   {sample.Surface}\n" +
               $"Trees {sample.TreeDensity}   rock {sample.Rock}   roots {sample.Roots}   at {hit.X:F0}, {hit.Z:F0} m";
    }
}

// ==================================================================== System

public partial class SystemPanel : HudPanel
{
    public override string Title => "Game";

    protected override void Build()
    {
        var time = Row(8);
        time.AddChild(UiTheme.Button("Skip to opening", Ctx.Host.SkipToOpeningHours, "Fast-forward to the next opening time"));
        time.AddChild(UiTheme.Button("Turbo till closing", Ctx.Host.TurboToClosingHours, "Run fast until the park closes"));
        Body.AddChild(time);
        var files = Row(8);
        files.AddChild(UiTheme.Button("Save", Ctx.Host.Save));
        files.AddChild(UiTheme.Button("Load", () => Ctx.Host.Load()));
        Body.AddChild(files);
        Body.AddChild(UiTheme.Label("Space pause · 1–4 speed · +/− zoom · B build · V trails · R riders · G lifts · M finances · O map", 11, UiTheme.TextDim));
    }

    public override void Refresh() { }
}
