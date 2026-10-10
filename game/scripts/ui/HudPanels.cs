using Bikepark.Game.Camera;
using Bikepark.Game.Crew;
using Bikepark.Game.Lifts;
using Bikepark.Game.Riders;
using Bikepark.Game.Terrain;
using Bikepark.Game.Ways;
using Bikepark.Sim;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Reporting;
using Bikepark.Sim.State;
using Bikepark.Sim.Systems;
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
    public required ClearingTool Clearing { get; init; }
    public required RiderView Riders { get; init; }
    public required TerrainView Terrain { get; init; }
    public required RtsCamera Camera { get; init; }

    /// <summary>Renaturalize / split trails (created by the HUD).</summary>
    public TrailEditTool TrailEdit { get; set; } = null!;

    /// <summary>Closed days seen this session (from <see cref="DayEnded"/>), for the finance chart.</summary>
    public List<DayReport> Days { get; } = [];

    public Simulation Sim => Host.Sim;

    /// <summary>Opens the repair pop-up for a trail (set by the HUD).</summary>
    public Action<int> OpenRepair { get; set; } = _ => { };

    public KpiReport Kpi { get; set; } = null!;

    public const int FeeStepCents = 250;

    /// <summary>"≈ 14 crew-h" / "≈ 45 crew-min" for crew-minutes of work.</summary>
    public static string CrewTime(long minutes) => minutes >= 90 ? $"≈ {(minutes + 30) / 60} crew-h" : $"≈ {minutes} crew-min";

    /// <summary>The crew part of a build preview: trees to fell first, wood, work.</summary>
    public string EstimateText(WorkEstimate e)
    {
        var parts = new List<string>();
        if (e.Trees > 0) parts.Add($"{e.Trees} trees to fell first (+{e.WoodGained} wood)");
        parts.Add(CrewTime(e.TotalMinutes));
        if (e.WoodNeeded > 0)
            parts.Add($"{e.WoodNeeded} wood (have {Sim.State.WoodStock})");
        return (Host.InstantBuild ? "Instant build (debug) · would take " : "Crew: ") + string.Join(" · ", parts);
    }

    public void ChangeFee(long delta) => Host.Enqueue(new SetEntryFeeCommand(Math.Max(0, Sim.State.Park.EntryFeeCents + delta)));
    public void ChangeLiftTicket(long delta) => Host.Enqueue(new SetLiftTicketCommand(Math.Max(0, Sim.State.Park.LiftTicketCents + delta)));

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

    private Label? _hint;

    public ToolCard() { _normal = _hover = _active = new StyleBoxFlat(); }

    /// <summary>Changes the small line under the name (e.g. a price or why it's locked).</summary>
    public void SetHint(string hint)
    {
        if (_hint is not null && _hint.Text != hint) _hint.Text = hint;
    }

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
        var hintLabel = _hint = UiTheme.Label(hint, 11, UiTheme.TextDim);
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
    private ToolCard _path = null!, _trail = null!, _parking = null!, _fell = null!, _platform = null!, _renaturalize = null!, _split = null!;
    private readonly List<(LiftType Type, ToolCard Card)> _lifts = [];
    private readonly List<(string TypeId, ToolCard Card)> _features = [];
    private Label _demoStatus = null!;

    public override string Title => "Build";

    protected override void Build()
    {
        var cards = Row(10);
        Body.AddChild(cards);
        _path = new ToolCard(UiIcon.Path, "Gravel path", "P", "Two-way access path riders can pedal up (max gradient ±4.0); the crew builds it",
            () => ToggleWay(WayTool.ToolMode.AccessPath));
        _trail = new ToolCard(UiIcon.Trail, "Trail", "T", "One-way downhill trail; start it on a plateau or path. The crew fells the trees in the way, then digs it",
            () => ToggleWay(WayTool.ToolMode.Trail));
        _parking = new ToolCard(UiIcon.Parking, "Parking lot", "K", "Parking next to a valley station (free); on your land",
            () => ToggleStructure(StructureTool.ToolMode.Parking));
        _fell = new ToolCard(UiIcon.Felling, "Fell trees", "area", "Mark an area of forest: the crew cuts the trees, each gives wood",
            () => Ctx.Clearing.SetActive(!Ctx.Clearing.Active));
        var demo = new ToolCard(UiIcon.Demo, "Demo trails", "", "Builds two example trails from the plateau (at once)", Ctx.Host.LoadDemoNetwork);
        var demoFeatures = new ToolCard(UiIcon.Demo, "Demo features", "", "Plans berms, jumps and wood features on the demo trails",
            () =>
            {
                _demoStatus.Text = Ctx.Host.LoadDemoFeatures() ? "" : "Build the demo trails first.";
                _demoStatus.Visible = _demoStatus.Text.Length > 0;
            });
        _platform = new ToolCard(UiIcon.Parking, "Gravel platform", "12 × 12 m", "A small gravel pad where paths and trails can start and end, to link them up (free); on your land",
            () => ToggleStructure(StructureTool.ToolMode.Platform));
        _renaturalize = new ToolCard(UiIcon.Felling, "Renaturalize", "remove", "Give a section of a trail back to nature: click where it starts and where it ends. " +
                                                                             "What is left keeps loose ends and stays closed until you connect it again (draw a trail from the loose end)",
            () => ToggleTrailEdit(TrailEditTool.ToolMode.Renaturalize));
        _split = new ToolCard(UiIcon.Trail, "Split trail", "cut", "Cut a trail into two trails at a point (each keeps its features)",
            () => ToggleTrailEdit(TrailEditTool.ToolMode.Split));
        foreach (var card in new[] { _path, _trail, _fell, _parking, _platform, _renaturalize, _split }) cards.AddChild(card);
        if (Ctx.Sim.State.Parcels.Count == 0)
        {
            // Sandbox only: the demo content.
            cards.AddChild(demo);
            cards.AddChild(demoFeatures);
        }
        _demoStatus = UiTheme.Label("", 12, UiTheme.Warn);
        _demoStatus.Visible = false;
        Body.AddChild(_demoStatus);
        Body.AddChild(UiTheme.Label("Click or drag to place points · Backspace undo · Enter plans it for the crew · Esc cancel", 12, UiTheme.TextDim));

        Body.AddChild(UiTheme.Separator());
        Body.AddChild(UiTheme.Label("LIFTS — a contractor builds them: paid at once, running after a few days, then a daily upkeep", 13, UiTheme.Accent, bold: true));
        var lifts = Row(8);
        Body.AddChild(lifts);
        foreach (var type in Ctx.Sim.State.LiftTypes)
        {
            string id = type.Id;
            var card = new ToolCard(UiIcon.Lift, type.Name, "", LiftTooltip(type), () => ToggleLift(id)) { CustomMinimumSize = new Vector2(150, 104) };
            lifts.AddChild(card);
            _lifts.Add((type, card));
        }
        Body.AddChild(UiTheme.Label("L: click the valley station, then the top · Enter orders it · both stations on your land", 12, UiTheme.TextDim));

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
                string cost = type.Wood > 0 ? $" · {type.Wood} wood" : "";
                var card = new ToolCard(FeatureIcon(type.Kind), type.Name, $"{type.LengthMeters} m · {DifficultyWord(type.Difficulty)}{cost}",
                    FeatureTooltip(type), () => ToggleFeature(id)) { CustomMinimumSize = new Vector2(100, 104) };
                row.AddChild(card);
                _features.Add((id, card));
            }
            if (row.GetChildCount() > 0) groups.AddChild(group);
        }
        Body.AddChild(UiTheme.Label("Point at a trail and click to plan it · Delete removes the feature under the cursor · Esc stops", 12, UiTheme.TextDim));
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
        string build = type.Wood > 0 ? $"{HudContext.CrewTime(type.WorkMinutes)} and {type.Wood} wood" : HudContext.CrewTime(type.WorkMinutes);
        return $"{type.Name}: {type.LengthMeters} m, difficulty {type.Difficulty} ({DifficultyWord(type.Difficulty)}); {where}{bend}. " +
               $"Flow riders {type.FlowAffinity / 10}%, technical riders {type.TechAffinity / 10}%. Building it takes {build}.";
    }

    private void ToggleTrailEdit(TrailEditTool.ToolMode mode)
    {
        Ctx.Ways.SetMode(WayTool.ToolMode.None);
        Ctx.Structures.SetMode(StructureTool.ToolMode.None);
        Ctx.Features.SetType(null);
        Ctx.Clearing.SetActive(false);
        Ctx.TrailEdit.SetMode(Ctx.TrailEdit.Mode == mode ? TrailEditTool.ToolMode.None : mode);
    }

    private void ToggleWay(WayTool.ToolMode mode)
    {
        Ctx.TrailEdit.SetMode(TrailEditTool.ToolMode.None);
        Ctx.Structures.SetMode(StructureTool.ToolMode.None);
        Ctx.Features.SetType(null);
        Ctx.Clearing.SetActive(false);
        Ctx.Ways.SetMode(Ctx.Ways.Mode == mode ? WayTool.ToolMode.None : mode);
    }

    private void ToggleStructure(StructureTool.ToolMode mode)
    {
        Ctx.TrailEdit.SetMode(TrailEditTool.ToolMode.None);
        Ctx.Features.SetType(null);
        Ctx.Clearing.SetActive(false);
        Ctx.Structures.SetMode(Ctx.Structures.Mode == mode ? StructureTool.ToolMode.None : mode);
    }

    private void ToggleLift(string typeId)
    {
        bool same = Ctx.Structures.Mode == StructureTool.ToolMode.Lift && Ctx.Structures.LiftTypeId == typeId;
        Ctx.Structures.LiftTypeId = typeId;
        Ctx.Features.SetType(null);
        Ctx.Clearing.SetActive(false);
        Ctx.Ways.SetMode(WayTool.ToolMode.None);
        Ctx.Structures.SetMode(same ? StructureTool.ToolMode.None : StructureTool.ToolMode.Lift);
    }

    private static string LiftTooltip(LiftType type) =>
        $"{type.Name}: {type.BikesPerCarrier} bike{(type.BikesPerCarrier == 1 ? "" : "s")} every {type.IntervalSeconds} s " +
        $"({LiftMath.BikeRidersPerHour(type, 1000)} riders/h), {type.SpeedCmPerS / 100.0:0.#} m/s, {type.MinLengthMeters}–{type.MaxLengthMeters} m" +
        $"{(type.Sheltered ? ", closed cabins" : ", open (riders get wet in the rain)")}. Build {UiTheme.Money(type.BuildCostCents)} in {type.BuildDays} days, " +
        $"upkeep {UiTheme.Money(type.UpkeepPerDayCents)}/day, from park level {type.RequiredLevel}.";

    private void ToggleFeature(string typeId) => Ctx.Features.SetType(Ctx.Features.TypeId == typeId ? null : typeId);

    public override void Refresh()
    {
        _path.Active = Ctx.Ways.Mode == WayTool.ToolMode.AccessPath;
        _trail.Active = Ctx.Ways.Mode == WayTool.ToolMode.Trail;
        int level = Ctx.Kpi.Level;
        foreach (var (type, card) in _lifts)
        {
            card.Active = Ctx.Structures.Mode == StructureTool.ToolMode.Lift && Ctx.Structures.LiftTypeId == type.Id;
            string? locked = Ctx.Host.InstantBuild ? null : LiftWorks.CannotBuild(Ctx.Sim.State, level, type);
            card.SetHint(locked is null ? $"{UiTheme.Money(type.BuildCostCents)} · {LiftMath.BikeRidersPerHour(type, 1000)}/h"
                : level < type.RequiredLevel ? $"locked: level {type.RequiredLevel}" : $"{UiTheme.Money(type.BuildCostCents)} (can't afford)");
            card.Modulate = locked is null ? Colors.White : new Color(1, 1, 1, 0.55f);
        }
        _parking.Active = Ctx.Structures.Mode == StructureTool.ToolMode.Parking;
        _platform.Active = Ctx.Structures.Mode == StructureTool.ToolMode.Platform;
        _renaturalize.Active = Ctx.TrailEdit.Mode == TrailEditTool.ToolMode.Renaturalize;
        _split.Active = Ctx.TrailEdit.Mode == TrailEditTool.ToolMode.Split;
        _fell.Active = Ctx.Clearing.Active;
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
        var state = Ctx.Sim.State;
        foreach (var (id, main, detail) in _rows)
        {
            var way = network.FindWay(id);
            if (way is null || !network.TryGetGeometry(id, out var g)) continue;
            if (!way.Built)
            {
                var job = Jobs.ForWay(state, id);
                string progress = job is null ? "" : job.IsFelling
                    ? $" · felling {job.TreesFelled}/{job.Trees.Count} trees"
                    : $" · {WorkCosts.ProgressPermille(state.CrewRules, job) / 10} % built";
                main.Text = $"{way.Name}  (planned)";
                detail.Text = $"{(way.Kind == WayKind.Trail ? $"Trail · {g.Rating}" : "Gravel path")} · {g.LengthCm / 100} m{progress}\n" +
                              "The crew builds it (Crew menu); riders can't use it yet" +
                              (way.Kind == WayKind.Trail ? "\n" + FeatureSummary(network.FeaturesOn(id)) : "");
                continue;
            }
            if (way.Kind == WayKind.AccessPath)
            {
                main.Text = $"{way.Name}";
                detail.Text = $"Gravel path · {g.LengthCm / 100} m · +{(g.EndHeightCm - g.StartHeightCm) / 100} m" +
                              (way.Origin == WayOrigin.Scenario ? " · existing hiking route" : "");
                continue;
            }
            var s = way.Stats;
            string avg = s.Runs == 0 ? "no runs yet" : $"{(double)s.SumRunMinutes / s.Runs:F1} min · fun {s.SumFun / s.Runs / 10}%";
            bool connected = network.IsConnected(way);
            main.Text = !connected ? $"{way.Name}  · NOT CONNECTED (closed)"
                : way.WornOut ? $"{way.Name}  · CLOSED (worn out)" : way.Repairing ? $"{way.Name}  · CLOSED (crew at work)" : way.Closed ? $"{way.Name}  · CLOSED" : way.Name;
            main.AddThemeColorOverride("font_color", network.IsRideable(way) ? UiTheme.Text : UiTheme.Bad);
            if (!connected)
                main.TooltipText = "A loose end: riders can't reach its start or can't get away from its end. Draw a trail from its loose end (or onto its loose start) to connect it.";
            detail.Text = $"{WayMeshes.RatingText(g)} · {g.LengthCm / 100} m · -{(g.StartHeightCm - g.EndHeightCm) / 100} m · steepest {Gradient.Format(-g.MaxDropGradient)}\n" +
                          $"{s.Runs} runs ({s.RunsToday} today) · {avg}\n" +
                          (state.CrashRules.Enabled ? CrashSummary(state, way, network) + "\n" : "") +
                          ConditionSummary(state, way, g) + "\n" +
                          FeatureSummary(network.FeaturesOn(id));
        }
    }

    /// <summary>The worst feature and what happens next: "Worst feature: Berm 40 m 63 %", closed, or repairing.</summary>
    private static string ConditionSummary(WorldState state, Way way, WayGeometry g)
    {
        var built = way.Features.Where(f => f.Built).ToList();
        if (built.Count == 0) return "Nothing to wear: only features wear";
        var worst = built.OrderBy(f => f.Condition).First();
        var type = TrailFeatures.FindType(state.TrailFeatureTypes, worst.TypeId);
        string next = way.WornOut ? "worn out: closed until repaired"
            : way.Repairing ? "closed while the crew works on a feature"
            : TrailCondition.Permille(worst) < state.WearRules.WarnBelowPermille ? "needs a repair!"
            : TrailCondition.Permille(worst) < 1000 ? "worn" : "like new";
        return $"Worst feature: {type?.Name} at {worst.DistanceCm / 100} m, {TrailCondition.Permille(worst) / 10} % · {next}";
    }

    /// <summary>"3 crashes (1 serious) · 2.4 per 1000 runs · most at the Double" and riders lying on it now.</summary>
    private static string CrashSummary(WorldState state, Way way, WayNetwork network)
    {
        var s = way.Stats;
        int down = state.Guests.Count(g => g.Activity == RiderActivity.Injured && g.CrashWayId == way.Id);
        string now = down > 0 ? $" · {down} injured on it now, helicopter coming" : "";
        if (s.Crashes == 0) return "No crashes yet" + now;
        string rate = s.Runs == 0 ? "" : $" · {s.Crashes * 1000.0 / s.Runs:0.0} per 1000 runs";
        var worst = network.FeaturesOn(way.Id).Where(f => f.Feature.Crashes > 0).OrderByDescending(f => f.Feature.Crashes).FirstOrDefault();
        string where = worst.Type is null ? "" : $" · most at the {worst.Type.Name} ({worst.Feature.Crashes})";
        return $"{s.Crashes} crash{(s.Crashes == 1 ? "" : "es")} ({s.SeriousCrashes} serious, {s.Collisions} collisions){rate}{where}{now}";
    }

    /// <summary>"2 berms · Tabletop · Drop" (in the order types first appear along the trail).</summary>
    private static string FeatureSummary(IReadOnlyList<PlacedFeature> features)
    {
        if (features.Count == 0) return "No features yet (Build → trail features)";
        var parts = features.GroupBy(f => f.Type.Id).Select(g => g.Count() == 1 ? g.First().Type.Name : $"{g.Count()} × {g.First().Type.Name}");
        int planned = features.Count(f => !f.Feature.Built);
        return $"Features: {string.Join(" · ", parts)}{(planned > 0 ? $"  ({planned} planned)" : "")}";
    }

    /// <summary>Ways, their features and closures; the list is rebuilt when it changes (wear in 10 % steps).</summary>
    private string Signature(List<Way> ways) =>
        string.Join(';', ways.Select(w => $"{w.Id}{(w.Built ? "" : "p")}{(w.Closed ? "c" : "")}{(w.WornOut ? "w" : "")}{(w.Repairing ? "r" : "")}:" +
                                          string.Join(',', w.Features.Select(f => f.Built ? $"{f.Id}" : $"{f.Id}p"))));

    /// <summary>Repair… (opens the pop-up) and close/open.</summary>
    private Control TrailCareButtons(Way way)
    {
        int id = way.Id;
        var box = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        box.AddThemeConstantOverride("separation", 3);
        Button Small(Button b) { b.AddThemeFontSizeOverride("font_size", 12); return b; }

        box.AddChild(Small(UiTheme.Button("Features…", () => Ctx.OpenRepair(id), "Feature condition and repairs (or click the trail)")));

        box.AddChild(Small(UiTheme.Button(way.Closed ? "Open" : "Close", () => Ctx.Host.Enqueue(new SetTrailClosedCommand(id, !way.Closed)),
            way.Closed ? "Let riders on it again (a worn-out trail stays closed until repaired)" : "Close it to riders (those on it finish their run)")));
        return box;
    }

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
            if (way.Kind == WayKind.Trail && way.Built)
            {
                // Click a trail for its feature overview and repairs.
                int clicked = way.Id;
                row.MouseDefaultCursorShape = CursorShape.PointingHand;
                row.TooltipText = "Click for feature condition and repairs";
                row.GuiInput += e =>
                {
                    if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Ctx.OpenRepair(clicked);
                };
            }
            var box = UiTheme.Box(UiTheme.Card, 8, 10, 6);
            box.ShadowSize = 0;
            row.AddThemeStyleboxOverride("panel", box);
            var h = Row(10);
            row.AddChild(h);
            var color = !way.Built ? WayMeshes.Blueprint
                : way.Kind == WayKind.AccessPath ? new Color(0.8f, 0.8f, 0.8f)
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
                    var chip = UiTheme.Button($"{feature.Type.Name} {feature.StartCm / 100} m{(feature.Feature.Built ? "" : " (planned)")}  ✕",
                        () => Ctx.Host.Enqueue(new RemoveTrailFeatureCommand(id, featureId)),
                        feature.Feature.Built ? "Remove this feature" : "Cancel this planned feature (its wood goes back to the stock)");
                    chip.AddThemeFontSizeOverride("font_size", 11);
                    chips.AddChild(chip);
                }
                texts.AddChild(chips);
            }
            if (way.Kind == WayKind.Trail && way.Built)
                h.AddChild(TrailCareButtons(way));
            if (way.Origin == WayOrigin.Player)
            {
                var delete = UiTheme.Button(way.Built ? "Delete" : "Cancel", () => Ctx.Host.Enqueue(new DeleteWayCommand(id)),
                    way.Built ? "Remove this way (not while others attach to it)" : "Cancel this planned way and its job");
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

// ==================================================================== Crew

/// <summary>Workers, tools, wood and the job queue (priority order: free workers take the first job they can work on).</summary>
public partial class CrewPanel : HudPanel
{
    private Label _workers = null!, _wages = null!, _wood = null!, _jobs = null!, _ahead = null!, _hours = null!;
    private HBoxContainer _tools = null!;
    private VBoxContainer _list = null!;
    private Button _buy10 = null!, _buy50 = null!;
    private string _toolSignature = "", _jobSignature = "";
    private readonly List<(int JobId, Label Title, Label State, ProgressBar Bar)> _rows = [];
    private const int MaxListHeight = 300;

    public override string Title => "Crew & jobs";

    protected override void Build()
    {
        var tiles = Row(24);
        tiles.AddChild(UiTheme.StatTile("Workers", out _workers, 24));
        tiles.AddChild(UiTheme.StatTile("Wages per day", out _wages));
        tiles.AddChild(UiTheme.StatTile("Wood", out _wood, 24));
        tiles.AddChild(UiTheme.StatTile("Jobs", out _jobs));
        tiles.AddChild(UiTheme.StatTile("Work ahead", out _ahead));
        Body.AddChild(tiles);

        var actions = Row(8);
        actions.AddChild(UiTheme.Button("Hire a worker", () => Ctx.Host.Enqueue(new HireCrewCommand()), "Paid every evening; works during work hours"));
        actions.AddChild(UiTheme.Button("Dismiss one", () =>
        {
            if (Ctx.Sim.State.Crew.LastOrDefault() is { } last) Ctx.Host.Enqueue(new DismissCrewCommand(last.Id));
        }, "Let the last hired worker go"));
        _hours = UiTheme.Label("", 12, UiTheme.TextDim);
        _hours.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        actions.AddChild(_hours);
        Body.AddChild(actions);
        Body.AddChild(UiTheme.Separator());

        Body.AddChild(UiTheme.Label("TOOLS — bought once, speed up one kind of work for the whole crew", 11, UiTheme.TextDim, bold: true));
        _tools = Row(8);
        Body.AddChild(_tools);

        var wood = Row(8);
        wood.AddChild(UiTheme.Label("WOOD", 11, UiTheme.TextDim, bold: true));
        _buy10 = UiTheme.Button("", () => Ctx.Host.Enqueue(new BuyWoodCommand(10)), "Buying is expensive; felling trees is cheaper");
        _buy50 = UiTheme.Button("", () => Ctx.Host.Enqueue(new BuyWoodCommand(50)), "Buying is expensive; felling trees is cheaper");
        wood.AddChild(_buy10);
        wood.AddChild(_buy50);
        wood.AddChild(UiTheme.Button("Fell trees…", () => Ctx.Clearing.SetActive(true), "Mark an area of forest for the crew to cut (each tree gives wood)"));
        Body.AddChild(wood);
        Body.AddChild(UiTheme.Separator());

        Body.AddChild(UiTheme.Label("JOBS — in priority order", 11, UiTheme.TextDim, bold: true));
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(600, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_list);
        Body.AddChild(scroll);
    }

    public override void Refresh()
    {
        var state = Ctx.Sim.State;
        var rules = state.CrewRules;
        _workers.Text = $"{state.Crew.Count} / {rules.MaxCrew}";
        _wages.Text = UiTheme.Money(state.Crew.Count * rules.WagePerDayCents);
        _wood.Text = $"{state.WoodStock}";
        _jobs.Text = $"{state.Jobs.Count}";
        long remaining = state.Jobs.Sum(j => WorkCosts.RemainingMinutes(rules, j));
        int perDay = state.Crew.Count * (rules.WorkEndMinute - rules.WorkStartMinute);
        _ahead.Text = remaining == 0 ? "–" : perDay == 0 ? HudContext.CrewTime(remaining) : $"{HudContext.CrewTime(remaining)} (~{(remaining + perDay - 1) / perDay} d)";
        _hours.Text = $"Work hours {rules.WorkStartMinute / 60:00}:00–{rules.WorkEndMinute / 60:00}:00 · {UiTheme.Money(rules.WagePerDayCents)} per worker and day";
        _buy10.Text = $"Buy 10 ({UiTheme.Money(10 * rules.WoodPriceCents)})";
        _buy50.Text = $"Buy 50 ({UiTheme.Money(50 * rules.WoodPriceCents)})";

        string toolSignature = string.Join(',', state.OwnedToolIds);
        if (toolSignature != _toolSignature || _tools.GetChildCount() == 0) RebuildTools();

        string jobSignature = string.Join(',', state.Jobs.Select(j => j.Id));
        if (jobSignature != _jobSignature) RebuildJobs();
        foreach (var (jobId, title, stateLabel, bar) in _rows)
        {
            if (Jobs.Find(state, jobId) is not { } job) continue;
            title.Text = Jobs.Title(state, job);
            stateLabel.Text = JobState(state, job);
            bar.Value = WorkCosts.ProgressPermille(rules, job);
        }
        var scroll = (ScrollContainer)_list.GetParent();
        scroll.CustomMinimumSize = new Vector2(600, Math.Min(MaxListHeight, Math.Max(30, _list.GetCombinedMinimumSize().Y)));
    }

    private static string JobState(WorldState state, Job job)
    {
        int workers = state.Crew.Count(m => m.JobId == job.Id);
        var rules = state.CrewRules;
        string who = workers == 0 ? "" : $" · {workers} worker{(workers == 1 ? "" : "s")}";
        if (!JobSystem.IsWorkable(state, job))
        {
            if (job.Kind == JobKind.BuildFeature && state.Ways.FirstOrDefault(w => w.Id == job.WayId)?.Built != true)
                return "Waiting for the trail to be built";
            if (job.Wood > 0 && !job.WoodTaken)
                return $"Waiting for wood: needs {job.Wood}, {state.WoodStock} in stock (fell trees or buy)";
        }
        if (workers > 0 && Jobs.IsWaitingForRiders(state, job))
            return $"Trail closed · waiting for the last riders to leave{who}";
        string left = HudContext.CrewTime(WorkCosts.RemainingMinutes(rules, job)) + " left";
        if (job.IsFelling)
            return $"Felling {job.TreesFelled}/{job.Trees.Count} trees · {left}{who}";
        string verb = job.CurrentWorkType == WorkType.Carpentry ? "Carpentry" : "Digging";
        return workers == 0 ? $"Queued · {left}" : $"{verb} {WorkCosts.ProgressPermille(rules, job) / 10} % · {left}{who}";
    }

    private void RebuildTools()
    {
        var state = Ctx.Sim.State;
        _toolSignature = string.Join(',', state.OwnedToolIds);
        foreach (var child in _tools.GetChildren()) child.QueueFree();
        if (state.ToolTypes.Count == 0)
        {
            _tools.AddChild(UiTheme.Label("No tools for sale in this scenario.", 12, UiTheme.TextDim));
            return;
        }
        foreach (var tool in state.ToolTypes)
        {
            string id = tool.Id;
            bool owned = state.OwnedToolIds.Contains(id);
            var button = new Button
            {
                Text = $"{tool.Name}{(owned ? "  ✓" : "")}\n{WorkWord(tool.WorkType)} +{tool.SpeedBonusPermille / 10} % · {(owned ? "owned" : UiTheme.Money(tool.PriceCents))}",
                Disabled = owned,
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(140, 46),
                TooltipText = owned ? "You own this tool" : $"Buy for {UiTheme.MoneyExact(tool.PriceCents)}",
            };
            button.AddThemeFontSizeOverride("font_size", 12);
            button.Pressed += () => Ctx.Host.Enqueue(new BuyToolCommand(id));
            _tools.AddChild(button);
        }
    }

    private static string WorkWord(WorkType type) => type switch
    {
        WorkType.Digging => "Digging",
        WorkType.Carpentry => "Carpentry",
        _ => "Felling",
    };

    private void RebuildJobs()
    {
        var state = Ctx.Sim.State;
        _jobSignature = string.Join(',', state.Jobs.Select(j => j.Id));
        foreach (var child in _list.GetChildren()) child.QueueFree();
        _rows.Clear();
        if (state.Jobs.Count == 0)
        {
            _list.AddChild(UiTheme.Label("No jobs. Plan a trail, a feature or a felling area in Build.", 13, UiTheme.TextDim));
            return;
        }
        for (int i = 0; i < state.Jobs.Count; i++)
        {
            var job = state.Jobs[i];
            int jobId = job.Id;
            var row = new PanelContainer();
            var box = UiTheme.Box(UiTheme.Card, 8, 10, 6);
            box.ShadowSize = 0;
            row.AddThemeStyleboxOverride("panel", box);
            var h = Row(10);
            row.AddChild(h);
            var icon = job.Kind switch
            {
                JobKind.FellTrees => UiIcon.Felling,
                JobKind.BuildWay => state.Ways.FirstOrDefault(w => w.Id == job.WayId)?.Kind == WayKind.AccessPath ? UiIcon.Path : UiIcon.Trail,
                JobKind.RepairFeature => UiIcon.Tool,
                _ => FeatureIconFor(state, job),
            };
            h.AddChild(new IconView(icon, 30, UiTheme.Accent) { SizeFlagsVertical = SizeFlags.ShrinkCenter });
            var texts = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            texts.AddThemeConstantOverride("separation", 1);
            var title = UiTheme.Label("", 14, bold: true);
            var stateLabel = UiTheme.Label("", 12, UiTheme.TextDim);
            var bar = new ProgressBar { MinValue = 0, MaxValue = 1000, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 6) };
            texts.AddChild(title);
            texts.AddChild(stateLabel);
            texts.AddChild(bar);
            h.AddChild(texts);
            if (i > 0)
                h.AddChild(new RoundButton(UiIcon.Up, 26, "", "Move to the top of the queue", () => Ctx.Host.Enqueue(new PrioritizeJobCommand(jobId))) { SizeFlagsVertical = SizeFlags.ShrinkCenter });
            h.AddChild(new RoundButton(UiIcon.Close, 26, "", job.Kind switch
                {
                    JobKind.FellTrees => "Cancel (trees cut so far stay cut)",
                    JobKind.RepairFeature => "Cancel the repair (the trail opens again)",
                    _ => "Cancel: removes the planned way or feature",
                }, () => Ctx.Host.Enqueue(new CancelJobCommand(jobId))) { SizeFlagsVertical = SizeFlags.ShrinkCenter });
            _list.AddChild(row);
            _rows.Add((jobId, title, stateLabel, bar));
        }
    }

    private static UiIcon FeatureIconFor(WorldState state, Job job)
    {
        var feature = state.Ways.FirstOrDefault(w => w.Id == job.WayId)?.Features.FirstOrDefault(f => f.Id == job.FeatureId);
        var type = feature is null ? null : TrailFeatures.FindType(state.TrailFeatureTypes, feature.TypeId);
        return type is null ? UiIcon.Build : BuildPanel.FeatureIcon(type.Kind);
    }
}

