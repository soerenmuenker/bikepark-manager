using Godot;

namespace Bikepark.Game.Ui;

internal enum InspectKind { Facility, Lift, Trail, Park }

/// <summary>What an inspector window shows: a building, lift or trail by id, or the park (id 0). The tab to open first, if any.</summary>
internal readonly record struct InspectTarget(InspectKind Kind, int Id, string? Tab = null);

/// <summary>A row of tabs (toggle buttons, one pressed): inspector windows and the Rating menu.</summary>
public partial class TabStrip : HBoxContainer
{
    private readonly ButtonGroup _group = new();
    private readonly List<Button> _buttons = [];

    public event Action<int>? Selected;

    public TabStrip() => AddThemeConstantOverride("separation", 4);

    public int Current { get; private set; }

    public void Add(string name, string tooltip = "")
    {
        int index = _buttons.Count;
        var button = new TipButton
        {
            Text = name, ToggleMode = true, ButtonGroup = _group, FocusMode = FocusModeEnum.None, TooltipText = tooltip,
            ButtonPressed = index == 0,
        };
        button.AddThemeFontSizeOverride("font_size", 13);
        button.Pressed += () => Select(index);
        _buttons.Add(button);
        AddChild(button);
    }

    public void Select(int index)
    {
        if (index < 0 || index >= _buttons.Count) return;
        for (int i = 0; i < _buttons.Count; i++) _buttons[i].SetPressedNoSignal(i == index);
        Current = index;
        Selected?.Invoke(index);
    }

    public int IndexOf(string name) => _buttons.FindIndex(b => string.Equals(b.Text, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A window for one thing in the park (a building, lift, trail or the park itself), in the spirit of RollerCoaster
/// Tycoon's ride windows: a header to drag it by (icon, name, locate, close) and tabs. A tab's content is built when it
/// is shown and rebuilt when <see cref="Signature"/> changes; its live numbers update a few times a second. Pure view:
/// reads the simulation, issues commands.
/// </summary>
internal abstract partial class InspectorWindow : PanelContainer
{
    public const float Width = 480f;
    private const float MaxBodyHeight = 500f;

    protected readonly HudContext Ctx;
    private readonly List<(string Name, Action<VBoxContainer> Build)> _tabs = [];
    private readonly List<Action> _updates = [];
    private TabStrip _strip = null!;
    private VBoxContainer _body = null!;
    private ScrollContainer _scroll = null!;
    private Label _title = null!, _subtitle = null!;
    private IconView _icon = null!;
    private PowerButton? _power;
    private string _signature = "";
    private bool _dragging;

    public InspectTarget Target { get; }

    public event Action<InspectorWindow>? CloseRequested;
    public event Action<InspectorWindow>? Raised;

    protected InspectorWindow(HudContext ctx, InspectTarget target)
    {
        Ctx = ctx;
        Target = target;
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, 12, 14, 12, new Color(UiTheme.Accent, 0.45f), 1));
    }

    /// <summary>Builds the chrome and the tabs (<see cref="AddTab"/> in <see cref="Setup"/>), then shows the first tab (or <paramref name="tab"/>).</summary>
    public void Open(string? tab)
    {
        var root = new VBoxContainer { CustomMinimumSize = new Vector2(Width, 0) };
        root.AddThemeConstantOverride("separation", 8);
        AddChild(root);

        var header = new HBoxContainer { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.Move };
        header.AddThemeConstantOverride("separation", 10);
        header.GuiInput += Drag;
        _icon = new IconView(Icon, 30, UiTheme.Accent) { SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        header.AddChild(_icon);
        var names = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        names.AddThemeConstantOverride("separation", 0);
        _title = UiTheme.Label("", 17, bold: true);
        _title.MouseFilter = MouseFilterEnum.Ignore;
        _title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _title.ClipText = true;
        names.AddChild(_title);
        _subtitle = UiTheme.Label("", 12, UiTheme.TextDim);
        _subtitle.MouseFilter = MouseFilterEnum.Ignore;
        _subtitle.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _subtitle.ClipText = true;
        names.AddChild(_subtitle);
        header.AddChild(names);
        if (IsOpenNow() is { } open)
        {
            _power = new PowerButton(26, () => { if (IsOpenNow() is { } now) SetOpen(!now); }) { SizeFlagsVertical = SizeFlags.ShrinkCenter, On = open };
            header.AddChild(_power);
        }
        if (Location() is not null)
            header.AddChild(new RoundButton(UiIcon.Map, 26, "", "Show it on the map", Locate) { SizeFlagsVertical = SizeFlags.ShrinkCenter });
        header.AddChild(new RoundButton(UiIcon.Close, 26, "", "Close (Esc)", () => CloseRequested?.Invoke(this)) { SizeFlagsVertical = SizeFlags.ShrinkCenter });
        root.AddChild(header);

        Setup();
        _strip = new TabStrip();
        foreach (var (name, _) in _tabs) _strip.Add(name);
        _strip.Selected += _ => Rebuild();
        root.AddChild(_strip);
        root.AddChild(UiTheme.Separator());

        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _body.AddThemeConstantOverride("separation", 8);
        _scroll.AddChild(_body);
        root.AddChild(_scroll);

        ShowTab(tab);
    }

    /// <summary>Switches to a tab by name (ignored when there is none).</summary>
    public void ShowTab(string? tab)
    {
        int index = tab is null ? -1 : _strip.IndexOf(tab);
        if (index >= 0) _strip.Select(index);
        else Rebuild();
    }

    protected abstract UiIcon Icon { get; }

    /// <summary>The thing is gone (torn down, renaturalized): the window closes.</summary>
    public abstract bool IsGone { get; }

    /// <summary>Registers the tabs (<see cref="AddTab"/>).</summary>
    protected abstract void Setup();

    protected abstract string TitleText();
    protected abstract string SubtitleText();

    /// <summary>Where it is (meters, x/z) for the locate button; null: no such button.</summary>
    protected virtual Vector2? Location() => null;

    /// <summary>Open (true) or closed (false) for the power button in the header; null: it can't be closed (no button).</summary>
    protected virtual bool? IsOpenNow() => null;

    /// <summary>The power button was clicked: open or close it (by command).</summary>
    protected virtual void SetOpen(bool open) { }

    /// <summary>What closing or opening does, for the power button's tip.</summary>
    protected virtual string PowerTip(bool open) => open ? "Open · click to close it" : "Closed · click to open it";

    /// <summary>Changes that need the current tab rebuilt (an upgrade bought, a deal signed...).</summary>
    protected virtual string Signature() => "";

    protected void AddTab(string name, Action<VBoxContainer> build) => _tabs.Add((name, build));

    /// <summary>Keeps a value current while its tab shows.</summary>
    protected void Live(Action update)
    {
        _updates.Add(update);
        update();
    }

    private void Rebuild()
    {
        _signature = Signature() + "|" + _strip.Current;
        _updates.Clear();
        foreach (var child in _body.GetChildren())
        {
            _body.RemoveChild(child);
            child.QueueFree();
        }
        if (_tabs.Count > 0) _tabs[_strip.Current].Build(_body);
        Refresh();
    }

    /// <summary>A few times a second while open.</summary>
    public void Refresh()
    {
        if (IsGone) return;
        if (Signature() + "|" + _strip.Current != _signature)
        {
            Rebuild();
            return;
        }
        _title.Text = TitleText();
        _subtitle.Text = SubtitleText();
        if (_power is not null && IsOpenNow() is { } open)
        {
            _power.On = open;
            _power.TooltipText = PowerTip(open);
        }
        foreach (var update in _updates) update();
        float height = Math.Min(MaxBodyHeight, _body.GetCombinedMinimumSize().Y);
        if (!Mathf.IsEqualApprox(_scroll.CustomMinimumSize.Y, height)) _scroll.CustomMinimumSize = new Vector2(0, height);
    }

    private void Locate()
    {
        if (Location() is { } at) Ctx.Camera.FocusOn(at);
    }

    private void Drag(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                _dragging = button.Pressed;
                if (button.Pressed) Raised?.Invoke(this);
                break;
            case InputEventMouseMotion motion when _dragging:
                var view = GetViewportRect().Size;
                Position = new Vector2(Math.Clamp(Position.X + motion.Relative.X, 0, Math.Max(0, view.X - Size.X)),
                    Math.Clamp(Position.Y + motion.Relative.Y, 0, Math.Max(0, view.Y - 60)));
                break;
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true }) Raised?.Invoke(this);
    }

