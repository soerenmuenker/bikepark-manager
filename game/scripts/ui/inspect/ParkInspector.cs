using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Events;
using Bikepark.Sim.Reputation;
using Bikepark.Sim.State;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>The park itself: arrivals (entrance fee, parking), drinks for riders, opening hours.</summary>
internal sealed partial class ParkInspector : InspectorWindow
{
    public ParkInspector(HudContext ctx, InspectTarget target) : base(ctx, target) { }

    private WorldState State => Ctx.Sim.State;

    public override bool IsGone => false;

    protected override UiIcon Icon => UiIcon.Land;

    protected override string TitleText() => Bikepark.Sim.Sponsors.SponsorMath.ParkTitle(State);

    protected override string SubtitleText() => $"Day {GameTime.Day(State.Tick) + 1} · {State.Stats.VisitorsToday} visitors today";

    protected override string Signature() => $"{State.ParkingLots.Count}";

    protected override void Setup()
    {
        AddTab("Arrivals", Arrivals);
        if (State.ProductTypes.Any(p => p.Alcohol > 0)) AddTab("Drinks", Drinks);
        AddTab("Info", Info);
    }

    private DayReport? Yesterday => State.DayHistory.Count > 0 ? State.DayHistory[^1] : null;

    private void Arrivals(VBoxContainer body)
    {
        Tiles(body,
            ("Visitors today", () => $"{State.Stats.VisitorsToday}"),
            ("Turned away today", () => $"{State.Stats.TurnedAwayToday}"),
            ("In the park", () => $"{Ctx.Kpi.GuestsInPark}"),
            ("Demand", () => $"{ReputationMath.DemandPermille(State) / 10} %"));
        Heading(body, "Entrance");
        Lever(body, "Entrance fee", () => UiTheme.MoneyExact(State.Park.EntryFeeCents),
            () => Ctx.ChangeFee(-HudContext.FeeStepCents), () => Ctx.ChangeFee(HudContext.FeeStepCents),
            "Lower the entrance fee (more guests come)", "Raise the entrance fee (fewer guests come)",
            () => Yesterday is { } y
                ? $"Yesterday: {y.Visitors} guests" + (y.TurnedAway > 0 ? $", {y.TurnedAway} turned away" : "") + " · dearer = fewer guests, and it counts in Value"
                : "Paid on arrival: dearer = fewer guests, and it counts in their Value rating.");
        if (State.Rules.MaxParkingFeeCents > 0)
        {
            Heading(body, "Parking");
            Lever(body, "Parking fee", () => State.Park.ParkingFeeCents == 0 ? "free" : UiTheme.MoneyExact(State.Park.ParkingFeeCents),
                () => Ctx.ChangeParkingFee(-HudContext.FeeStepCents), () => Ctx.ChangeParkingFee(HudContext.FeeStepCents),
                "Lower the parking fee", "Raise the parking fee (per car, shared by its guests and counted with the entrance fee; campers park free)",
                () => State.ParkingLots.Count == 0 ? "No parking lots: nobody pays it."
                    : $"Today: {UiTheme.Money(State.Finance.ParkingTodayCents)} · per car, shared by its guests and counted with the entrance fee");
        }
        if (State.ParkingLots.Count == 0) return;
        var lots = Text(body, "", 12);
        Live(() =>
        {
            int? capacity = LiftMath.ParkingCapacity(State);
            int dayGuests = LiftMath.DayGuests(State);
            lots.Text = string.Join("\n", State.ParkingLots.Select(l => $"{l.Name}: {l.Spaces} spaces")) +
                        (capacity is { } c ? $"\nCars' guests now: {dayGuests} of {c} (when full, new guests are turned away)" : "");
        });
    }

    private void Drinks(VBoxContainer body)
    {
        Text(body, "When bars serve alcohol to riders. Tipsy riders pick harder trails and crash more; after their last lap guests may always stay for après-bike.", 12);
        var row = new HBoxContainer();
        row.AddChild(UiTheme.Label("Beer for riders", 13, bold: true));
        var button = UiTheme.Button("", CycleBeerHours, "All day → from 15:00 → après-bike only → never");
        row.AddChild(button);
        body.AddChild(row);
        Live(() => button.Text = FacilityInspector.BeerHours(State, State.Park.BeerFromMinute));
        var stats = Text(body, "", 12);
        Live(() => stats.Text = $"Crashes after a drink: {State.Safety.CrashesUnderInfluence} · après-bike guests: {State.Stats.TotalApresGuests}");
    }

    private void CycleBeerHours()
    {
        int[] options = [0, 15 * GameTime.MinutesPerHour, State.Rules.CloseMinute, GameTime.MinutesPerDay];
        int index = Array.IndexOf(options, State.Park.BeerFromMinute);
        Ctx.Host.Enqueue(new SetBeerHoursCommand(options[(index + 1) % options.Length]));
    }

    private void Info(VBoxContainer body)
    {
        var rules = State.Rules;
        static string Clock(int minute) => $"{minute / 60:00}:{minute % 60:00}";
        var facts = new List<(string, string)>
        {
            ("Open", $"{Clock(rules.OpenMinute)}–{Clock(rules.CloseMinute)} for guests, lifts and the crew"),
        };
        if (State.FacilityRules.ApresEndMinute > 0) facts.Add(("Après-bike", $"until {Clock(State.FacilityRules.ApresEndMinute)}"));
        if (rules.HasLunch) facts.Add(("Lunch", $"{Clock(rules.LunchStartMinute)}–{Clock(rules.LunchEndMinute)}"));
        facts.Add(("Lift pass", State.Park.LiftTicketCents == 0 ? "free lifts" : $"{UiTheme.MoneyExact(State.Park.LiftTicketCents)} (set it in any lift's window)"));
        facts.Add(("Prices", "Click a bar or shop: its window sets the price of what it sells (one price for the whole park)"));
        Facts(body, facts);
    }
}
