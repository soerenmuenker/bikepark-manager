using Bikepark.Sim.Core;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// Small pop-up with the followed rider's stats (click a rider or press F): what they are doing, mood and energy, runs,
/// time in the park and cash. ✕ stops following. Pure view: reads the sim.
/// </summary>
internal sealed partial class RiderCard : PanelContainer
{
    private readonly HudContext _ctx;
    private readonly Label _title, _subtitle, _doing, _mood, _energy, _details;
    private readonly ProgressBar _moodBar, _energyBar;

    public RiderCard(HudContext ctx)
    {
        _ctx = ctx;
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, 12, 14, 10, new Color(UiTheme.Accent, 0.4f), 1));

        var box = new VBoxContainer { CustomMinimumSize = new Vector2(270, 0) };
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);

        var titleRow = new HBoxContainer();
        titleRow.AddThemeConstantOverride("separation", 8);
        titleRow.AddChild(new IconView(UiIcon.Follow, 18, UiTheme.Accent) { SizeFlagsVertical = SizeFlags.ShrinkCenter });
        _title = UiTheme.Label("", 15, bold: true);
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _title.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        titleRow.AddChild(_title);
        titleRow.AddChild(new RoundButton(UiIcon.Close, 24, "", "Stop following", () => _ctx.Riders.StopFollowing()) { SizeFlagsVertical = SizeFlags.ShrinkCenter });
        box.AddChild(titleRow);

        _subtitle = UiTheme.Label("", 12, UiTheme.TextDim);
        box.AddChild(_subtitle);
        _doing = UiTheme.Label("", 13);
        _doing.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_doing);
        box.AddChild(Meter("Mood", out _mood, out _moodBar));
        box.AddChild(Meter("Energy", out _energy, out _energyBar));
        box.AddChild(UiTheme.Separator());
        _details = UiTheme.Label("", 12, UiTheme.TextDim);
        box.AddChild(_details);
    }

    private static HBoxContainer Meter(string caption, out Label value, out ProgressBar bar)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var label = UiTheme.Label(caption, 12, UiTheme.TextDim);
        label.CustomMinimumSize = new Vector2(52, 0);
        row.AddChild(label);
        bar = new ProgressBar
        {
            MinValue = 0, MaxValue = 1000, ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 8),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        row.AddChild(bar);
        value = UiTheme.Label("", 12, bold: true);
        value.CustomMinimumSize = new Vector2(40, 0);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(value);
        return row;
    }

    public void Refresh()
    {
        var guest = _ctx.Riders.Followed;
        Visible = guest is not null;
        if (guest is null) return;

        var state = _ctx.Sim.State;
        _title.Text = guest.IsInfluencer ? $"{Bikepark.Sim.Reputation.ReputationMath.InfluencerName(guest.Id)} (influencer)" : $"Rider #{guest.Id}";
        _subtitle.Text = $"{SkillName(guest.Skill)}, skill {guest.Skill / 10} · {guest.Style} rider";
        _doing.Text = Doing(guest);

        SetMeter(_mood, _moodBar, guest.Happiness, UiTheme.MoodColor(guest.Happiness));
        SetMeter(_energy, _energyBar, guest.Energy,
            guest.Energy < state.TrailRules.TiredEnergy * 2 ? UiTheme.Warn : UiTheme.Accent);

        string lastTrail = guest.LastTrailId == 0 ? "" : $" · last: {WayName(guest.LastTrailId)}";
        string lunch = guest.HadLunch ? "had lunch" : guest.LunchMinute >= 0 ? $"lunch from {Clock(guest.LunchMinute)}" : "no lunch planned";
        _details.Text =
            $"Runs: {guest.RunsCompleted}{lastTrail}\n" +
            $"In the park since {Clock(GameTime.MinuteOfDay(guest.ArrivedTick))}, " +
            $"stays until ~{Clock(GameTime.MinuteOfDay(guest.ArrivedTick + guest.PlannedStayMinutes))}\n" +
            $"Cash: {UiTheme.Money(guest.CashCents)} · {lunch}";
    }

    private string Doing(Guest guest)
    {
        var state = _ctx.Sim.State;
        string target = guest.TrailId == 0 ? "" : WayName(guest.TrailId);
        var leg = guest.Route.Count == 0 ? (RouteLeg?)null : guest.Route[Math.Clamp(guest.LegIndex, 0, guest.Route.Count - 1)];
        string LiftName() => leg is { Kind: LegKind.Lift } l ? state.Lifts.FirstOrDefault(x => x.Id == l.WayId)?.Name ?? "the lift" : "the lift";
        string crashPlace = guest.CrashFeatureId != 0
            && state.Ways.SelectMany(w => w.Features).FirstOrDefault(f => f.Id == guest.CrashFeatureId) is { } crashed
            && TrailFeatures.FindType(state.TrailFeatureTypes, crashed.TypeId) is { } crashType
                ? $"at the {crashType.Name.ToLowerInvariant()} on {WayName(guest.CrashWayId)}"
                : guest.CrashCause == Bikepark.Sim.Safety.CrashCause.Collision ? $"in a collision on {WayName(guest.CrashWayId)}"
                : $"on {WayName(guest.CrashWayId)}";
        switch (guest.Activity)
        {
            case RiderActivity.Injured:
                long left = Math.Max(0, guest.RescueAtTick - state.Tick);
                return $"Crashed {crashPlace}, waiting for the rescue helicopter ({left} min)";
            case RiderActivity.Descending or RiderActivity.Climbing when guest.Injury != Bikepark.Sim.Safety.InjurySeverity.None:
                return $"Hurt in a crash {crashPlace}: riding down slowly, then home";
            case RiderActivity.Descending when guest.EntryWaitMs >= 0:
                return $"Waiting to drop into {WayName(leg!.Value.WayId)}";
            case RiderActivity.Descending when leg is { } l && l.WayId != guest.TrailId:
                return $"Riding {WayName(l.WayId)} on the way to {target}";
            case RiderActivity.Descending:
                string fun = guest.RunSegments == 0 ? "" : $" · fun so far {guest.RunFun / guest.RunSegments / 10}%";
                return $"Riding {target}{fun}";
            case RiderActivity.Climbing:
                return $"Pedalling up to {target}";
            case RiderActivity.Walking:
                return $"Walking, heading for {target}";
            case RiderActivity.Queuing:
                return $"In the queue at {LiftName()}, heading for {target}";
            case RiderActivity.OnLift:
                return $"On {LiftName()}, heading for {target}";
            case RiderActivity.Eating:
                return $"Having lunch until {Clock(GameTime.MinuteOfDay(guest.BusyUntilTick))}";
            case RiderActivity.Idle:
                return "Taking a break between laps";
            default:
                return "Looking around";
        }
    }

    private static void SetMeter(Label value, ProgressBar bar, int permille, Color color)
    {
        value.Text = $"{permille / 10}%";
        value.AddThemeColorOverride("font_color", color);
        bar.Value = permille;
        var fill = UiTheme.Box(color, 4, 0, 0);
        fill.ShadowSize = 0;
        bar.AddThemeStyleboxOverride("fill", fill);
    }

    private string WayName(int wayId) => _ctx.Sim.State.Ways.FirstOrDefault(w => w.Id == wayId)?.Name ?? "a trail";

    private static string Clock(long minuteOfDay) => $"{minuteOfDay / 60 % 24:00}:{minuteOfDay % 60:00}";

    /// <summary>Skill named after the trail rating the rider's jersey color shows.</summary>
    private static string SkillName(int skill) => skill switch
    {
        < WayGeometry.GreenMaxDifficulty => "Beginner (green)",
        < WayGeometry.BlueMaxDifficulty => "Intermediate (blue)",
        < WayGeometry.RedMaxDifficulty => "Advanced (red)",
        _ => "Expert (black)",
    };
}
