using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// A pausing pop-up for money trouble: a missed loan installment, a warning that the park is in the red, and the
/// bankruptcy itself (final: it only leads back to the start screen). Later notices wait their turn. Reads the sim only.
/// </summary>
internal sealed partial class NoticeDialog : PanelContainer
{
    public readonly record struct Notice(string Title, string Text, bool Final = false);

    private readonly HudContext _ctx;
    private readonly List<Notice> _waiting = [];
    private readonly Label _title, _text;
    private readonly HBoxContainer _buttons;
    private readonly RoundButton _close;
    private bool _final;

    public NoticeDialog(HudContext ctx)
    {
        _ctx = ctx;
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(UiTheme.Panel, 1f), 12, 16, 12, UiTheme.Bad, 2));

        var box = new VBoxContainer { CustomMinimumSize = new Vector2(430, 0) };
        box.AddThemeConstantOverride("separation", 8);
        AddChild(box);
        var titleRow = new HBoxContainer();
        _title = UiTheme.Label("", 16, UiTheme.Bad, bold: true);
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        titleRow.AddChild(_title);
        _close = new RoundButton(UiIcon.Close, 26, "", "Close", Close) { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        titleRow.AddChild(_close);
        box.AddChild(titleRow);
        _text = UiTheme.Label("", 13);
        _text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_text);
        _buttons = new HBoxContainer();
        box.AddChild(_buttons);
    }

    /// <summary>Shows the notice now (pausing the game) or after the ones before it; a final one replaces them all.</summary>
    public void Request(Notice notice)
    {
        if (_final) return;
        if (notice.Final)
        {
            _waiting.Clear();
            Open(notice);
            return;
        }
        if (Visible) _waiting.Add(notice);
        else Open(notice);
    }

    public void Clear()
    {
        _waiting.Clear();
        _final = false;
        Visible = false;
    }

    private void Open(Notice notice)
    {
        _final = notice.Final;
        _title.Text = notice.Title;
        _text.Text = notice.Text;
        _close.Visible = !notice.Final;
        foreach (var child in _buttons.GetChildren()) child.QueueFree();
        _buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        if (notice.Final)
        {
            _buttons.AddChild(UiTheme.Button("Back to the menu", () => _ctx.Host.BackToMenu(), "The career is over: start a new one"));
            _ctx.Host.SetSpeedIndex(0);
        }
        else
        {
            _buttons.AddChild(UiTheme.Button("Finances", () =>
            {
                Close();
                _ctx.OpenMenu(Hud.Menu.Finance);
            }, "Open the Finances menu (loans)"));
            _buttons.AddChild(UiTheme.Button("OK", Close));
            _ctx.PauseForWarning(this);
        }
        Visible = true;
    }

    private void Close()
    {
        if (_final) return;
        Visible = false;
        if (_waiting.Count > 0)
        {
            var next = _waiting[0];
            _waiting.RemoveAt(0);
            Open(next);
            return;
        }
        _ctx.ResumeAfterWarning(this);
    }
}
