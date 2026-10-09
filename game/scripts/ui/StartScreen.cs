using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// The title screen shown before a game starts (only when the game runs without debug arguments): pick the sandbox
/// demo or the Starter Valley career. The demo world waits, paused, behind it.
/// </summary>
public partial class StartScreen : PanelContainer
{
    private readonly SimHost _host;

    public StartScreen() : this(null!) { }

    public StartScreen(SimHost host)
    {
        _host = host;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(LayoutPreset.FullRect);
        AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.04f, 0.07f, 0.10f, 0.82f) });
        if (host is null) return;

        var center = new CenterContainer();
        AddChild(center);
        var column = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        column.AddThemeConstantOverride("separation", 18);
        center.AddChild(column);

        var title = UiTheme.Label("BIKEPARK MANAGER", 44, UiTheme.Text, bold: true);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        column.AddChild(title);
        var subtitle = UiTheme.Label("Choose how to start", 16, UiTheme.TextDim);
        subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        column.AddChild(subtitle);

        var cards = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        cards.AddThemeConstantOverride("separation", 24);
        column.AddChild(cards);
        cards.AddChild(Card("Starter Valley", "Career",
            "Start on the old beginner ski hill at the foot of the mountain: a rusty T-bar, a gravel track and 150,000 €. " +
            "Restore the lift, build trails, earn a reputation and buy more land, up to the gondola and the summit.",
            () => _host.NewGame(SimHost.CareerScenarioFile, demoContent: false)));
        cards.AddChild(Card("Demo", "Sandbox",
            "The whole mountain is yours, the gondola runs with rented bike access and two demo trails with features are " +
            "built. Plenty of money: try everything.",
            () => _host.NewGame(SimHost.DemoScenarioFile, demoContent: true)));
    }

    private static Control Card(string name, string kind, string text, Action onPick)
    {
        var card = new PanelContainer { CustomMinimumSize = new Vector2(340, 250), MouseDefaultCursorShape = CursorShape.PointingHand };
        var normal = UiTheme.Box(UiTheme.Panel, 14, 22, 18, new Color(UiTheme.Accent, 0.35f), 1);
        var hover = UiTheme.Box(UiTheme.CardHover, 14, 22, 18, UiTheme.Accent, 2);
        card.AddThemeStyleboxOverride("panel", normal);
        card.MouseEntered += () => card.AddThemeStyleboxOverride("panel", hover);
        card.MouseExited += () => card.AddThemeStyleboxOverride("panel", normal);
        card.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) onPick();
        };
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 10);
        card.AddChild(box);
        box.AddChild(UiTheme.Label(kind.ToUpperInvariant(), 12, UiTheme.Accent, bold: true));
        box.AddChild(UiTheme.Label(name, 26, bold: true));
        var body = UiTheme.Label(text, 14, UiTheme.TextDim);
        body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.CustomMinimumSize = new Vector2(296, 0);
        box.AddChild(body);
        var start = UiTheme.Button($"Start {name}", onPick);
        start.SizeFlagsVertical = SizeFlags.ExpandFill | SizeFlags.ShrinkEnd;
        box.AddChild(start);
        foreach (var child in box.GetChildren().OfType<Label>()) child.MouseFilter = MouseFilterEnum.Ignore;
        return card;
    }
}
