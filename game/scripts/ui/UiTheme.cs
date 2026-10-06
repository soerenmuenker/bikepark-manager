using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// Look of the HUD (in the spirit of SimCity's 2013 interface): dark translucent navy panels with soft corners, a teal
/// accent, white text, round category buttons. All styles are built in code so no theme resource is needed.
/// </summary>
internal static class UiTheme
{
    public static readonly Color Bar = new(0.07f, 0.11f, 0.16f, 0.94f);
    public static readonly Color Panel = new(0.09f, 0.14f, 0.20f, 0.95f);
    public static readonly Color Card = new(0.14f, 0.21f, 0.29f, 1f);
    public static readonly Color CardHover = new(0.18f, 0.28f, 0.38f, 1f);
    public static readonly Color Accent = new(0.25f, 0.78f, 0.85f);
    public static readonly Color AccentDark = new(0.10f, 0.42f, 0.50f);
    public static readonly Color Text = new(0.93f, 0.96f, 0.98f);
    public static readonly Color TextDim = new(0.62f, 0.70f, 0.78f);
    public static readonly Color Good = new(0.45f, 0.85f, 0.40f);
    public static readonly Color Warn = new(0.98f, 0.75f, 0.25f);
    public static readonly Color Bad = new(0.95f, 0.35f, 0.30f);
    public static readonly Color Line = new(1f, 1f, 1f, 0.08f);

    public const float BarHeight = 84f;
    public const float Gap = 10f;

    private static Font? _regular, _bold;

    public static Font Regular => _regular ??= SystemFont(400);
    public static Font Bold => _bold ??= SystemFont(650);

    private static Font SystemFont(int weight) => new SystemFont
    {
        FontNames = ["Avenir Next", "Helvetica Neue", "Segoe UI", "Roboto", "Noto Sans", "Arial"],
        FontWeight = weight,
        Antialiasing = TextServer.FontAntialiasing.Gray,
    };

    public static StyleBoxFlat Box(Color color, float radius = 10f, float padX = 12f, float padY = 10f, Color? border = null, int borderWidth = 0)
    {
        var box = new StyleBoxFlat
        {
            BgColor = color,
            ContentMarginLeft = padX, ContentMarginRight = padX, ContentMarginTop = padY, ContentMarginBottom = padY,
            AntiAliasing = true,
            ShadowColor = new Color(0, 0, 0, 0.25f),
            ShadowSize = color.A > 0.5f ? 6 : 0,
        };
        box.SetCornerRadiusAll((int)radius);
        if (border is { } b)
        {
            box.BorderColor = b;
            box.SetBorderWidthAll(borderWidth);
        }
        return box;
    }

    /// <summary>The theme for every HUD control: fonts, label colours, flat rounded buttons, progress bars.</summary>
    public static Theme Create()
    {
        var theme = new Theme { DefaultFont = Regular, DefaultFontSize = 15 };
        theme.SetColor("font_color", "Label", Text);
        theme.SetColor("font_shadow_color", "Label", new Color(0, 0, 0, 0.35f));

        var normal = Box(Card, 8, 10, 6);
        normal.ShadowSize = 0;
        var hover = Box(CardHover, 8, 10, 6);
        hover.ShadowSize = 0;
        var pressed = Box(AccentDark, 8, 10, 6, Accent, 1);
        pressed.ShadowSize = 0;
        var disabled = Box(new Color(Card, 0.45f), 8, 10, 6);
        disabled.ShadowSize = 0;
        theme.SetStylebox("normal", "Button", normal);
        theme.SetStylebox("hover", "Button", hover);
        theme.SetStylebox("pressed", "Button", pressed);
        theme.SetStylebox("hover_pressed", "Button", pressed);
        theme.SetStylebox("disabled", "Button", disabled);
        theme.SetStylebox("focus", "Button", new StyleBoxEmpty());
        theme.SetColor("font_color", "Button", Text);
        theme.SetColor("font_hover_color", "Button", Colors.White);
        theme.SetColor("font_pressed_color", "Button", Colors.White);
        theme.SetColor("font_disabled_color", "Button", TextDim);

        var track = Box(new Color(1, 1, 1, 0.08f), 4, 0, 0);
        track.ShadowSize = 0;
        var fill = Box(Accent, 4, 0, 0);
        fill.ShadowSize = 0;
        theme.SetStylebox("background", "ProgressBar", track);
        theme.SetStylebox("fill", "ProgressBar", fill);

        var tooltip = Box(new Color(0.04f, 0.07f, 0.10f, 0.97f), 6, 8, 5);
        theme.SetStylebox("panel", "TooltipPanel", tooltip);
        theme.SetColor("font_color", "TooltipLabel", Text);

        theme.SetConstant("separation", "HBoxContainer", 8);
        theme.SetConstant("separation", "VBoxContainer", 6);
        return theme;
    }

    // ---------------------------------------------------------------- small factories

    public static Label Label(string text = "", int size = 15, Color? color = null, bool bold = false)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", size);
        if (bold) label.AddThemeFontOverride("font", Bold);
        if (color is { } c) label.AddThemeColorOverride("font_color", c);
        return label;
    }

    /// <summary>A small grey caption above a value, as in the panels' stat tiles.</summary>
    public static Control StatTile(string caption, out Label value, int valueSize = 20)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 0);
        box.AddChild(Label(caption.ToUpperInvariant(), 11, TextDim, bold: true));
        value = Label("–", valueSize, Text, bold: true);
        box.AddChild(value);
        return box;
    }

    public static Button Button(string text, Action onPressed, string tooltip = "")
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, TooltipText = tooltip };
        button.Pressed += onPressed;
        return button;
    }

    public static HSeparator Separator()
    {
        var separator = new HSeparator();
        separator.AddThemeStyleboxOverride("separator", new StyleBoxLine { Color = Line, Thickness = 1 });
        return separator;
    }

    public static Color MoodColor(int permille) =>
        permille >= 650 ? Good : permille >= 450 ? Warn : Bad;

    public static string Money(long cents) => cents / 100.0 is var euros && Math.Abs(euros) >= 100_000
        ? $"{euros / 1000:N0}k €"
        : $"{euros:N0} €";

    public static string MoneyExact(long cents) => $"{cents / 100.0:N2} €";
}