// ==================================================================== Riders

public partial class RidersPanel : HudPanel
{
    private Label _inPark = null!, _riding = null!, _queuing = null!, _onLift = null!, _walking = null!, _eating = null!;
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
                     ("Lunch break", l => _eating = l),
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
        _eating.Text = $"{k.GuestsEating}";
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
        string signature = string.Join(',', state.Lifts.Select(l =>
            $"{l.Id}:{l.BikeAccess?.TierIndex}:{l.BikeAccess?.PendingTierIndex}:{l.Derelict}:{l.ReadyTick}:{LiftWorks.StationsOwned(state, l)}"));
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
            names.AddChild(UiTheme.Label($"{type.Name} · run by {op?.Name ?? "the park"}" +
                                         (op is null ? $" · upkeep {UiTheme.Money(type.UpkeepPerDayCents)}/day" : ""), 12, UiTheme.TextDim));
            header.AddChild(names);
            _list.AddChild(header);

            // Out of service: rusty (restore it) or with the contractor.
            if (lift.ReadyTick > 0)
            {
                long ready = lift.ReadyTick;
                _list.AddChild(UiTheme.Label(
                    $"{(lift.Derelict ? "Being restored" : "Under construction")}: running from day {GameTime.Day(ready) + 1}, " +
                    $"{GameTime.Format(ready)[^5..]}", 13, UiTheme.Warn, bold: true));
            }
            else if (lift.Derelict)
            {
                var rusty = Row(10);
                rusty.AddChild(UiTheme.Label("Rusty and out of service. Riders pedal up instead.", 13, UiTheme.Warn, bold: true));
                string? why = !LiftWorks.StationsOwned(state, lift) ? "It stands on land you don't own."
                    : state.Finance.MoneyCents < type.RestoreCostCents ? "Not enough money." : null;
                var restore = UiTheme.Button($"Restore ({UiTheme.Money(type.RestoreCostCents)}, {type.RestoreDays} days)",
                    () => Ctx.Host.Enqueue(new RestoreLiftCommand(liftId)),
                    why ?? $"A contractor fixes it for half the price of a new {type.Name}; it runs from the next opening after the work");
                restore.Disabled = why is not null;
                rusty.AddChild(restore);
                _list.AddChild(rusty);
                continue;
            }

