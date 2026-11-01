using Bikepark.Sim.Crew;
using Bikepark.Sim.Trails;
using Bikepark.Game.Ways;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// Asks how a trail being drawn gets across a rock: one button per wooden feature (with its rating and cost, disabled with the
/// reason when it doesn't fit), or back to drawing. Only asks: the trail tool does the rest.
/// </summary>
internal sealed partial class RockCrossingDialog : CenterContainer
{
    private readonly Label _title;
    private readonly VBoxContainer _options;
    private Action? _cancel;

    public RockCrossingDialog()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore; // at the top: the rock in question stays in view
        SetAnchorsPreset(LayoutPreset.TopWide);
        OffsetTop = 124;
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(460, 0) };
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(UiTheme.Panel, 1f), 12, 16, 12, UiTheme.Accent, 2));
        AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        panel.AddChild(box);
        _title = UiTheme.Label("", 15, UiTheme.Accent, bold: true);
        _title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_title);
        _options = new VBoxContainer();
        _options.AddThemeConstantOverride("separation", 6);
        box.AddChild(_options);
        box.AddChild(UiTheme.Button("Keep drawing", () =>
        {
            Visible = false;
            _cancel?.Invoke();
        }));
    }

    public void Ask(string title, IReadOnlyList<WayToolRockOption> options, Action cancel)
    {
        _title.Text = title;
        _cancel = cancel;
        foreach (var child in _options.GetChildren()) child.QueueFree();
        foreach (var option in options)
        {
            var (type, length) = (option.Type, option.LengthCm);
            string cost = $"{type.Rating} · {WorkCosts.FeatureWood(type, length)} wood · {HudContext.CrewTime(WorkCosts.FeatureMinutes(type, length))}";
            var pick = option.Pick;
            var button = UiTheme.Button($"{type.Name} ({cost})", () =>
            {
                Visible = false;
                pick();
            }, option.Reason ?? "");
            button.Disabled = option.Reason is not null;
            _options.AddChild(button);
            if (option.Reason is { } reason)
            {
                var why = UiTheme.Label(reason, 11, UiTheme.TextDim);
                why.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                _options.AddChild(why);
            }
        }
        Visible = true;
    }
}