    // ---------------------------------------------------------------- content helpers

    protected static Label Heading(VBoxContainer into, string text)
    {
        var label = UiTheme.Label(text.ToUpperInvariant(), 11, UiTheme.Accent, bold: true);
        into.AddChild(label);
        return label;
    }

    protected static Label Text(VBoxContainer into, string text, int size = 12, Color? color = null)
    {
        var label = UiTheme.Label(text, size, color ?? UiTheme.TextDim);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(Width - 20, 0);
        into.AddChild(label);
        return label;
    }

    /// <summary>A row of stat tiles, each kept current by its function.</summary>
    protected void Tiles(VBoxContainer into, params (string Caption, Func<string> Value)[] tiles)
    {
        var grid = new GridContainer { Columns = Math.Min(4, tiles.Length) };
        grid.AddThemeConstantOverride("h_separation", 22);
        grid.AddThemeConstantOverride("v_separation", 8);
        foreach (var (caption, value) in tiles)
        {
            grid.AddChild(UiTheme.StatTile(caption, out var label, 18));
            Live(() => label.Text = value());
        }
        into.AddChild(grid);
    }

    /// <summary>A price lever: name, −, value, +, and a live detail line under it.</summary>
    protected void Lever(VBoxContainer into, string name, Func<string> value, Action lower, Action raise, string lowerTip, string raiseTip,
        Func<string>? detail = null, string tooltip = "")
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var caption = UiTheme.Label(name, 13, bold: true);
        caption.CustomMinimumSize = new Vector2(120, 0);
        caption.TooltipText = tooltip;
        caption.MouseFilter = tooltip.Length > 0 ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        row.AddChild(caption);
        row.AddChild(UiTheme.Button("−", lower, lowerTip));
        var label = UiTheme.Label("", 16, bold: true);
        label.CustomMinimumSize = new Vector2(84, 0);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        row.AddChild(label);
        row.AddChild(UiTheme.Button("+", raise, raiseTip));
        into.AddChild(row);
        Live(() => label.Text = value());
        if (detail is null) return;
        var line = Text(into, "", 11);
        Live(() => line.Text = detail());
    }

    /// <summary>"Key   value" lines.</summary>
    protected static void Facts(VBoxContainer into, IEnumerable<(string Key, string Value)> facts)
    {
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 3);
        foreach (var (key, value) in facts)
        {
            grid.AddChild(UiTheme.Label(key, 12, UiTheme.TextDim));
            var v = UiTheme.Label(value, 12);
            v.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            v.CustomMinimumSize = new Vector2(300, 0);
            grid.AddChild(v);
        }
        into.AddChild(grid);
    }

    protected static string Signed(long cents) => cents >= 0 ? "+" + UiTheme.Money(cents) : "−" + UiTheme.Money(-cents);
}
