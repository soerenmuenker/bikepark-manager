using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// The title screen (shown when the game runs without world-setting debug arguments, and after "Back to menu"): start a
/// new Starter Valley career with a park name, continue or delete a saved career, or play the Demo sandbox (never
/// saved). Whatever world was loaded waits, paused, behind it.
/// </summary>
public partial class StartScreen : PanelContainer
{
    private readonly SimHost _host;
    private LineEdit _name = null!;
    private VBoxContainer _careers = null!;

    public StartScreen() : this(null!) { }

    public StartScreen(SimHost host)
    {
        _host = host;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(LayoutPreset.FullRect);
        AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.04f, 0.07f, 0.10f, 0.86f) });
        if (host is null) return;

        var center = new CenterContainer();
        AddChild(center);
        var column = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        column.AddThemeConstantOverride("separation", 18);
        center.AddChild(column);

        var title = UiTheme.Label("BIKEPARK MANAGER", 44, UiTheme.Text, bold: true);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        column.AddChild(title);

        var cards = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        cards.AddThemeConstantOverride("separation", 24);
        column.AddChild(cards);
        cards.AddChild(CareerCard());
        cards.AddChild(DemoCard());
        Refresh();
    }

    // ---------------------------------------------------------------- career

    private Control CareerCard()
    {
        var (card, box) = Card(520);
        box.AddChild(UiTheme.Label("CAREER", 12, UiTheme.Accent, bold: true));
        box.AddChild(UiTheme.Label("Starter Valley", 26, bold: true));
        var text = UiTheme.Label("Start on the old beginner ski hill at the foot of the mountain: a rusty T-bar, a gravel track and " +
                                 "150,000 €. Restore the lift, build trails, earn a reputation and buy more land.", 14, UiTheme.TextDim);
        text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        text.CustomMinimumSize = new Vector2(476, 0);
        box.AddChild(text);

        var start = new HBoxContainer();
        start.AddThemeConstantOverride("separation", 8);
        _name = new LineEdit
        {
            Text = "Old Ski Hill Bikepark",
            PlaceholderText = "Park name",
            MaxLength = Bikepark.Sim.Commands.RenameParkCommand.MaxLength,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 38),
        };
        _name.TextSubmitted += _ => StartCareer();
        start.AddChild(_name);
        start.AddChild(UiTheme.Button("Start new career", StartCareer));
        box.AddChild(start);

        box.AddChild(UiTheme.Separator());
        box.AddChild(UiTheme.Label("SAVED CAREERS — saved automatically when you quit or go back to the menu", 11, UiTheme.TextDim, bold: true));
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(476, 210), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _careers = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _careers.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_careers);
        box.AddChild(scroll);
        return card;
    }

    private void StartCareer()
    {
        string name = _name.Text.Trim();
        _host.NewCareer(name.Length == 0 ? "Old Ski Hill Bikepark" : name);
    }

    /// <summary>Re-reads the saved careers (call when the screen is shown again).</summary>
    public void Refresh()
    {
        if (_host is null) return;
        foreach (var child in _careers.GetChildren()) child.QueueFree();
        var careers = CareerStore.List();
        if (careers.Count == 0)
        {
            _careers.AddChild(UiTheme.Label("No saved careers yet.", 13, UiTheme.TextDim));
            return;
        }
        foreach (var career in careers)
            _careers.AddChild(CareerRow(career));
    }

    private Control CareerRow(CareerInfo career)
    {
        var row = new PanelContainer();
        var style = UiTheme.Box(UiTheme.Card, 8, 10, 6);
        style.ShadowSize = 0;
        row.AddThemeStyleboxOverride("panel", style);
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 8);
        row.AddChild(h);

        var texts = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        texts.AddThemeConstantOverride("separation", 0);
        bool current = career.Id == _host.CurrentCareerId;
        texts.AddChild(UiTheme.Label(career.ParkName + (current ? "  (open)" : ""), 15, bold: true));
        string rating = career.RatingTenths is { } r ? $" · {r / 10}.{r % 10}★" : "";
        texts.AddChild(UiTheme.Label($"Day {career.Day + 1} · level {career.Level}{rating} · {UiTheme.Money(career.MoneyCents)}", 12, UiTheme.TextDim));
        texts.AddChild(UiTheme.Label($"Last played {career.LastPlayedUtc.ToLocalTime():g}", 11, UiTheme.TextDim));
        h.AddChild(texts);

        string id = career.Id;
        var buttons = new HBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        buttons.AddThemeConstantOverride("separation", 6);
        h.AddChild(buttons);
        void ShowNormal()
        {
            foreach (var child in buttons.GetChildren()) child.QueueFree();
            buttons.AddChild(UiTheme.Button(current ? "Resume" : "Continue", () =>
            {
                if (current) _host.ResumeFromMenu(); else _host.ContinueCareer(id);
            }));
            var delete = UiTheme.Button("Delete", ShowConfirm, "Delete this career for good");
            delete.Disabled = current;
            if (current) delete.TooltipText = "It is open right now";
            buttons.AddChild(delete);
        }
        void ShowConfirm()
        {
            foreach (var child in buttons.GetChildren()) child.QueueFree();
            buttons.AddChild(UiTheme.Label("Delete for good?", 12, UiTheme.Bad, bold: true));
            buttons.AddChild(UiTheme.Button("Yes", () =>
            {
                CareerStore.Delete(id);
                Refresh();
            }));
            buttons.AddChild(UiTheme.Button("No", ShowNormal));
        }
        ShowNormal();
        return row;
    }

    // ---------------------------------------------------------------- demo

    private Control DemoCard()
    {
        var (card, box) = Card(340);
        box.AddChild(UiTheme.Label("SANDBOX", 12, UiTheme.Accent, bold: true));
        box.AddChild(UiTheme.Label("Demo", 26, bold: true));
        var text = UiTheme.Label("The whole mountain is yours, the gondola runs with rented bike access and two demo trails with " +
                                 "features are built. Plenty of money: try everything. The Demo is never saved.", 14, UiTheme.TextDim);
        text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        text.CustomMinimumSize = new Vector2(296, 0);
        box.AddChild(text);
        box.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        box.AddChild(UiTheme.Button("Start Demo", () => _host.NewGame(SimHost.DemoScenarioFile, demoContent: true)));
        return card;
    }

    private static (PanelContainer Card, VBoxContainer Box) Card(float width)
    {
        var card = new PanelContainer { CustomMinimumSize = new Vector2(width, 0) };
        card.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, 14, 22, 18, new Color(UiTheme.Accent, 0.35f), 1));
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        card.AddChild(box);
        return (card, box);
    }
}