            var stoppedLabel = UiTheme.Label("", 13, UiTheme.Bad, bold: true);
            stoppedLabel.Visible = false;
            _list.AddChild(stoppedLabel);
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
                long now = Ctx.Sim.State.Tick;
                stoppedLabel.Visible = l.IsStopped(now);
                if (stoppedLabel.Visible)
                    stoppedLabel.Text = $"Stopped: a rider crashed on the track. Running again at {GameTime.Format(l.StoppedUntilTick)[^5..]} " +
                                        $"({l.StoppedUntilTick - now} min).";
                int minutes = LiftMath.ExpectedWaitMinutes(type, l.BikeCarrierPermille, l.Queue.Count);
                queue.Text = $"{l.Queue.Count}";
                bool stopped = l.IsStopped(now);
                wait.Text = stopped ? "stopped" : minutes < 0 ? "no bikes" : minutes == 0 ? "none" : $"~{minutes} min";
                wait.AddThemeColorOverride("font_color", stopped || minutes < 0 || minutes > 20 ? UiTheme.Bad : minutes > 8 ? UiTheme.Warn : UiTheme.Good);
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
                bool owned = LiftWorks.StationsOwned(state, lift);
                if (!owned)
                    _list.AddChild(UiTheme.Label($"{op.Name} only rents you bike access once you own the land at both stations (Land menu).", 12, UiTheme.Warn));
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
                    button.Disabled = !owned && i > 0;
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
    private Label _totalRevenue = null!, _totalExpenses = null!, _liftFees = null!, _wages = null!, _materials = null!, _food = null!, _fee = null!, _liftTicket = null!, _tickets = null!;
    private Label _insurance = null!, _landAndLifts = null!, _liftUpkeep = null!;
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
        totals.AddChild(UiTheme.StatTile("Lunch sales", out _food, 16));
        totals.AddChild(UiTheme.StatTile("Lift tickets sold", out _tickets, 16));
        totals.AddChild(UiTheme.StatTile("Lift fees paid", out _liftFees, 16));
        totals.AddChild(UiTheme.StatTile("Crew wages", out _wages, 16));
        totals.AddChild(UiTheme.StatTile("Tools & wood", out _materials, 16));
        totals.AddChild(UiTheme.StatTile("Insurance", out _insurance, 16));
        Body.AddChild(totals);
        totals = Row(24);
        totals.AddChild(UiTheme.StatTile("Land bought", out _landAndLifts, 16));
        totals.AddChild(UiTheme.StatTile("Own lifts: upkeep", out _liftUpkeep, 16));
        Body.AddChild(totals);
        Body.AddChild(UiTheme.Separator());

