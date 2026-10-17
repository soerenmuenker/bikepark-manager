using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Reputation;
using Bikepark.Sim.State;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>A lift: queue and status, bike access, the lift pass and its money, what its riders say, what it is.</summary>
internal sealed partial class LiftInspector : InspectorWindow
{
    public LiftInspector(HudContext ctx, InspectTarget target) : base(ctx, target) { }

    private WorldState State => Ctx.Sim.State;
    private Lift? Lift => State.Lifts.FirstOrDefault(l => l.Id == Target.Id);
    private LiftType? Type => Lift is { } l ? LiftNetwork.FindType(State, l.TypeId) : null;

    public override bool IsGone => Lift is null || Type is null;

    protected override UiIcon Icon => UiIcon.Lift;

    protected override string TitleText() => Lift?.Name ?? "";

    protected override string SubtitleText()
    {
        if (Lift is not { } l || Type is not { } type) return "";
        var op = LiftNetwork.FindOperator(State, l.OperatorId);
        return $"{type.Name} · run by {op?.Name ?? "the park"}";
    }

    protected override Vector2? Location() =>
        Lift is { } l && State.TerrainEdits.FirstOrDefault(e => e.Id == l.Valley.TerrainEditId) is { } edit
            ? new Vector2(edit.Pad.CenterX / 100f, edit.Pad.CenterZ / 100f)
            : null;

    protected override string Signature() =>
        Lift is { } l ? $"{l.Derelict}|{l.ReadyTick}|{l.BikeAccess?.TierIndex}|{l.BikeAccess?.PendingTierIndex}|{LiftWorks.StationsOwned(State, l)}|{State.Reputation.TotalReviews / 10}" : "";

    protected override void Setup()
    {
        AddTab("Overview", Overview);
        if (Lift!.BikeAccess is not null && LiftNetwork.FindOperator(State, Lift.OperatorId) is not null) AddTab("Bike access", BikeAccessTab);
        AddTab("Finances", Finances);
        if (State.ReputationRules.Enabled) AddTab("Reviews", Reviews);
        AddTab("Info", Info);
    }

