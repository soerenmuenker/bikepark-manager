using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// A small modal pop-up asking for a name (a new trail, renaming one): a text field pre-filled with a suggestion, OK and
/// Cancel. Enter confirms, Esc cancels; the map behind it can't be clicked while it is open. The name is checked by the
/// caller's validator (the same rules as the command) before the pop-up closes.
/// </summary>
internal sealed partial class NameDialog : Control
{
    private readonly Label _title;
    private readonly Label _hint;
    private readonly Label _error;
    private readonly LineEdit _name;
    private Func<string, string?> _validate = _ => null;
    private Action<string>? _ok;
    private Action? _cancel;

    public NameDialog()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.25f), MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 });
        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 };
        AddChild(center);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(UiTheme.Panel, 1f), 12, 18, 14, UiTheme.Accent, 2));
        center.AddChild(panel);
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(380, 0) };
        box.AddThemeConstantOverride("separation", 8);
        panel.AddChild(box);

        _title = UiTheme.Label("", 16, UiTheme.Accent, bold: true);
        box.AddChild(_title);
        _hint = UiTheme.Label("", 12, UiTheme.TextDim);
        _hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_hint);
        _name = new LineEdit { MaxLength = Bikepark.Sim.Commands.BuildWayCommand.MaxNameLength, CustomMinimumSize = new Vector2(0, 36) };
        _name.TextSubmitted += _ => Confirm();
        _name.TextChanged += _ => _error!.Visible = false;
        box.AddChild(_name);
        _error = UiTheme.Label("", 12, UiTheme.Bad);
        _error.Visible = false;
        box.AddChild(_error);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        buttons.AddChild(UiTheme.Button("Cancel", Cancel, "Esc"));
        buttons.AddChild(UiTheme.Button("OK", Confirm, "Enter"));
        box.AddChild(buttons);
    }

    /// <summary>Shows the pop-up. <paramref name="ok"/> gets the trimmed name once <paramref name="validate"/> accepts it.</summary>
    public void Ask(string title, string hint, string suggestion, Func<string, string?> validate, Action<string> ok, Action? cancel = null)
    {
        _title.Text = title;
        _hint.Text = hint;
        _hint.Visible = hint.Length > 0;
        _error.Visible = false;
        _validate = validate;
        _ok = ok;
        _cancel = cancel;
        _name.Text = suggestion;
        Visible = true;
        GetParent()?.MoveChild(this, -1);
        _name.CallDeferred(Control.MethodName.GrabFocus);
        _name.CallDeferred(LineEdit.MethodName.SelectAll);
    }

    public override void _Input(InputEvent @event)
    {
        if (!Visible) return;
        if (@event is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Escape })
        {
            Cancel();
            GetViewport().SetInputAsHandled();
        }
    }

    private void Confirm()
    {
        string name = _name.Text.Trim();
        if (_validate(name) is { } problem)
        {
            _error.Text = problem;
            _error.Visible = true;
            return;
        }
        var ok = _ok;
        Close();
        ok?.Invoke(name);
    }

    private void Cancel()
    {
        var cancel = _cancel;
        Close();
        cancel?.Invoke();
    }

    private void Close()
    {
        Visible = false;
        _ok = null;
        _cancel = null;
        _name.ReleaseFocus();
    }
}
