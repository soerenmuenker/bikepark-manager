using Bikepark.Sim.Scenarios;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// The scenario's welcome (<see cref="WelcomeNote"/>): shown once when a world is still at tick 0 (a new career), pausing
/// the game. "Show me" points the camera at the end of the note's focus trail. Reads the sim only.
/// </summary>
internal sealed partial class WelcomeDialog : PanelContainer
{
    private readonly HudContext _ctx;
    private readonly Label _title;
    private readonly VBoxContainer _paragraphs;
    private readonly Button _showMe;
    private string? _focusTrail;

    public WelcomeDialog(HudContext ctx)
    {
        _ctx = ctx;
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(UiTheme.Panel, 1f), 12, 20, 16, UiTheme.Accent, 2));

        var box = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
        box.AddThemeConstantOverride("separation", 10);
        AddChild(box);
        var titleRow = new HBoxContainer();
        _title = UiTheme.Label("", 18, UiTheme.Accent, bold: true);
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        titleRow.AddChild(_title);
        titleRow.AddChild(new RoundButton(UiIcon.Close, 26, "", "Close", Close) { SizeFlagsVertical = SizeFlags.ShrinkBegin });
        box.AddChild(titleRow);
        _paragraphs = new VBoxContainer();
        _paragraphs.AddThemeConstantOverride("separation", 8);
        box.AddChild(_paragraphs);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _showMe = UiTheme.Button("Show me", ShowTrail, "Move the camera to where the trail ends");
        buttons.AddChild(_showMe);
        buttons.AddChild(UiTheme.Button("Let's go", Close));
        box.AddChild(buttons);
    }

    /// <summary>Shows the world's welcome if it has one and hasn't started yet (tick 0); otherwise stays hidden.</summary>
    public void OpenIfNew()
    {
        var state = _ctx.Sim.State;
        if (state.Tick != 0 || state.Welcome is not { } note)
        {
            Clear();
            return;
        }
        string park = state.Park.Name;
        _title.Text = WelcomeNote.Fill(note.Title, park);
        foreach (var child in _paragraphs.GetChildren()) child.QueueFree();
        foreach (var paragraph in note.Paragraphs)
        {
            var label = UiTheme.Label(WelcomeNote.Fill(paragraph, park), 13);
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.CustomMinimumSize = new Vector2(520, 0);
            _paragraphs.AddChild(label);
        }
        _focusTrail = note.FocusTrail;
        _showMe.Text = _focusTrail is { } trail ? $"Show me {trail}" : "Show me";
        _showMe.Visible = _focusTrail is not null;
        Visible = true;
        _ctx.PauseForWarning(this);
    }

    public void Clear() => Visible = false;

    private void ShowTrail()
    {
        var state = _ctx.Sim.State;
        var way = state.Ways.FirstOrDefault(w => w.Kind == WayKind.Trail && w.Name == _focusTrail);
        if (way is not null && _ctx.Sim.Network.TryGetGeometry(way.Id, out var geometry))
        {
            var at = Ways.WayMeshes.ToWorld(geometry.PositionAt(geometry.LengthCm));
            _ctx.Riders.StopFollowing();
            _ctx.Camera.FocusOn(new Vector2(at.X, at.Z));
        }
        Close();
    }

    private void Close()
    {
        if (!Visible) return;
        Visible = false;
        _ctx.ResumeAfterWarning(this);
    }
}