        var fee = Row(8);
        fee.AddChild(UiTheme.Label("ENTRANCE", 11, UiTheme.TextDim, bold: true));
        fee.AddChild(UiTheme.Button("−", () => Ctx.ChangeFee(-HudContext.FeeStepCents), "Lower the entrance fee"));
        _fee = UiTheme.Label("", 18, bold: true);
        _fee.CustomMinimumSize = new Vector2(80, 0);
        _fee.HorizontalAlignment = HorizontalAlignment.Center;
        fee.AddChild(_fee);
        fee.AddChild(UiTheme.Button("+", () => Ctx.ChangeFee(HudContext.FeeStepCents), "Raise the entrance fee (fewer guests come)"));
        Body.AddChild(fee);

        var ticket = Row(8);
        ticket.AddChild(UiTheme.Label("LIFT PASS", 11, UiTheme.TextDim, bold: true));
        ticket.AddChild(UiTheme.Button("−", () => Ctx.ChangeLiftTicket(-HudContext.FeeStepCents), "Lower the lift day pass"));
        _liftTicket = UiTheme.Label("", 18, bold: true);
        _liftTicket.CustomMinimumSize = new Vector2(80, 0);
        _liftTicket.HorizontalAlignment = HorizontalAlignment.Center;
        ticket.AddChild(_liftTicket);
        ticket.AddChild(UiTheme.Button("+", () => Ctx.ChangeLiftTicket(HudContext.FeeStepCents),
            "Raise the lift day pass (paid once per visit by guests who use a lift; too dear and they pedal instead)"));
        Body.AddChild(ticket);

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
        _food.Text = UiTheme.Money(f.TotalFoodCents);
        _wages.Text = UiTheme.Money(f.TotalWagesCents);
        _materials.Text = UiTheme.Money(f.TotalToolsCents + f.TotalWoodCents);
        _landAndLifts.Text = UiTheme.Money(f.TotalLandCents);
        _landAndLifts.TooltipText = $"Land {UiTheme.Money(f.TotalLandCents)} · lifts built or restored {UiTheme.Money(f.TotalLiftBuildCents)}";
        _liftUpkeep.Text = UiTheme.Money(f.TotalLiftUpkeepCents);
        _liftUpkeep.TooltipText = $"Built and restored lifts: {UiTheme.Money(f.TotalLiftBuildCents)}";
        var safety = Ctx.Sim.State.Safety;
        _insurance.Text = UiTheme.Money(safety.TotalInsuranceCents);
        _insurance.TooltipText = Ctx.Sim.State.CrashRules.Enabled
            ? $"Accident insurance, charged daily: {UiTheme.Money(Bikepark.Sim.Safety.CrashMath.Premium(Ctx.Sim.State.CrashRules, safety))} at the moment " +
              $"(a base plus every accident of the last {Ctx.Sim.State.CrashRules.InsuranceDays} days)"
            : "No accident insurance in this scenario";
        _fee.Text = UiTheme.MoneyExact(Ctx.Sim.State.Park.EntryFeeCents);
        _liftTicket.Text = UiTheme.MoneyExact(Ctx.Sim.State.Park.LiftTicketCents);
        _tickets.Text = UiTheme.Money(f.TotalLiftTicketsCents);
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
    private Button _menu = null!;
    private Label _saveNote = null!;

