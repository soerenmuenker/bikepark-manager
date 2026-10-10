using Bikepark.Game.Camera;
using Bikepark.Game.Crew;
using Bikepark.Game.Lifts;
using Bikepark.Game.Riders;
using Bikepark.Game.Terrain;
using Bikepark.Game.Ways;
using Bikepark.Sim;
using Bikepark.Sim.Core;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Reporting;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Trails;
using Bikepark.Sim.Weather;
using Godot;
using Gradient = Bikepark.Sim.Trails.Gradient;

namespace Bikepark.Game.Ui;

/// <summary>
/// The game HUD, laid out like SimCity's: a bar along the bottom with the clock and speed (left), round category
/// buttons that open menus above the bar (centre: Build, Trails, Crew, Riders, Lifts, Finances, Map) and the headline
/// stats (right: money, guests, mood, rating and level, lift queue, crew, wood; clicking one opens its menu). While a build tool is active a compact tool
/// panel shows at the top; events appear as short toasts. Pure view: reads the simulation, issues commands.
/// <para>
/// Keys: Space pause · 1–4 speed · B V C R G M U O open menus · Esc closes the menu · [ ] bike access tier · +/− zoom
/// (also the zoom buttons next to the stats).
/// Debug: <c>--screenshot=&lt;file.png&gt;</c> saves a screenshot after a few seconds and quits; <c>--panel=build</c>
/// opens a menu at start; <c>--tool=trail</c> (path, fell, lift, parking or a feature id) starts a build tool;
/// <c>--follow</c> follows the first rider (and shows the rider card).
/// </para>
/// </summary>
public partial class Hud : CanvasLayer
{
    private enum Menu { None, Build, Trails, Crew, Riders, Lifts, Finance, Reputation, Land, Map, System }

    private const int MaxToasts = 4;

    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";
    [Export] public NodePath TerrainPath { get; set; } = "../TerrainView";
    [Export] public NodePath CameraPath { get; set; } = "../RtsCamera";
    [Export] public NodePath WayToolPath { get; set; } = "../WayTool";
    [Export] public NodePath RiderViewPath { get; set; } = "../RiderView";
    [Export] public NodePath StructureToolPath { get; set; } = "../StructureTool";
    [Export] public NodePath FeatureToolPath { get; set; } = "../FeatureTool";
    [Export] public NodePath ClearingToolPath { get; set; } = "../ClearingTool";

    private HudContext _ctx = null!;
    private Control _root = null!;
    private PanelContainer _bar = null!;
    private readonly Dictionary<Menu, HudPanel> _panels = [];
    private readonly Dictionary<Menu, RoundButton> _menuButtons = [];
    private readonly List<RoundButton> _speedButtons = [];
    private Menu _open = Menu.None;
    private double _refreshTimer;
    private readonly List<IDisposable> _subscriptions = [];

    private Label _parkName = null!, _clock = null!, _openState = null!, _weather = null!;
    private Label _moneyValue = null!, _guestsValue = null!, _moodValue = null!, _queueValue = null!, _crewValue = null!, _woodValue = null!;
    private IconView _moodIcon = null!, _ratingIcon = null!;
    private Label _ratingValue = null!;
    private Control _queueChip = null!;

    private PanelContainer _toolPanel = null!;
    private PanelContainer _toolChip = null!;
    private IconView _toolChipIcon = null!;
    private Label _toolChipText = null!;
    private Label _toolText = null!;
    private RiderCard _riderCard = null!;
    private bool _followOnStart;
    private VBoxContainer _toasts = null!;
    private RepairDialog _repairDialog = null!;
    private NameDialog _nameDialog = null!;

