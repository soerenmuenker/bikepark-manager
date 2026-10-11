using Bikepark.Sim.Core;
using Bikepark.Sim.Safety;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// The "lift stopped" pop-up: raised when a rider being pulled up a T-bar is hit at a trail crossing and the lift stops
/// (<see cref="Bikepark.Sim.Events.LiftStopped"/>). Pauses the game, says why and for how long, and can point the camera
/// at the crossing. Stops of other lifts while it is open wait their turn. Reads the sim only.
/// </summary>
internal sealed partial class LiftStopDialog : PanelContainer
{
    /// <summary>A stop: the lift, until when, the rider hit on it and where (on the trail that crosses the track).</summary>
    public readonly record struct Stop(int LiftId, long UntilTick, int GuestId, int WayId, long Cm);

    private readonly HudContext _ctx;
    private readonly List<Stop> _waiting = [];
    private Stop _stop;
    private readonly Label _title, _text, _status;

    public LiftStopDialog(HudContext ctx)
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
        titleRow.AddChild(new RoundButton(UiIcon.Close, 26, "", "Close", Close) { SizeFlagsVertical = SizeFlags.ShrinkCenter });
        box.AddChild(titleRow);
        _text = UiTheme.Label("", 13);
        _text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_text);
        _status = UiTheme.Label("", 13, UiTheme.Warn, bold: true);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_status);
        var hint = UiTheme.Label("Trails crossing a T-bar track risk collisions with the riders being pulled up: reroute the trail, " +
                                 "or renaturalize the section across the track.", 11, UiTheme.TextDim);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(hint);

        var buttons = new HBoxContainer();
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        buttons.AddChild(UiTheme.Button("Show me", ShowCrossing, "Move the camera to the crossing where it happened"));
        buttons.AddChild(UiTheme.Button("OK", Close));
        box.AddChild(buttons);
    }

    /// <summary>A lift stopped: show it now (pausing the game), or after the stops before it (a later stop of the same lift replaces its earlier one).</summary>
    public void Request(Stop stop)
    {
        if (Visible && _stop.LiftId == stop.LiftId)
        {
            _stop = stop;
            Refresh();
            return;
        }
        _waiting.RemoveAll(s => s.LiftId == stop.LiftId);
        if (Visible)
        {
            _waiting.Add(stop);
            return;
        }
        Open(stop);
    }

    public void Clear()
    {
        _waiting.Clear();
        Visible = false;
    }

    private void Open(Stop stop)
    {
        _stop = stop;
        _ctx.PauseForWarning(this);
        Visible = true;
        Refresh();
    }

    private void Close()
    {
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

    private void ShowCrossing()
    {
        if (_ctx.Sim.Network.TryGetGeometry(_stop.WayId, out var geometry))
        {
            var at = Ways.WayMeshes.ToWorld(geometry.PositionAt(_stop.Cm));
            _ctx.Riders.StopFollowing();
            _ctx.Camera.FocusOn(new Vector2(at.X, at.Z));
        }
        Close();
    }

    /// <summary>Called a few times a second: keeps the status current (running again, helicopter on its way).</summary>
    public void Refresh()
    {
        if (!Visible) return;
        var state = _ctx.Sim.State;
        var lift = state.Lifts.FirstOrDefault(l => l.Id == _stop.LiftId);
        string liftName = lift?.Name ?? "The lift";
        string trail = state.Ways.FirstOrDefault(w => w.Id == _stop.WayId)?.Label ?? "a trail";
        var rider = state.Guests.FirstOrDefault(g => g.Id == _stop.GuestId);
        bool serious = rider?.Injury == InjurySeverity.Serious || rider?.Activity == Bikepark.Sim.State.RiderActivity.Injured;

        _title.Text = $"⚠ {liftName} stopped";
        _text.Text = $"A rider on the T-bar was hit by a rider coming down {trail}, where it crosses the track. " +
                     (serious
                         ? "They are seriously hurt: the lift stays stopped until the rescue helicopter has flown them out."
                         : "They get off the track and go home.") +
                     " Until then nobody boards and the riders on the lift wait where they are.";
        string until = GameTime.Format(_stop.UntilTick)[^5..];
        _status.Text = lift is null ? ""
            : !lift.IsStopped(state.Tick) ? "It is running again."
            : serious ? $"Helicopter expected at about {until}."
            : $"It runs again at {until}.";
        _status.AddThemeColorOverride("font_color", lift is not null && !lift.IsStopped(state.Tick) ? UiTheme.Good : UiTheme.Warn);
    }
}