    public override string Title => "Game";

    protected override void Build()
    {
        var time = Row(8);
        time.AddChild(UiTheme.Button("Skip to opening", Ctx.Host.SkipToOpeningHours, "Fast-forward to the next opening time"));
        time.AddChild(UiTheme.Button("Turbo till closing", Ctx.Host.TurboToClosingHours, "Run fast until the park closes"));
        Body.AddChild(time);
        var files = Row(8);
        _menu = UiTheme.Button("Back to menu", Ctx.Host.BackToMenu, "To the start screen (your career is saved first)");
        files.AddChild(_menu);
        _saveNote = UiTheme.Label("", 12, UiTheme.TextDim);
        _saveNote.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        files.AddChild(_saveNote);
        Body.AddChild(files);
        var instant = new CheckButton
        {
            Text = "Instant build (debug): ways and features are built at once, no crew job",
            ButtonPressed = Ctx.Host.InstantBuild,
            FocusMode = FocusModeEnum.None,
        };
        instant.Toggled += on => Ctx.Host.InstantBuild = on;
        Body.AddChild(instant);
        var nights = new CheckButton
        {
            Text = "Skip nights: fast-forward while the park is closed and empty and the crew is off",
            ButtonPressed = Ctx.Host.AutoSkipNights,
            FocusMode = FocusModeEnum.None,
        };
        nights.Toggled += on => Ctx.Host.AutoSkipNights = on;
        Body.AddChild(nights);
        Body.AddChild(UiTheme.Label("Space pause · 1–4 speed · +/− zoom · B build · V trails · C crew · R riders · G lifts · M finances · U reputation · O map", 11, UiTheme.TextDim));
    }

    public override void Refresh()
    {
        bool career = Ctx.Host.CurrentCareerId is not null;
        _menu.Text = career ? "Save & back to menu" : "Back to menu";
        _saveNote.Text = career
            ? "Your career is saved automatically when you quit or go back to the menu."
            : "The Demo is a sandbox: it is never saved.";
    }
}