    private LandView _land = null!;
    private StartScreen _startScreen = null!;
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
            Clearing = GetNode<ClearingTool>(ClearingToolPath),
        };
        _ctx.Kpi = KpiReport.From(_ctx.Sim.State, includeHash: false, network: _ctx.Sim.Network);
        BuildUi();
        _ctx.Host.SimulationReplaced += Subscribe;
        Subscribe(_ctx.Sim);
        _ctx.Host.CareerSaved += OnCareerSaved;
        _ctx.TrailEdit = new TrailEditTool(_ctx.Host, _ctx.Terrain, _ctx.Camera);
        GetParent().CallDeferred(Node.MethodName.AddChild, _ctx.TrailEdit);
        _land = new LandView(_ctx.Host, _ctx.Terrain, () => _ctx.Kpi.Level);
        _ctx.Terrain.CallDeferred(Node.MethodName.AddChild, _land);
        _startScreen = new StartScreen(_ctx.Host);
        _root.AddChild(_startScreen);

        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--screenshot=", StringComparison.Ordinal)) _screenshotPath = arg[13..];
            else if (arg.StartsWith("--panel=", StringComparison.Ordinal) && Enum.TryParse<Menu>(arg[8..], true, out var menu)) Toggle(menu);
            else if (arg.StartsWith("--tool=", StringComparison.Ordinal)) StartTool(arg[7..]);
            else if (arg.StartsWith("--features=", StringComparison.Ordinal) && int.TryParse(arg[11..], out int wayId)) _repairDialog.Open(wayId);
            else if (arg.StartsWith("--rename=", StringComparison.Ordinal) && int.TryParse(arg[9..], out int renameId)) _ctx.RenameTrail(renameId);
            else if (arg == "--follow") _followOnStart = true;
        }
    }

    public override void _ExitTree()
    {
        _ctx.Host.SimulationReplaced -= Subscribe;
        _ctx.Host.CareerSaved -= OnCareerSaved;
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
        menus.AddThemeConstantOverride("separation", 0);
        foreach (var (menu, icon, caption, tooltip) in new[]
                 {
                     (Menu.Build, UiIcon.Build, "Build", "Plan paths, trails, features; fell trees; lifts, parking  [B]"),
                     (Menu.Trails, UiIcon.Trails, "Trails", "Your trails  [V]"),
                     (Menu.Crew, UiIcon.Crew, "Crew", "Workers, jobs, wood and tools  [C]"),
                     (Menu.Riders, UiIcon.Riders, "Riders", "Guests, mood and fun  [R]"),
                     (Menu.Lifts, UiIcon.Lift, "Lifts", "Queues and bike access  [G]"),
                     (Menu.Finance, UiIcon.Finance, "Finances", "Money, fees, daily results  [M]"),
                     (Menu.Reputation, UiIcon.Star, "Rating", "Reviews, rating, influencers, park level  [U]"),
                     (Menu.Land, UiIcon.Land, "Land", "Buy land to build on  [N]"),
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
        AddPanel(Menu.Crew, new CrewPanel());
        AddPanel(Menu.Riders, new RidersPanel());
        AddPanel(Menu.Lifts, new LiftsPanel());
        AddPanel(Menu.Finance, new FinancePanel());
        AddPanel(Menu.Reputation, new ReputationPanel());
        AddPanel(Menu.Land, new LandPanel());
        AddPanel(Menu.Map, new MapPanel());
        AddPanel(Menu.System, new SystemPanel());

        // Floating: tool panel (top centre), follow chip, toasts (top right).
        // While a build tool is active the Build menu folds into this narrow chip above the bar.
        _toolChip = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        var chipStyle = UiTheme.Box(UiTheme.Panel, 14, 14, 6, new Color(UiTheme.Accent, 0.6f), 1);
        _toolChip.AddThemeStyleboxOverride("panel", chipStyle);
        var chipRow = new HBoxContainer();
        chipRow.AddThemeConstantOverride("separation", 10);
        _toolChipIcon = new IconView(UiIcon.Build, 24, UiTheme.Accent) { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        chipRow.AddChild(_toolChipIcon);
        _toolChipText = UiTheme.Label("", 14, bold: true);
        _toolChipText.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        chipRow.AddChild(_toolChipText);
        var chipHint = UiTheme.Label("Esc or ✕ to stop", 12, UiTheme.TextDim);
        chipHint.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        chipRow.AddChild(chipHint);
        chipRow.AddChild(new RoundButton(UiIcon.Close, 24, "", "Stop building (Esc)", EndTools) { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
        _toolChip.AddChild(chipRow);
        _root.AddChild(_toolChip);

        _toolPanel = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _toolPanel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, 12, 16, 10, new Color(UiTheme.Accent, 0.4f), 1));
        _toolText = UiTheme.Label("", 13);
        _toolPanel.AddChild(_toolText);
        _root.AddChild(_toolPanel);

        _riderCard = new RiderCard(_ctx);
        _root.AddChild(_riderCard);
        _ctx.Riders.CanPick = () => !ToolActive;

        _repairDialog = new RepairDialog(_ctx);
        _ctx.OpenRepair = _repairDialog.Open;
        _root.AddChild(_repairDialog);

        _nameDialog = new NameDialog();
        _ctx.AskName = _nameDialog.Ask;
        _ctx.Ways.AskName = (suggestion, ok, cancel) => _nameDialog.Ask("Name your trail",
            "It's planned once named: the crew builds it (Crew menu). You can rename it later in the Trails menu.",
            suggestion, name => Bikepark.Sim.Commands.RenameTrailCommand.CannotName(_ctx.Sim.State, name), ok, cancel);
        _root.AddChild(_nameDialog);

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
        _weather = UiTheme.Label("", 11, UiTheme.TextDim);
        _weather.TooltipText = "Today's weather and tomorrow's forecast. Rain keeps guests away and wears wet trails faster.";
        _weather.MouseFilter = Control.MouseFilterEnum.Stop;
        box.AddChild(_weather);

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
        box.AddThemeConstantOverride("separation", 4);
        box.AddChild(Chip(UiIcon.Finance, "Money", Menu.Finance, out _moneyValue, out _));
        box.AddChild(Chip(UiIcon.Person, "Guests", Menu.Riders, out _guestsValue, out _));
        box.AddChild(Chip(UiIcon.Mood, "Mood", Menu.Riders, out _moodValue, out _moodIcon));
        box.AddChild(Chip(UiIcon.Star, "Rating", Menu.Reputation, out _ratingValue, out _ratingIcon));
        _queueChip = Chip(UiIcon.Queue, "Queue", Menu.Lifts, out _queueValue, out _);
        box.AddChild(_queueChip);
        box.AddChild(Chip(UiIcon.Crew, "Crew", Menu.Crew, out _crewValue, out _));
        box.AddChild(Chip(UiIcon.Wood, "Wood", Menu.Crew, out _woodValue, out _));
        return box;
    }

    private Control Chip(UiIcon icon, string caption, Menu menu, out Label value, out IconView iconView)
    {
        var chip = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.PointingHand, TooltipText = $"{caption} — click for details" };
        var normal = UiTheme.Box(new Color(1, 1, 1, 0.04f), 10, 7, 6);
        var hover = UiTheme.Box(new Color(1, 1, 1, 0.10f), 10, 7, 6);
        chip.AddThemeStyleboxOverride("panel", normal);
        chip.MouseEntered += () => chip.AddThemeStyleboxOverride("panel", hover);
        chip.MouseExited += () => chip.AddThemeStyleboxOverride("panel", normal);
        chip.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) Toggle(menu);
        };
        chip.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        chip.AddChild(row);
        iconView = new IconView(icon, 24, UiTheme.Accent) { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        row.AddChild(iconView);
        var texts = new VBoxContainer();
        texts.AddThemeConstantOverride("separation", -2);
        texts.AddChild(UiTheme.Label(caption.ToUpperInvariant(), 10, UiTheme.TextDim, bold: true));
        value = UiTheme.Label("", 18, bold: true);
        value.CustomMinimumSize = new Vector2(caption switch { "Money" => 84, "Crew" => 60, "Rating" => 62, _ => 44 }, 0);
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
        bool wasBuild = _open == Menu.Build;
        _open = _open == menu ? Menu.None : menu;
        foreach (var (m, panel) in _panels)
        {
            panel.Visible = m == _open;
            if (panel.Visible) panel.Refresh();
        }
        foreach (var (m, button) in _menuButtons) button.Active = m == _open;
        // Leaving the build menu ends the build tools (and reminds of trails left with a loose end).
        if (_open != Menu.Build)
        {
            if (wasBuild) WarnUnconnected();
            _ctx.TrailEdit.SetMode(TrailEditTool.ToolMode.None);
            _ctx.Ways.SetMode(WayTool.ToolMode.None);
            _ctx.Structures.SetMode(StructureTool.ToolMode.None);
            _ctx.Features.SetType(null);
            if (_open != Menu.Crew) _ctx.Clearing.SetActive(false); // the crew menu can start felling too
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

        if (_toolChip.Visible)
        {
            var size = _toolChip.GetCombinedMinimumSize();
            _toolChip.Size = size;
            _toolChip.Position = new Vector2(MathF.Round((view.X - size.X) / 2), view.Y - UiTheme.BarHeight - UiTheme.Gap - size.Y);
        }

        if (_repairDialog.Visible)
        {
            var size = _repairDialog.GetCombinedMinimumSize();
            _repairDialog.Size = size;
            _repairDialog.Position = new Vector2(MathF.Round((view.X - size.X) / 2), MathF.Round((view.Y - size.Y) / 3));
        }

        float top = 14;
        if (_toolPanel.Visible)
        {
            var size = _toolPanel.GetCombinedMinimumSize();
            _toolPanel.Size = size;
            _toolPanel.Position = new Vector2(MathF.Round((view.X - size.X) / 2), top);
            top += size.Y + 8;
        }
        if (_riderCard.Visible)
        {
            var size = _riderCard.GetCombinedMinimumSize();
            _riderCard.Size = size;
            _riderCard.Position = new Vector2(14, 14);
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
            _ctx.Kpi = KpiReport.From(state, includeHash: false, network: _ctx.Sim.Network);
            UpdateBar();
            if (_open != Menu.None) _panels[_open].Refresh();
            _repairDialog.Refresh();
        }
        _land.ShowLabels = _open == Menu.Land;
        if (_startScreen.Visible != _ctx.Host.ShowStartScreen)
        {
            _startScreen.Visible = _ctx.Host.ShowStartScreen;
            if (_startScreen.Visible)
            {
                _startScreen.Refresh();
                Toggle(Menu.None);
            }
        }
        // Picking a path, trail, structure, feature or felling tool ends trail editing.
        if (_ctx.TrailEdit.Active && (_ctx.Ways.Mode != WayTool.ToolMode.None || _ctx.Structures.Mode != StructureTool.ToolMode.None
                                      || _ctx.Features.Active || _ctx.Clearing.Active))
            _ctx.TrailEdit.SetMode(TrailEditTool.ToolMode.None);
        UpdateClock();
        UpdateToolPanel();
        UpdateToolChip();
        if (_followOnStart && _ctx.Riders.VisibleRiders > 0)
        {
            _followOnStart = false;
            _ctx.Riders.FollowNext();
        }
        _riderCard.Refresh();
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
        var (text, color) = _ctx.Host.IsSkippingNight ? ("NIGHT ⏩", UiTheme.TextDim)
            : _ctx.Host.IsSkipping ? ("FAST-FORWARD", UiTheme.Warn)
            : ParkSchedule.Phase(state, state.Tick) switch
            {
                DayPhase.Open => ("OPEN", UiTheme.Good),
                DayPhase.PreOpening => ("PRE-OPENING", UiTheme.Warn),
                DayPhase.LastRides => ("LAST RIDES", UiTheme.Warn),
                _ => ("CLOSED", UiTheme.TextDim),
            };
        _openState.Text = "  " + text;
        _openState.AddThemeColorOverride("font_color", color);
        var weather = state.Weather;
        bool raining = WeatherMath.IsRaining(state, state.Tick);
        _weather.Text = (raining ? "RAINING" : WeatherText(weather.Today)) +
                        (weather.Tomorrow is { } tomorrow ? $"  ·  tomorrow {WeatherText(tomorrow)}" : "") +
                        (weather.WetnessPermille >= 100 ? $"  ·  ground {weather.WetnessPermille / 10} % wet" : "");
        _weather.AddThemeColorOverride("font_color", raining ? UiTheme.Accent : UiTheme.TextDim);
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

        _ratingValue.Text = $"{ReputationPanel.StarsText(k.RatingTenths)} · L{k.Level}";
        _ratingValue.TooltipText = k.RatingTenths is null ? $"No rating yet ({k.TotalReviews} reviews) · park level {k.Level}" : $"{k.TotalReviews} reviews · park level {k.Level}";
        _ratingIcon.Set(UiIcon.Star, k.RatingTenths is { } stars ? UiTheme.MoodColor((stars - 10) * 25) : UiTheme.TextDim);

        _crewValue.Text = $"{state.Crew.Count} · {state.Jobs.Count}";
        _crewValue.TooltipText = $"{state.Crew.Count} workers, {state.Jobs.Count} jobs";
        _woodValue.Text = $"{state.WoodStock}";
        bool waiting = state.Jobs.Any(j => j.Wood > 0 && !j.WoodTaken && j.Wood > state.WoodStock);
        _woodValue.AddThemeColorOverride("font_color", waiting ? UiTheme.Warn : UiTheme.Text);

        var lift = state.Lifts.FirstOrDefault(l => l.InService && l.BikeCarrierPermille > 0) ?? state.Lifts.FirstOrDefault();
        _queueChip.Visible = lift is not null;
        if (lift is not null && LiftNetwork.FindType(state, lift.TypeId) is { } type)
        {
            int wait = LiftMath.ExpectedWaitMinutes(type, lift.BikeCarrierPermille, lift.Queue.Count);
            _queueValue.Text = !lift.InService ? "–" : lift.IsStopped(state.Tick) ? "stop" : wait < 0 ? "no bikes" : wait == 0 ? $"{lift.Queue.Count}" : $"{lift.Queue.Count} · {wait}′";
            _queueValue.TooltipText = $"{lift.Name}: riders in the queue · expected wait";
            _queueValue.AddThemeColorOverride("font_color", !lift.InService ? UiTheme.TextDim : lift.IsStopped(state.Tick) ? UiTheme.Bad : wait < 0 || wait > 20 ? UiTheme.Bad : wait > 8 ? UiTheme.Warn : UiTheme.Text);
        }
    }

    private bool ToolActive => _ctx.Ways.Mode != WayTool.ToolMode.None || _ctx.Structures.Mode != StructureTool.ToolMode.None
                               || _ctx.Features.Active || _ctx.Clearing.Active || _ctx.TrailEdit.Active;

    /// <summary>
    /// While a build tool is active the open menu (Build, or Crew for felling) is folded away so the map is free to work
    /// on, and the chip above the bar names the tool; when the tool ends (Esc, ✕) the menu comes back.
    /// </summary>
    private void UpdateToolChip()
    {
        bool active = ToolActive;
        if (_open != Menu.None && _panels[_open].Visible == active)
        {
            _panels[_open].Visible = !active;
            if (!active) _panels[_open].Refresh();
        }
        _toolChip.Visible = active;
        if (!active) return;

        var (icon, name) = _ctx.TrailEdit.Mode == TrailEditTool.ToolMode.Renaturalize ? (UiIcon.Felling, "Renaturalize")
            : _ctx.Structures.Mode == StructureTool.ToolMode.Platform ? (UiIcon.Parking, "Gravel platform")
            : _ctx.Clearing.Active ? (UiIcon.Felling, "Fell trees")
            : _ctx.Features.Active && TrailFeatures.FindType(_ctx.Sim.State.TrailFeatureTypes, _ctx.Features.TypeId!) is { } type
                ? (BuildPanel.FeatureIcon(type.Kind), $"Feature: {type.Name}")
            : _ctx.Structures.Mode == StructureTool.ToolMode.Lift ? (UiIcon.Lift, "Lift")
            : _ctx.Structures.Mode == StructureTool.ToolMode.Parking ? (UiIcon.Parking, "Parking lot")
            : _ctx.Ways.Mode == WayTool.ToolMode.AccessPath ? (UiIcon.Path, "Gravel path")
            : (UiIcon.Trail, "Trail");
        _toolChipIcon.Set(icon, UiTheme.Accent);
        _toolChipText.Text = $"Building: {name}";
    }

    /// <summary>Debug (<c>--tool=trail|path|fell|lift|parking|&lt;feature id&gt;</c>): opens Build with that tool active.</summary>
    private void StartTool(string tool)
    {
        if (_open != Menu.Build) Toggle(Menu.Build);
        switch (tool)
        {
            case "trail": _ctx.Ways.SetMode(WayTool.ToolMode.Trail); break;
            case "path": _ctx.Ways.SetMode(WayTool.ToolMode.AccessPath); break;
            case "fell": _ctx.Clearing.SetActive(true); break;
            case "lift": _ctx.Structures.SetMode(StructureTool.ToolMode.Lift); break;
            case "parking": _ctx.Structures.SetMode(StructureTool.ToolMode.Parking); break;
            case "platform": _ctx.Structures.SetMode(StructureTool.ToolMode.Platform); break;
            case "renaturalize": _ctx.TrailEdit.SetMode(TrailEditTool.ToolMode.Renaturalize); break;
            default: _ctx.Features.SetType(tool); break;
        }
    }

    /// <summary>Ends every build tool (the chip's ✕).</summary>
    private void EndTools()
    {
        _ctx.TrailEdit.SetMode(TrailEditTool.ToolMode.None);
        _ctx.Ways.SetMode(WayTool.ToolMode.None);
        _ctx.Structures.SetMode(StructureTool.ToolMode.None);
        _ctx.Features.SetType(null);
        _ctx.Clearing.SetActive(false);
    }

    private void UpdateToolPanel()
    {
        var lines = new List<string>();
        var structures = _ctx.Structures;
        var ways = _ctx.Ways;
        var features = _ctx.Features;
        var clearing = _ctx.Clearing;
        if (_ctx.TrailEdit.Active)
        {
            lines.Add(_ctx.TrailEdit.Status);
            if (_ctx.TrailEdit.Problem is { } problem) lines.Add($"✗ {problem}");
            else lines.Add("Esc to start over or stop");
        }
        else if (clearing.Active)
        {
            lines.Add(clearing.Status);
            if (clearing.Plan is { } cp)
            {
                lines.Add($"Radius {cp.RadiusCm / 100} m · {cp.Trees.Count} trees · +{cp.Estimate.WoodGained} wood · {HudContext.CrewTime(cp.Estimate.TotalMinutes)}");
                lines.AddRange(cp.Issues.Where(i => i.Severity == IssueSeverity.Error).Take(2).Select(i => $"✗ {i.Message}"));
                if (cp.IsValid) lines.Add("✓ Click to mark the area");
            }
            else lines.Add("Esc to stop");
        }
        else if (features.Active)
        {
            lines.Add(features.Status);
            if (features.Hovered is { } hovered)
                lines.Add($"{hovered.Feature.Type.Name} at {hovered.Feature.StartCm / 100} m · Delete removes it");
            else if (features.Plan is { } fp)
            {
                string trail = _ctx.Sim.Network.FindWay(fp.WayId)?.Name ?? "";
                lines.Add($"{trail} · {fp.StartCm / 100}–{fp.EndCm / 100} m");
                lines.AddRange(fp.Issues.Where(i => i.Severity == IssueSeverity.Error).Take(2).Select(i => $"✗ {i.Message}"));
                if (fp.IsValid && fp.Type is { } type)
                {
                    lines.Add(_ctx.EstimateText(WorkCosts.Feature(type)));
                    lines.Add(_ctx.Host.InstantBuild ? "✓ Click to build" : "✓ Click to plan it for the crew");
                }
            }
            else lines.Add("Point at a trail · Esc to stop");
        }
        else if (structures.Mode != StructureTool.ToolMode.None)
        {
            lines.Add(structures.Status);
            if (structures.LiftPlan is { } lp)
            {
                if (lp.LengthCm > 0) lines.Add($"{lp.Type?.Name}: {lp.HorizontalCm / 100} m long · +{lp.RiseCm / 100} m · ride {lp.RideSeconds / 60.0:F1} min");
                if (structures.Locked() is { } locked) lines.Add($"✗ {locked}");
                lines.AddRange(lp.Issues.Take(2).Select(i => $"✗ {i.Message}"));
                if (lp.IsValid && structures.Locked() is null && lp.Type is { } type)
                    lines.Add(_ctx.Host.InstantBuild ? "✓ Enter to build (debug: free, at once)"
                        : $"✓ Enter to order it: {UiTheme.Money(type.BuildCostCents)}, ready in {type.BuildDays} days, then {UiTheme.Money(type.UpkeepPerDayCents)}/day");
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
                string kind = plan.Kind == WayKind.Trail ? $" · {WayMeshes.RatingText(g)}, difficulty {g.DifficultyScore}" : "";
                lines.Add($"{plan.LengthCm / 100} m · {(plan.DropCm >= 0 ? "drop" : "climb")} {Math.Abs(plan.DropCm) / 100} m · " +
                          $"steepest {Gradient.Format(-g.MaxDropGradient)} / {Gradient.Format(g.MaxClimbGradient)}{kind}");
                foreach (var issue in plan.Issues.Where(i => i.Severity == IssueSeverity.Error).Take(2))
                    lines.Add($"✗ {issue.Message}");
                if (plan.IsValid)
                {
                    if (ways.Estimate is { } estimate) lines.Add(_ctx.EstimateText(estimate));
                    string enter = _ctx.Host.InstantBuild ? "✓ Enter to build" : "✓ Enter to plan it for the crew";
                    lines.Add(plan.Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Warning) is { } warning ? $"! {warning.Message}   {enter}" : enter);
                }
            }
        }
        _toolPanel.Visible = lines.Count > 0;
        _toolText.Text = string.Join('\n', lines);
    }

    // ---------------------------------------------------------------- input

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        bool toolActive = ToolActive;
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
            case Key.C: Toggle(Menu.Crew); break;
            case Key.R: Toggle(Menu.Riders); break;
            case Key.G: Toggle(Menu.Lifts); break;
            case Key.M: Toggle(Menu.Finance); break;
            case Key.U: Toggle(Menu.Reputation); break;
            case Key.N: Toggle(Menu.Land); break;
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

    /// <summary>"Sunny", "Showers 13–15 h", "Rain 06–14 h".</summary>
    private static string WeatherText(DayWeather day) => day.HasRain
        ? $"{day.Kind} {day.RainStartMinute / 60:00}–{(day.RainEndMinute + 59) / 60:00} h"
        : day.Kind.ToString();

    private string TrailName(int wayId) => _ctx.Sim.State.Ways.FirstOrDefault(w => w.Id == wayId)?.Label ?? "A trail";

    // ---------------------------------------------------------------- events → toasts

    private void Subscribe(Simulation sim)
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        _ctx.Days.Clear();
        _ctx.Days.AddRange(sim.State.DayHistory); // charts continue after loading a career
        _repairDialog.Clear();
        var events = sim.Events;
        _subscriptions.Add(events.Subscribe<DayEnded>(e =>
        {
            _ctx.Days.Add(e.Report);
            long net = e.Report.RevenueCents - e.Report.ExpensesCents;
            Toast($"Day {e.Report.Day + 1} closed: {e.Report.Visitors} visitors, {e.Report.LiftRides} lift rides, net {UiTheme.Money(net)}",
                net >= 0 ? UiTheme.Good : UiTheme.Bad);
        }));
        _subscriptions.Add(events.Subscribe<InfluencerArrived>(e =>
            Toast($"Influencer {e.Name} is visiting today: their FakeSocial post depends on how the day goes", UiTheme.Accent)));
        _subscriptions.Add(events.Subscribe<InfluencerPosted>(e => Toast(
            $"{e.Post.Name} posted: {ReputationPanel.StarsText(e.Post.StarsTenths)} stars, visitors {e.Post.EffectPermille / 10:+0;-0;0} % for a few days",
            e.Post.EffectPermille > 0 ? UiTheme.Good : e.Post.EffectPermille < 0 ? UiTheme.Bad : UiTheme.TextDim)));
        _subscriptions.Add(events.Subscribe<LevelChanged>(e => Toast(e.NewLevel > e.OldLevel
            ? $"Park level {e.NewLevel} reached!" : $"The park dropped to level {e.NewLevel}", e.NewLevel > e.OldLevel ? UiTheme.Good : UiTheme.Warn)));
        _subscriptions.Add(events.Subscribe<TrailSplit>(e =>
            Toast($"The gravel path divides the trail into {TrailName(e.WayId)} and {TrailName(e.NewWayId)}", UiTheme.Accent)));
        _subscriptions.Add(events.Subscribe<TrailRenaturalized>(e =>
        {
            var way = _ctx.Sim.State.Ways.FirstOrDefault(w => w.Id == e.WayId);
            Toast(way is null ? "Renaturalized: nature takes it back"
                : e.NewWayId != 0 && way.Kind == WayKind.Trail ? $"Renaturalized {(e.ToCm - e.FromCm) / 100} m: {way.Name} and {TrailName(e.NewWayId)} are left"
                : $"Renaturalized {(e.ToCm - e.FromCm) / 100} m of {way.Label}", UiTheme.Accent);
            WarnUnconnected();
        }));
        _subscriptions.Add(events.Subscribe<StructureRenaturalized>(e =>
        {
            Toast(e.CostCents > 0 ? $"{e.Name} is torn down ({UiTheme.Money(e.CostCents)}): nature takes it back" : $"{e.Name} is gone: nature takes it back",
                UiTheme.Accent);
            WarnUnconnected();
        }));
        _subscriptions.Add(events.Subscribe<TrailRenamed>(e => Toast($"Renamed to {e.Name}", UiTheme.Accent)));
        _subscriptions.Add(events.Subscribe<TrailsJoined>(e => Toast($"Joined into {TrailName(e.WayId)}", UiTheme.Good)));
        _subscriptions.Add(events.Subscribe<PlatformBuilt>(_ => Toast("Built a gravel platform", UiTheme.Accent)));
        _subscriptions.Add(events.Subscribe<ParcelBought>(e => Toast(
            $"Bought {_ctx.Sim.State.Parcels.FirstOrDefault(p => p.Id == e.ParcelId)?.Name} for {UiTheme.Money(e.PriceCents)}: you can build there now", UiTheme.Good)));
        _subscriptions.Add(events.Subscribe<LiftConstructionStarted>(e => Toast(
            $"{(e.Restoration ? "Restoring" : "Building")} {LiftName(e.LiftId)} for {UiTheme.Money(e.CostCents)}: running from day {GameTime.Day(e.ReadyTick) + 1}", UiTheme.Accent)));
        _subscriptions.Add(events.Subscribe<LiftStopped>(e => Toast(
            $"{LiftName(e.LiftId)} stopped: a rider crashed on its track. It runs again at {GameTime.Format(e.UntilTick)[^5..]}", UiTheme.Bad)));
        _subscriptions.Add(events.Subscribe<LiftReady>(e => Toast($"{LiftName(e.LiftId)} is ready and running", UiTheme.Good)));
        _subscriptions.Add(events.Subscribe<RiderCrashed>(e =>
        {
            string where = e.FeatureId != 0 ? $" at the {FeatureTypeName(e.WayId, e.FeatureId)}" : e.Cause == Bikepark.Sim.Safety.CrashCause.Collision ? " (collision at a crossing)" : "";
            if (e.LiftId != 0)
                Toast(e.Severity == Bikepark.Sim.Safety.InjurySeverity.Serious
                    ? $"Serious crash on the {LiftName(e.LiftId)} track (hit by a rider on {TrailName(e.WayId)}): rescue helicopter called"
                    : $"Minor crash on the {LiftName(e.LiftId)} track (hit by a rider on {TrailName(e.WayId)}): the rider gets off and goes home",
                    e.Severity == Bikepark.Sim.Safety.InjurySeverity.Serious ? UiTheme.Bad : UiTheme.Warn);
            else if (e.Severity == Bikepark.Sim.Safety.InjurySeverity.Serious)
                Toast($"Serious crash on {TrailName(e.WayId)}{where}: rescue helicopter called, the trail is blocked", UiTheme.Bad);
            else
                Toast($"Minor crash on {TrailName(e.WayId)}{where}: the rider rides down slowly and goes home", UiTheme.Warn);
        }));
        _subscriptions.Add(events.Subscribe<RiderEvacuated>(e => Toast($"The helicopter has flown the injured rider out: {TrailName(e.WayId)} is clear", UiTheme.TextDim)));
        _subscriptions.Add(events.Subscribe<ParkOpened>(_ => Toast("The park is open", UiTheme.Good)));
        _subscriptions.Add(events.Subscribe<WeatherForecast>(e =>
            Toast($"Today: {WeatherText(e.Today)} · tomorrow: {WeatherText(e.Tomorrow)}", e.Today.HasRain ? UiTheme.Warn : UiTheme.TextDim)));
        _subscriptions.Add(events.Subscribe<RainStarted>(_ => Toast("It's raining: trails wear faster when wet", UiTheme.Warn)));
        _subscriptions.Add(events.Subscribe<TrailClosed>(e => Toast(e.Reason switch
        {
            TrailClosedReason.WornOut => $"{TrailName(e.WayId)} is worn out and closed until the crew repairs it",
            TrailClosedReason.Repair => $"{TrailName(e.WayId)} is closed while the crew repairs it",
            TrailClosedReason.Building => $"{TrailName(e.WayId)} is closed while the crew builds a feature",
            _ => $"{TrailName(e.WayId)} is closed",
        }, e.Reason == TrailClosedReason.WornOut ? UiTheme.Bad : UiTheme.TextDim)));
        _subscriptions.Add(events.Subscribe<FeatureWarning>(e =>
        {
            if (_repairDialog.Request(e.WayId, force: e.ConditionPermille == 0))
                Toast($"{TrailName(e.WayId)}: {FeatureName(e.WayId, e.FeatureId)} is worn down to {e.ConditionPermille / 10} %: it needs a repair", UiTheme.Warn);
        }));
        _subscriptions.Add(events.Subscribe<TrailReopened>(e => Toast($"{TrailName(e.WayId)} is open again", UiTheme.Good)));
        _subscriptions.Add(events.Subscribe<ParkClosed>(_ => Toast("The park has closed", UiTheme.TextDim)));
        _subscriptions.Add(events.Subscribe<CommandRejected>(e => Toast(e.Reason, UiTheme.Bad)));
        _subscriptions.Add(events.Subscribe<WayBuilt>(e =>
        {
            var way = _ctx.Sim.State.Ways.FirstOrDefault(w => w.Id == e.WayId);
            if (way is null) return; // joined onto a trail at once (that has its own toast)
            Toast(way is { Built: false } ? $"Planned {(way.Kind == WayKind.AccessPath ? "a gravel path" : way.Name)}: the crew will build it" : $"Built {way.Label}", UiTheme.Accent);
        }));
        _subscriptions.Add(events.Subscribe<TrailFeaturePlaced>(e => Toast(FeatureText(e.WayId, e.FeatureId), UiTheme.Accent)));
        _subscriptions.Add(events.Subscribe<JobCompleted>(e => Toast(e.Kind switch
        {
            JobKind.BuildWay => $"{e.Title} is built and open",
            JobKind.BuildFeature => $"Built: {e.Title}",
            JobKind.RepairFeature => $"Repaired: {e.Title.Replace("Repair ", "")}",
            _ => $"Done: {e.Title.ToLowerInvariant().Replace("fell ", "felled ")}",
        }, UiTheme.Good)));
        _subscriptions.Add(events.Subscribe<CrewHired>(e => Toast($"Hired {_ctx.Sim.State.Crew.FirstOrDefault(m => m.Id == e.CrewId)?.Name}", UiTheme.Accent)));
        _subscriptions.Add(events.Subscribe<ToolBought>(e => Toast($"Bought: {_ctx.Sim.State.ToolTypes.FirstOrDefault(t => t.Id == e.ToolId)?.Name}", UiTheme.Accent)));
        _subscriptions.Add(events.Subscribe<WoodBought>(e => Toast($"Bought {e.Amount} wood", UiTheme.Accent)));
        _subscriptions.Add(events.Subscribe<JobQueued>(e =>
        {
            if (e.Kind == JobKind.FellTrees && Jobs.Find(_ctx.Sim.State, e.JobId) is { } job)
                Toast($"Marked {job.Trees.Count} trees for felling", UiTheme.Accent);
        }));
        _subscriptions.Add(events.Subscribe<LiftBuilt>(e => Toast($"Built {_ctx.Sim.State.Lifts.FirstOrDefault(l => l.Id == e.LiftId)?.Name}", UiTheme.Accent)));
        _subscriptions.Add(events.Subscribe<ParkingLotBuilt>(_ => Toast("Built a parking lot", UiTheme.Accent)));
        _subscriptions.Add(events.Subscribe<BikeAccessBooked>(e => Toast(TierText(e.LiftId, e.TierIndex, booked: true), UiTheme.Accent)));
        _subscriptions.Add(events.Subscribe<BikeAccessChanged>(e => Toast(TierText(e.LiftId, e.TierIndex, booked: false), UiTheme.Accent)));
    }

    private void OnCareerSaved(CareerInfo info)
    {
        if (IsInsideTree()) Toast($"Career saved: {info.ParkName}, day {info.Day + 1}", UiTheme.Good);
    }

    /// <summary>Toast about built trails that aren't connected (closed until they are).</summary>
    private void WarnUnconnected()
    {
        var network = _ctx.Sim.Network;
        var loose = _ctx.Sim.State.Ways.Where(w => w.Kind == WayKind.Trail && w.Built && !network.IsConnected(w)).Select(w => w.Name).ToList();
        if (loose.Count > 0)
            Toast($"Not connected, closed until connected: {string.Join(", ", loose)}. Draw a trail from a loose end to a path, trail, platform or station.", UiTheme.Warn);
    }

    private string LiftName(int liftId) => _ctx.Sim.State.Lifts.FirstOrDefault(l => l.Id == liftId)?.Name ?? "The lift";

    private string FeatureTypeName(int wayId, int featureId)
    {
        var state = _ctx.Sim.State;
        var feature = state.Ways.FirstOrDefault(w => w.Id == wayId)?.Features.FirstOrDefault(f => f.Id == featureId);
        return (feature is null ? null : TrailFeatures.FindType(state.TrailFeatureTypes, feature.TypeId)?.Name.ToLowerInvariant()) ?? "feature";
    }

    private string FeatureName(int wayId, int featureId)
    {
        var state = _ctx.Sim.State;
        var way = state.Ways.FirstOrDefault(w => w.Id == wayId);
        var feature = way?.Features.FirstOrDefault(f => f.Id == featureId);
        var type = feature is null ? null : TrailFeatures.FindType(state.TrailFeatureTypes, feature.TypeId);
        return $"{type?.Name ?? "A feature"} on {way?.Name} at {feature?.DistanceCm / 100} m";
    }

    private string FeatureText(int wayId, int featureId)
    {
        var state = _ctx.Sim.State;
        var way = state.Ways.FirstOrDefault(w => w.Id == wayId);
        var feature = way?.Features.FirstOrDefault(f => f.Id == featureId);
        var type = feature is null ? null : TrailFeatures.FindType(state.TrailFeatureTypes, feature.TypeId);
        string verb = feature is { Built: false } ? "Planned" : "Built";
        return $"{verb} a {type?.Name.ToLowerInvariant() ?? "feature"} on {way?.Name} at {feature?.DistanceCm / 100} m";
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
