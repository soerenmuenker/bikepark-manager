using Bikepark.Game.Camera;
using Bikepark.Game.Lifts;
using Bikepark.Game.Riders;
using Bikepark.Game.Terrain;
using Bikepark.Game.Ways;
using Bikepark.Sim;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Reporting;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Trails;
using Godot;
using Gradient = Bikepark.Sim.Trails.Gradient;

namespace Bikepark.Game.Ui;

/// <summary>
/// The game HUD, laid out like SimCity's: a bar along the bottom with the clock and speed (left), round category
/// buttons that open menus above the bar (centre: Build, Trails, Riders, Lifts, Finances, Map) and the headline stats
/// (right: money, guests, mood, lift queue; clicking one opens its menu). While a build tool is active a compact tool
/// panel shows at the top; events appear as short toasts. Pure view: reads the simulation, issues commands.
/// <para>
/// Keys: Space pause · 1–4 speed · B V R G M O open menus · Esc closes the menu · [ ] bike access tier · +/− zoom
/// (also the zoom buttons next to the stats).
/// Debug: <c>--screenshot=&lt;file.png&gt;</c> saves a screenshot after a few seconds and quits; <c>--panel=build</c>
/// opens a menu at start.
/// </para>
/// </summary>
public partial class Hud : CanvasLayer
{
    private enum Menu { None, Build, Trails, Riders, Lifts, Finance, Map, System }

    private const int MaxToasts = 4;

    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";
    [Export] public NodePath TerrainPath { get; set; } = "../TerrainView";
    [Export] public NodePath CameraPath { get; set; } = "../RtsCamera";
    [Export] public NodePath WayToolPath { get; set; } = "../WayTool";
    [Export] public NodePath RiderViewPath { get; set; } = "../RiderView";
    [Export] public NodePath StructureToolPath { get; set; } = "../StructureTool";
    [Export] public NodePath FeatureToolPath { get; set; } = "../FeatureTool";

    private HudContext _ctx = null!;
    private Control _root = null!;
    private PanelContainer _bar = null!;
    private readonly Dictionary<Menu, HudPanel> _panels = [];
    private readonly Dictionary<Menu, RoundButton> _menuButtons = [];
    private readonly List<RoundButton> _speedButtons = [];
    private Menu _open = Menu.None;
    private double _refreshTimer;
    private readonly List<IDisposable> _subscriptions = [];

    private Label _parkName = null!, _clock = null!, _openState = null!;
    private Label _moneyValue = null!, _guestsValue = null!, _moodValue = null!, _queueValue = null!;
    private IconView _moodIcon = null!;
    private Control _queueChip = null!;

    private PanelContainer _toolPanel = null!;
    private Label _toolText = null!;
    private PanelContainer _followChip = null!;
    private Label _followText = null!;
    private VBoxContainer _toasts = null!;

    private string? _screenshotPath;
    private double _screenshotTimer = 4;