    private void Overview(VBoxContainer body)
    {
        var lift = Lift!;
        var type = Type!;
        int liftId = lift.Id;
        if (lift.ReadyTick > 0)
        {
            Text(body, $"{(lift.Derelict ? "Being restored" : "Under construction")}: running from day {GameTime.Day(lift.ReadyTick) + 1}, " +
                       $"{GameTime.Format(lift.ReadyTick)[^5..]}.", 13, UiTheme.Warn);
            return;
        }
        if (lift.Derelict)
        {
            Text(body, "Rusty and out of service. Riders pedal up instead.", 13, UiTheme.Warn);
            string? why = !LiftWorks.StationsOwned(State, lift) ? "It stands on land you don't own."
                : State.Finance.MoneyCents < type.RestoreCostCents ? "Not enough money." : null;
            var restore = UiTheme.Button($"Restore ({UiTheme.Money(type.RestoreCostCents)}, {type.RestoreDays} days)",
                () => Ctx.Host.Enqueue(new RestoreLiftCommand(liftId)),
                why ?? $"A contractor fixes it for half the price of a new {type.Name}; it runs from the next opening after the work");
            restore.Disabled = why is not null;
            body.AddChild(restore);
            return;
        }
        var stopped = Text(body, "", 13, UiTheme.Bad);
        Live(() =>
        {
            bool isStopped = Lift is { } l && l.IsStopped(State.Tick);
            stopped.Visible = isStopped;
            if (isStopped)
                stopped.Text = $"Stopped: a rider crashed on the track. Running again at {GameTime.Format(Lift!.StoppedUntilTick)[^5..]}.";
        });
        Tiles(body,
            ("Queue", () => $"{Lift?.Queue.Count ?? 0}"),
            ("Wait now", () =>
            {
                if (Lift is not { } l) return "–";
                int minutes = LiftMath.ExpectedWaitMinutes(type, l.BikeCarrierPermille, l.Queue.Count);
                return l.IsStopped(State.Tick) ? "stopped" : minutes < 0 ? "no bikes" : minutes == 0 ? "none" : $"~{minutes} min";
            }),
            ("Bike capacity", () => $"{LiftMath.BikeRidersPerHour(type, Lift?.BikeCarrierPermille ?? 0)}/h"),
            ("Rides today", () => $"{Lift?.Stats.RidersToday ?? 0}"),
            ("Avg wait", () => Lift is { Stats.Riders: > 0 } l ? $"{(double)l.Stats.SumWaitMinutes / l.Stats.Riders:F1} min" : "–"),
            ("Longest queue today", () => $"{Lift?.Stats.MaxQueueToday ?? 0}"),
            ("Rides total", () => $"{Lift?.Stats.Riders ?? 0}"));
        var bar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 8) };
        body.AddChild(bar);
        Live(() =>
        {
            if (Lift is not { } l) return;
            bar.MaxValue = Math.Max(60, l.Stats.MaxQueue);
            bar.Value = l.Queue.Count;
        });
        foreach (var lot in State.ParkingLots.Where(p => p.LiftId == liftId))
            Text(body, $"Parking: {lot.Name}, {lot.Spaces} spaces (≈ {lot.Spaces * State.LiftRules.GuestsPerCarPermille / 1000} guests)", 12);
    }

    private void BikeAccessTab(VBoxContainer body)
    {
        var lift = Lift!;
        var op = LiftNetwork.FindOperator(State, lift.OperatorId)!;
        var access = lift.BikeAccess!;
        int liftId = lift.Id;
        Text(body, $"Rented from {op.Name}: how many cabins carry bikes. Changes start at the next opening.  [ / ]", 12);
        bool owned = LiftWorks.StationsOwned(State, lift);
        if (!owned)
            Text(body, $"{op.Name} only rents you bike access once you own the land at both stations (Land menu).", 12, UiTheme.Warn);
        var tiers = new HFlowContainer();
        tiers.AddThemeConstantOverride("h_separation", 8);
        tiers.AddThemeConstantOverride("v_separation", 8);
        for (int i = 0; i < op.BikeAccessTiers.Count; i++)
        {
            var tier = op.BikeAccessTiers[i];
            int index = i;
            string mark = i == access.TierIndex ? "  ● now" : i == access.PendingTierIndex ? "  ◌ booked" : "";
            var button = new TipButton
            {
                Text = $"{tier.Name}{mark}\n{tier.BikeCarrierPermille / 10}% of cabins · {UiTheme.Money(tier.DailyFeeCents)}/day",
                ToggleMode = true,
                ButtonPressed = i == (access.PendingTierIndex ?? access.TierIndex),
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(150, 48),
                TooltipText = i == access.TierIndex ? "In effect today" : "Book from the next opening",
            };
            button.AddThemeFontSizeOverride("font_size", 12);
            button.Pressed += () => Ctx.Host.Enqueue(new SetLiftBikeAccessCommand(liftId, index));
            button.Disabled = !owned && i > 0;
            tiers.AddChild(button);
        }
        body.AddChild(tiers);
    }

    private void Finances(VBoxContainer body)
    {
        var type = Type!;
        var op = LiftNetwork.FindOperator(State, Lift!.OperatorId);
        Heading(body, "Lift pass — one day pass for all lifts");
        Lever(body, "Lift pass",
            () => State.Park.LiftTicketCents == 0 ? "free" : UiTheme.MoneyExact(State.Park.LiftTicketCents),
            () => Ctx.ChangeLiftTicket(-HudContext.FeeStepCents), () => Ctx.ChangeLiftTicket(HudContext.FeeStepCents),
            "Lower the lift day pass",
            "Raise the lift day pass (paid once per visit by guests who use a lift; too dear and they pedal instead)",
            () => State.Park.LiftTicketCents == 0 ? "Lifts are free to use."
                : $"Park-wide today: {UiTheme.Money(State.Finance.LiftTicketsTodayCents)} · bought once per visit, at the first lift" +
                  (Lift is { Stats.NoPassToday: > 0 } l ? $" · {l.Stats.NoPassToday} riders here couldn't pay it today and pedalled" : ""));
        Tiles(body,
            ("Passes sold here today", () => $"{Lift?.Stats.PassesSoldToday ?? 0}"),
            ("Pass money today", () => UiTheme.Money(Lift?.Stats.PassRevenueTodayCents ?? 0)),
            ("Passes total", () => $"{Lift?.Stats.PassesSold ?? 0}"),
            ("Pass money total", () => UiTheme.Money(Lift?.Stats.PassRevenueCents ?? 0)));
        Heading(body, "Costs");
        if (op is null)
            Facts(body, [("Upkeep", $"{UiTheme.Money(type.UpkeepPerDayCents)} a day (the park owns it)")]);
        else if (Lift!.BikeAccess is { } access && access.TierIndex < op.BikeAccessTiers.Count)
            Facts(body, [("Bike access", $"{UiTheme.Money(op.BikeAccessTiers[access.TierIndex].DailyFeeCents)} a day to {op.Name} ({op.BikeAccessTiers[access.TierIndex].Name})")]);
    }

    private void Reviews(VBoxContainer body)
    {
        int id = Target.Id;
        var mentioning = ReviewMath.Mentioning(State.Reputation, liftId: id).ToList();
        var stars = ReviewMath.AverageStars(mentioning);
        Heading(body, $"Guests who rode it · {mentioning.Count} reviews");
        body.AddChild(UiTheme.Label(stars is { } s ? $"{Aspects.Glyphs(s)}  {s / 10.0:0.0} from their whole visit" : "No reviews from its riders yet.", 15,
            stars is { } c ? UiTheme.MoodColor((c - 10) * 25) : UiTheme.TextDim, bold: true));
        body.AddChild(Aspects.Bars(mentioning));
        Text(body, "Long queues here lower Smoothness for everyone who rode it; the lift pass counts in Value.", 11);
        var browser = new ReviewBrowser(Ctx, r => r.LiftIds.Contains(id), pageSize: 5, width: Width - 20);
        body.AddChild(browser);
        Live(browser.Refresh);
    }

    private void Info(VBoxContainer body)
    {
        var type = Type!;
        Facts(body,
        [
            ("Type", type.Name),
            ("Carriers", $"{type.BikesPerCarrier} bike{(type.BikesPerCarrier == 1 ? "" : "s")} every {type.IntervalSeconds} s ({LiftMath.BikeRidersPerHour(type, 1000)} riders/h with every carrier for bikes)"),
            ("Speed", $"{type.SpeedCmPerS / 100.0:0.#} m/s"),
            ("Weather", type.Sheltered ? "Closed cabins: riders stay dry" : "Open: riders get wet in the rain"),
            ("Build price", $"{UiTheme.Money(type.BuildCostCents)} ({type.BuildDays} days)"),
        ]);
        Text(body, "Riders take a lift when walking to it, queuing and riding up is cheaper for them than pedalling up the gravel paths.", 11);
    }
}