    public override void _Ready()
    {
        _ctx = new HudContext
        {
            Host = GetNode<SimHost>(SimHostPath),
            Terrain = GetNode<TerrainView>(TerrainPath),
            Camera = GetNode<RtsCamera>(CameraPath),
            Ways = GetNode<WayTool>(WayToolPath),
            Riders = GetNode<RiderView>(RiderViewPath),
            Structures = GetNode<StructureTool>(StructureToolPath),
            Features = GetNode<FeatureTool>(FeatureToolPath),
        };
        _ctx.Kpi = KpiReport.From(_ctx.Sim.State, includeHash: false);
        BuildUi();
        _ctx.Host.SimulationReplaced += Subscribe;
        Subscribe(_ctx.Sim);

        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--screenshot=", StringComparison.Ordinal)) _screenshotPath = arg[13..];
            else if (arg.StartsWith("--panel=", StringComparison.Ordinal) && Enum.TryParse<Menu>(arg[8..], true, out var menu)) Toggle(menu);
        }
    }

    public override void _ExitTree()
    {
        _ctx.Host.SimulationReplaced -= Subscribe;
        foreach (var s in _subscriptions) s.Dispose();
    }

    // ---------------------------------------------------------------- layout

    private void BuildUi()
    {
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Theme = UiTheme.Create() };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        // Bottom bar.
        _bar = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        var barStyle = UiTheme.Box(UiTheme.Bar, 0, 18, 8, null);
        barStyle.BorderColor = new Color(UiTheme.Accent, 0.55f);
        barStyle.BorderWidthTop = 2;
        _bar.AddThemeStyleboxOverride("panel", barStyle);
        _root.AddChild(_bar);
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 14);
        _bar.AddChild(bar);

        bar.AddChild(ClockBlock());
        bar.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        var menus = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        menus.AddThemeConstantOverride("separation", 10);
        foreach (var (menu, icon, caption, tooltip) in new[]
                 {
                     (Menu.Build, UiIcon.Build, "Build", "Build paths, trails, lifts, parking  [B]"),
                     (Menu.Trails, UiIcon.Trails, "Trails", "Your trails and paths  [V]"),
                     (Menu.Riders, UiIcon.Riders, "Riders", "Guests, mood and fun  [R]"),
                     (Menu.Lifts, UiIcon.Lift, "Lifts", "Queues and bike access  [G]"),
                     (Menu.Finance, UiIcon.Finance, "Finances", "Money, fees, daily results  [M]"),
                     (Menu.Map, UiIcon.Map, "Map", "Terrain overlays  [O]"),
                 })
        {
            var m = menu;
            var button = new RoundButton(icon, 50, caption, tooltip, () => Toggle(m));
            menus.AddChild(button);
            _menuButtons[menu] = button;
        }
        bar.AddChild(menus);
        bar.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        var zoom = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        zoom.AddThemeConstantOverride("separation", 4);
        zoom.AddChild(new RoundButton(UiIcon.ZoomIn, 30, "", "Zoom in  [+]", _ctx.Camera.ZoomIn));
        zoom.AddChild(new RoundButton(UiIcon.ZoomOut, 30, "", "Zoom out  [−]", _ctx.Camera.ZoomOut));
        bar.AddChild(zoom);
        bar.AddChild(StatsBlock());
        var system = new RoundButton(UiIcon.Menu, 36, "", "Game: save, load, skip time", () => Toggle(Menu.System));
        system.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        _menuButtons[Menu.System] = system;
        bar.AddChild(system);

        // Menus.
        AddPanel(Menu.Build, new BuildPanel());
        AddPanel(Menu.Trails, new TrailsPanel());
        AddPanel(Menu.Riders, new RidersPanel());
        AddPanel(Menu.Lifts, new LiftsPanel());
        AddPanel(Menu.Finance, new FinancePanel());
        AddPanel(Menu.Map, new MapPanel());
        AddPanel(Menu.System, new SystemPanel());

        // Floating: tool panel (top centre), follow chip, toasts (top right).
        _toolPanel = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _toolPanel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, 12, 16, 10, new Color(UiTheme.Accent, 0.4f), 1));
        _toolText = UiTheme.Label("", 13);
        _toolPanel.AddChild(_toolText);
        _root.AddChild(_toolPanel);

        _followChip = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _followChip.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, 16, 14, 6));
        var follow = new HBoxContainer();
        follow.AddChild(new IconView(UiIcon.Follow, 18, UiTheme.Accent));
        _followText = UiTheme.Label("", 13);
        follow.AddChild(_followText);
        _followChip.AddChild(follow);
        _root.AddChild(_followChip);

        _toasts = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End };
        _toasts.AddThemeConstantOverride("separation", 6);
        _root.AddChild(_toasts);
    }

    private Control ClockBlock()
    {
        var box = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        box.AddThemeConstantOverride("separation", 2);
        _parkName = UiTheme.Label("", 11, UiTheme.TextDim, bold: true);
        box.AddChild(_parkName);
        var time = new HBoxContainer();
        _clock = UiTheme.Label("", 20, bold: true);
        time.AddChild(_clock);
        _openState = UiTheme.Label("", 11, UiTheme.Good, bold: true);
        _openState.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        time.AddChild(_openState);
        box.AddChild(time);

        var speeds = new HBoxContainer();
        speeds.AddThemeConstantOverride("separation", 4);
        UiIcon[] icons = [UiIcon.Pause, UiIcon.Play1, UiIcon.Play2, UiIcon.Play3, UiIcon.Play4];
        for (int i = 0; i < SimHost.SpeedMultipliers.Length; i++)
        {
            int index = i;
            int speed = SimHost.SpeedMultipliers[i];
            var button = new RoundButton(icons[i], 26, "", speed == 0 ? "Pause  [Space]" : $"{speed}x  [{i}]", () => _ctx.Host.SetSpeedIndex(index));
            speeds.AddChild(button);
            _speedButtons.Add(button);
        }
        speeds.AddChild(new RoundButton(UiIcon.Skip, 26, "", "Skip to opening hours", _ctx.Host.SkipToOpeningHours));
        box.AddChild(speeds);
        return box;
    }

    private Control StatsBlock()
    {
        var box = new HBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        box.AddChild(Chip(UiIcon.Finance, "Money", Menu.Finance, out _moneyValue, out _));
        box.AddChild(Chip(UiIcon.Person, "Guests", Menu.Riders, out _guestsValue, out _));
        box.AddChild(Chip(UiIcon.Mood, "Mood", Menu.Riders, out _moodValue, out _moodIcon));
        _queueChip = Chip(UiIcon.Queue, "Queue", Menu.Lifts, out _queueValue, out _);
        box.AddChild(_queueChip);
        return box;
    }

    private Control Chip(UiIcon icon, string caption, Menu menu, out Label value, out IconView iconView)
    {
        var chip = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.PointingHand, TooltipText = $"{caption} — click for details" };
        var normal = UiTheme.Box(new Color(1, 1, 1, 0.04f), 10, 10, 6);
        var hover = UiTheme.Box(new Color(1, 1, 1, 0.10f), 10, 10, 6);
        chip.AddThemeStyleboxOverride("panel", normal);
        chip.MouseEntered += () => chip.AddThemeStyleboxOverride("panel", hover);
        chip.MouseExited += () => chip.AddThemeStyleboxOverride("panel", normal);
        chip.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) Toggle(menu);
        };
        chip.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        chip.AddChild(row);
        iconView = new IconView(icon, 26, UiTheme.Accent) { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        row.AddChild(iconView);
        var texts = new VBoxContainer();
        texts.AddThemeConstantOverride("separation", -2);
        texts.AddChild(UiTheme.Label(caption.ToUpperInvariant(), 10, UiTheme.TextDim, bold: true));
        value = UiTheme.Label("", 18, bold: true);
        value.CustomMinimumSize = new Vector2(caption == "Money" ? 84 : 44, 0);
        texts.AddChild(value);
        row.AddChild(texts);
        return chip;
    }

    private void AddPanel(Menu menu, HudPanel panel)
    {
        panel.Init(_ctx);
        panel.Visible = false;
        panel.CloseRequested += () => Toggle(Menu.None);
        _root.AddChild(panel);
        _panels[menu] = panel;
    }

    private void Toggle(Menu menu)
    {
        _open = _open == menu ? Menu.None : menu;
        foreach (var (m, panel) in _panels)
        {
            panel.Visible = m == _open;
            if (panel.Visible) panel.Refresh();
        }
        foreach (var (m, button) in _menuButtons) button.Active = m == _open;
        // Leaving the build menu ends the build tools.
        if (_open != Menu.Build)
        {
            _ctx.Ways.SetMode(WayTool.ToolMode.None);
            _ctx.Structures.SetMode(StructureTool.ToolMode.None);
            _ctx.Features.SetType(null);
        }
    }

    private void Layout()
    {
        var view = _root.GetViewportRect().Size;
        _bar.Position = new Vector2(0, view.Y - UiTheme.BarHeight);
        _bar.Size = new Vector2(view.X, UiTheme.BarHeight);

        if (_open != Menu.None && _panels[_open] is { } panel)
        {
            var size = panel.GetCombinedMinimumSize();
            panel.Size = size;
            panel.Position = new Vector2(MathF.Round((view.X - size.X) / 2), view.Y - UiTheme.BarHeight - UiTheme.Gap - size.Y);
        }

        float top = 14;
        if (_toolPanel.Visible)
        {
            var size = _toolPanel.GetCombinedMinimumSize();
            _toolPanel.Size = size;
            _toolPanel.Position = new Vector2(MathF.Round((view.X - size.X) / 2), top);
            top += size.Y + 8;
        }
        if (_followChip.Visible)
        {
            var size = _followChip.GetCombinedMinimumSize();
            _followChip.Size = size;
            _followChip.Position = new Vector2(MathF.Round((view.X - size.X) / 2), top);
        }

        var toastSize = _toasts.GetCombinedMinimumSize();
        _toasts.Size = toastSize;
        _toasts.Position = new Vector2(view.X - toastSize.X - 14, 14);
    }

    // ---------------------------------------------------------------- per frame

    public override void _Process(double delta)
    {
        var state = _ctx.Sim.State;
        _refreshTimer -= delta;
        if (_refreshTimer <= 0)
        {
            _refreshTimer = 0.25;
            _ctx.Kpi = KpiReport.From(state, includeHash: false);
            UpdateBar();
            if (_open != Menu.None) _panels[_open].Refresh();
        }
        UpdateClock();
        UpdateToolPanel();
        _followText.Text = _ctx.Riders.FollowedRider ?? "";
        _followChip.Visible = _ctx.Riders.FollowedRider is not null;
        Layout();

        if (_screenshotPath is not null && (_screenshotTimer -= delta) <= 0)
        {
            GetViewport().GetTexture().GetImage().SavePng(_screenshotPath);
            GD.Print($"Screenshot saved to {_screenshotPath}");
            _screenshotPath = null;
            GetTree().Quit();
        }
    }

    private void UpdateClock()
    {
        var state = _ctx.Sim.State;
        _parkName.Text = state.Park.Name.ToUpperInvariant();
        _clock.Text = $"Day {GameTime.Day(state.Tick) + 1}  {GameTime.Format(state.Tick)[^5..]}";
        bool open = ParkSchedule.IsOpen(state, state.Tick);
        _openState.Text = _ctx.Host.IsSkipping ? "  FAST-FORWARD" : open ? "  OPEN" : "  CLOSED";
        _openState.AddThemeColorOverride("font_color", _ctx.Host.IsSkipping ? UiTheme.Warn : open ? UiTheme.Good : UiTheme.TextDim);
        for (int i = 0; i < _speedButtons.Count; i++)
            _speedButtons[i].Active = !_ctx.Host.IsSkipping && _ctx.Host.SpeedIndex == i;
    }

    private void UpdateBar()
    {
        var k = _ctx.Kpi;
        var state = _ctx.Sim.State;
        _moneyValue.Text = UiTheme.Money(k.MoneyCents);
        _moneyValue.AddThemeColorOverride("font_color", k.MoneyCents < 0 ? UiTheme.Bad : UiTheme.Text);
        _guestsValue.Text = $"{k.GuestsInPark}";
        bool anyone = k.GuestsInPark > 0;
        int mood = anyone ? k.AverageHappiness : k.AverageExitHappiness;
        _moodValue.Text = mood == 0 ? "–" : $"{mood / 10}%";
        _moodIcon.Set(UiIcon.Mood, mood == 0 ? UiTheme.TextDim : UiTheme.MoodColor(mood), mood == 0 ? 500 : mood);

        var lift = state.Lifts.FirstOrDefault();
        _queueChip.Visible = lift is not null;
        if (lift is not null && LiftNetwork.FindType(state, lift.TypeId) is { } type)
        {
            int wait = LiftMath.ExpectedWaitMinutes(type, lift.BikeCarrierPermille, lift.Queue.Count);
            _queueValue.Text = wait < 0 ? "no bikes" : wait == 0 ? $"{lift.Queue.Count}" : $"{lift.Queue.Count} · {wait}′";
            _queueValue.AddThemeColorOverride("font_color", wait < 0 || wait > 20 ? UiTheme.Bad : wait > 8 ? UiTheme.Warn : UiTheme.Text);
        }
    }

    private void UpdateToolPanel()
    {
        var lines = new List<string>();
        var structures = _ctx.Structures;
        var ways = _ctx.Ways;
        var features = _ctx.Features;
        if (features.Active)
        {
            lines.Add(features.Status);
            if (features.Hovered is { } hovered)
                lines.Add($"{hovered.Feature.Type.Name} at {hovered.Feature.StartCm / 100} m · Delete removes it");
            else if (features.Plan is { } fp)
            {
                string trail = _ctx.Sim.Network.FindWay(fp.WayId)?.Name ?? "";
                lines.Add($"{trail} · {fp.StartCm / 100}–{fp.EndCm / 100} m");
                lines.AddRange(fp.Issues.Where(i => i.Severity == IssueSeverity.Error).Take(2).Select(i => $"✗ {i.Message}"));
                if (fp.IsValid) lines.Add("✓ Click to place");
            }
            else lines.Add("Point at a trail · Esc to stop");
        }
        else if (structures.Mode != StructureTool.ToolMode.None)
        {
            lines.Add(structures.Status);
            if (structures.LiftPlan is { } lp)
            {
                if (lp.LengthCm > 0) lines.Add($"{lp.HorizontalCm / 100} m long · +{lp.RiseCm / 100} m · ride {lp.RideSeconds / 60.0:F1} min");
                lines.AddRange(lp.Issues.Take(2).Select(i => $"✗ {i.Message}"));
                if (lp.IsValid) lines.Add("✓ Enter to build");
            }
            if (structures.ParkingPlan is { } pp)
            {
                lines.AddRange(pp.Issues.Take(2).Select(i => $"✗ {i.Message}"));
                if (pp.IsValid) lines.Add("✓ Enter to build");
            }
        }
        else if (ways.Mode != WayTool.ToolMode.None)
        {
            lines.Add($"{ways.Status}   ·   {ways.PointCount} points");
            if (ways.Plan is { Geometry: { } g } plan)
            {
                string kind = plan.Kind == WayKind.Trail ? $" · {g.Rating} (difficulty {g.DifficultyScore})" : "";
                lines.Add($"{plan.LengthCm / 100} m · {(plan.DropCm >= 0 ? "drop" : "climb")} {Math.Abs(plan.DropCm) / 100} m · " +
                          $"steepest {Gradient.Format(-g.MaxDropGradient)} / {Gradient.Format(g.MaxClimbGradient)}{kind}");
                foreach (var issue in plan.Issues.Where(i => i.Severity == IssueSeverity.Error).Take(2))
                    lines.Add($"✗ {issue.Message}");
                if (plan.IsValid)
                    lines.Add(plan.Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Warning) is { } warning ? $"! {warning.Message}   ✓ Enter to build" : "✓ Enter to build");
            }
        }
        _toolPanel.Visible = lines.Count > 0;
        _toolText.Text = string.Join('\n', lines);
    }

    // ---------------------------------------------------------------- input

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        bool toolActive = _ctx.Ways.Mode != WayTool.ToolMode.None || _ctx.Structures.Mode != StructureTool.ToolMode.None || _ctx.Features.Active;
        // Bike tier keys go by character: on e.g. German layouts the "+" key sits where US "]" is.
        if (key.Keycode is Key.Bracketleft or Key.Bracketright)
        {
            _ctx.StepBikeTier(key.Keycode == Key.Bracketleft ? -1 : 1);
            GetViewport().SetInputAsHandled();
            return;
        }
        switch (key.PhysicalKeycode)
        {
            case Key.B: Toggle(Menu.Build); break;
            case Key.V: Toggle(Menu.Trails); break;
            case Key.R: Toggle(Menu.Riders); break;
            case Key.G: Toggle(Menu.Lifts); break;
            case Key.M: Toggle(Menu.Finance); break;
            case Key.O: Toggle(Menu.Map); break;
            case Key.Escape when !toolActive && _open != Menu.None: Toggle(Menu.None); break;
            case Key.Space: _ctx.Host.SetSpeedIndex(_ctx.Host.SpeedIndex == 0 ? 1 : 0); break;
            case Key.Key1 or Key.Key2 or Key.Key3 or Key.Key4: _ctx.Host.SetSpeedIndex((int)(key.PhysicalKeycode - Key.Key0)); break;
            case Key.P or Key.T or Key.L or Key.K when _open != Menu.Build:
                // Build hotkeys open the build menu (and the tool, handled by the tools afterwards).
                _open = Menu.None;
                Toggle(Menu.Build);
                return;
            default: return;
        }
        GetViewport().SetInputAsHandled();
    }

    // ---------------------------------------------------------------- events → toasts

    private void Subscribe(Simulation sim)
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        _ctx.Days.Clear();
        var events = sim.Events;
        _subscriptions.Add(events.Subscribe<DayEnded>(e =>
        {
            _ctx.Days.Add(e.Report);
            long net = e.Report.RevenueCents - e.Report.ExpensesCents;
            Toast($"Day {e.Report.Day + 1} closed: {e.Report.Visitors} visitors, {e.Report.LiftRides} lift rides, net {UiTheme.Money(net)}",
                net >= 0 ? UiTheme.Good : UiTheme.Bad);
        }));
        _subscriptions.Add(events.Subscribe<ParkOpened>(_ => Toast("The park is open", UiTheme.Good)));
        _subscriptions.Add(events.Subscribe<ParkClosed>(_ => Toast("The park has closed", UiTheme.TextDim)));
        _subscriptions.Add(events.Subscribe<CommandRejected>(e => Toast(e.Reason, UiTheme.Bad)));
        _subscriptions.Add(events.Subscribe<WayBuilt>(e => Toast($"Built {_ctx.Sim.State.Ways.FirstOrDefault(w => w.Id == e.WayId)?.Name}", UiTheme.Accent)));
        _subscriptions.Add(events.Subscribe<TrailFeaturePlaced>(e => Toast(FeatureText(e.WayId, e.FeatureId), UiTheme.Accent)));
        _subscriptions.Add(events.Subscribe<LiftBuilt>(e => Toast($"Built {_ctx.Sim.State.Lifts.FirstOrDefault(l => l.Id == e.LiftId)?.Name}", UiTheme.Accent)));
        _subscriptions.Add(events.Subscribe<ParkingLotBuilt>(_ => Toast("Built a parking lot", UiTheme.Accent)));
        _subscriptions.Add(events.Subscribe<BikeAccessBooked>(e => Toast(TierText(e.LiftId, e.TierIndex, booked: true), UiTheme.Accent)));
        _subscriptions.Add(events.Subscribe<BikeAccessChanged>(e => Toast(TierText(e.LiftId, e.TierIndex, booked: false), UiTheme.Accent)));
    }

    private string FeatureText(int wayId, int featureId)
    {
        var state = _ctx.Sim.State;
        var way = state.Ways.FirstOrDefault(w => w.Id == wayId);
        var feature = way?.Features.FirstOrDefault(f => f.Id == featureId);
        var type = feature is null ? null : TrailFeatures.FindType(state.TrailFeatureTypes, feature.TypeId);
        return $"Built a {type?.Name.ToLowerInvariant() ?? "feature"} on {way?.Name} at {feature?.DistanceCm / 100} m";
    }

    private string TierText(int liftId, int tierIndex, bool booked)
    {
        var state = _ctx.Sim.State;
        var lift = state.Lifts.FirstOrDefault(l => l.Id == liftId);
        var tier = LiftNetwork.FindOperator(state, lift?.OperatorId)?.BikeAccessTiers.ElementAtOrDefault(tierIndex);
        string name = tier is null ? $"tier {tierIndex}" : $"“{tier.Name}” ({UiTheme.Money(tier.DailyFeeCents)}/day)";
        return booked ? $"Booked {name} from the next opening" : $"{lift?.Name}: {name} now in effect";
    }

    private void Toast(string text, Color accent)
    {
        GD.Print(text);
        var toast = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = UiTheme.Box(UiTheme.Panel, 8, 12, 7);
        style.BorderColor = accent;
        style.BorderWidthLeft = 4;
        toast.AddThemeStyleboxOverride("panel", style);
        var label = UiTheme.Label(text, 13);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(320, 0);
        toast.AddChild(label);
        _toasts.AddChild(toast);
        while (_toasts.GetChildCount() > MaxToasts)
        {
            var oldest = _toasts.GetChild(0);
            _toasts.RemoveChild(oldest);
            oldest.QueueFree();
        }
        var tween = toast.CreateTween();
        tween.TweenInterval(5.0);
        tween.TweenProperty(toast, "modulate:a", 0.0, 0.6);
        tween.TweenCallback(Callable.From(() => { if (IsInstanceValid(toast)) toast.QueueFree(); }));
    }
}
