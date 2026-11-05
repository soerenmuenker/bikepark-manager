using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Reputation;
using Bikepark.Sim.Staff;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// Park menu: the park as a whole. Its level (XP, where it comes from, what the next level unlocks), arrivals (entrance fee, lift pass, parking fee and lots, demand), drinks for riders
/// (beer hours), a summary of the park staff (managed in Crew → Park staff) and the opening hours. Opened from the bar,
/// by clicking the park name or a parking lot.
/// </summary>
public partial class ParkPanel : HudPanel
{
    private const float ContentWidth = 620;

    private readonly List<Action> _updates = [];
    private VBoxContainer _parking = null!, _drinks = null!, _staff = null!;
    private Label _lots = null!, _facts = null!, _staffLines = null!;
    private Button _beer = null!;
    private Label _xpText = null!, _xpParts = null!, _nextUnlocks = null!;
    private ProgressBar _xpBar = null!;

    public override string Title => "Park";

    protected override void Build()
    {
        var page = new VBoxContainer { CustomMinimumSize = new Vector2(ContentWidth, 0) };
        page.AddThemeConstantOverride("separation", 8);
        Body.AddChild(page);

        Tiles(page,
            ("Visitors today", () => $"{State.Stats.VisitorsToday}"),
            ("Turned away today", () => $"{State.Stats.TurnedAwayToday}"),
            ("In the park", () => $"{Ctx.Kpi.GuestsInPark}"),
            ("Demand", () => $"{ReputationMath.DemandPermille(State) * StaffMath.MarketingPermille(State) / 10_000} %"));

        Heading(page, "Park level");
        _xpText = UiTheme.Label("", 14, bold: true);
        page.AddChild(_xpText);
        _xpBar = new ProgressBar { ShowPercentage = false, CustomMinimumSize = new Vector2(ContentWidth, 10), MaxValue = 1000 };
        page.AddChild(_xpBar);
        _xpParts = Text(page, "");
        _nextUnlocks = Text(page, "", 12, UiTheme.Text);

        Heading(page, "Entrance");
        Lever(page, "Entrance fee", () => UiTheme.MoneyExact(State.Park.EntryFeeCents),
            () => Ctx.ChangeFee(-HudContext.FeeStepCents), () => Ctx.ChangeFee(HudContext.FeeStepCents),
            "Lower the entrance fee (more guests come)", "Raise the entrance fee (fewer guests come)",
            () => Yesterday is { } y
                ? $"Yesterday: {y.Visitors} guests" + (y.TurnedAway > 0 ? $", {y.TurnedAway} turned away" : "") + " · dearer = fewer guests, and it counts in Value"
                : "Paid on arrival: dearer = fewer guests, and it counts in their Value rating.");
        Lever(page, "Lift pass", () => State.Park.LiftTicketCents == 0 ? "free" : UiTheme.MoneyExact(State.Park.LiftTicketCents),
            () => Ctx.ChangeLiftTicket(-HudContext.FeeStepCents), () => Ctx.ChangeLiftTicket(HudContext.FeeStepCents),
            "Lower the lift day pass", "Raise the lift day pass",
            () => State.Lifts.Count == 0 ? "No lifts yet: riders pedal up."
                : $"A day pass, bought when a guest first takes a lift · today {UiTheme.Money(State.Lifts.Sum(l => l.Stats.PassRevenueCents))}");

        _parking = Section(page);
        Heading(_parking, "Parking");
        Lever(_parking, "Parking fee", () => State.Park.ParkingFeeCents == 0 ? "free" : UiTheme.MoneyExact(State.Park.ParkingFeeCents),
            () => Ctx.ChangeParkingFee(-HudContext.FeeStepCents), () => Ctx.ChangeParkingFee(HudContext.FeeStepCents),
            "Lower the parking fee", "Raise the parking fee (per car, shared by its guests and counted with the entrance fee; campers park free)",
            () => State.ParkingLots.Count == 0 ? "No parking lots: nobody pays it."
                : $"Today: {UiTheme.Money(State.Finance.ParkingTodayCents)} · per car, shared by its guests and counted with the entrance fee");
        _lots = Text(_parking, "");

        _drinks = Section(page);
        Heading(_drinks, "Drinks for riders");
        Text(_drinks, "When bars serve alcohol to riders. Tipsy riders pick harder trails and crash more; after their last lap guests may always stay for après-bike.");
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(UiTheme.Label("Beer for riders", 13, bold: true));
        _beer = UiTheme.Button("", CycleBeerHours, "All day → from 15:00 → après-bike only → never");
        row.AddChild(_beer);
        _drinks.AddChild(row);
        var drinkStats = Text(_drinks, "");
        _updates.Add(() =>
        {
            _beer.Text = FacilityInspector.BeerHours(State, State.Park.BeerFromMinute);
            drinkStats.Text = $"Crashes after a drink: {State.Safety.CrashesUnderInfluence} · après-bike guests: {State.Stats.TotalApresGuests}";
        });

        _staff = Section(page);
        Heading(_staff, "Park staff");
        _staffLines = Text(_staff, "", 12, UiTheme.Text);
        var manage = Row(8);
        manage.AddChild(UiTheme.Button("Manage staff…", () => Ctx.ShowMenu(Hud.Menu.Crew, "staff"), "Crew menu → Park staff: hire and let go by role"));
        _staff.AddChild(manage);

        Heading(page, "Opening hours");
        _facts = Text(page, "");
    }

    private WorldState State => Ctx.Sim.State;

    private DayReport? Yesterday => State.DayHistory.Count > 0 ? State.DayHistory[^1] : null;

    public override void Refresh()
    {
        var state = State;
        RefreshLevel(state);
        _parking.Visible = state.Rules.MaxParkingFeeCents > 0;
        int? capacity = LiftMath.ParkingCapacity(state);
        _lots.Text = state.ParkingLots.Count == 0 ? "" :
            string.Join("\n", state.ParkingLots.Select(l => $"{l.Name}: {l.Spaces} spaces")) +
            (capacity is { } c ? $"\nCars' guests now: {LiftMath.DayGuests(state)} of {c} (when full, new guests are turned away)" : "");
        _drinks.Visible = state.ProductTypes.Any(p => p.Alcohol > 0);

        _staff.Visible = true;
        _staffLines.Text = state.StaffRules.Enabled
            ? string.Join("\n", state.StaffRoles.Select(r => $"{r.Name} ×{StaffMath.Count(state, r.Id)}: {CrewPanel.EffectText(state, r.Effect)}")) +
              $"\n{UiTheme.Money(StaffMath.WagesPerDay(state))} per day"
            : $"A fixed base staff (ticket office, first aid, cleaning): {UiTheme.Money(state.Rules.DailyUpkeepCents)} per day.";

        var rules = state.Rules;
        static string Clock(int minute) => $"{minute / 60:00}:{minute % 60:00}";
        var facts = new List<string> { $"Open {Clock(rules.OpenMinute)}–{Clock(rules.CloseMinute)} for guests, lifts and the crew" };
        if (state.FacilityRules.ApresEndMinute > 0) facts.Add($"Après-bike until {Clock(state.FacilityRules.ApresEndMinute)}");
        if (rules.HasLunch) facts.Add($"Lunch {Clock(rules.LunchStartMinute)}–{Clock(rules.LunchEndMinute)}");
        facts.Add("Prices of food, drinks and merch: click a bar or shop (one price for the whole park)");
        _facts.Text = string.Join("\n", facts);

        foreach (var update in _updates) update();
    }

    /// <summary>XP towards the next level, where it comes from and what the next level brings.</summary>
    private void RefreshLevel(WorldState state)
    {
        var rules = state.ReputationRules;
        var xp = ParkProgress.Xp(state, Ctx.Sim.Network);
        int level = ParkProgress.Level(rules, xp.Total);
        int floor = level == 0 ? 0 : rules.LevelXp[level - 1];
        int? next = ParkProgress.NextLevelXp(rules, level);
        _xpBar.Value = next is { } n ? (xp.Total - floor) * 1000.0 / Math.Max(1, n - floor) : 1000;
        _xpText.Text = rules.LevelXp.Count == 0 ? $"{xp.Total} XP"
            : next is { } to ? $"Level {level} of {ReputationRules.MaxLevel} · {xp.Total} XP · {to - xp.Total} XP to level {level + 1}"
            : $"Level {level} (top level) · {xp.Total} XP";
        static string PerColour(Func<int, string> value) => $"blue {value(0)} · red {value(1)} · black {value(2)}";
        _xpParts.Text = $"Trails {PerColour(r => $"{xp.TrailMeters[r] / 1000.0:0.0} km")}  +{xp.TrailXp} XP\n" +
                        $"Features {PerColour(r => $"{xp.Features[r]}")}  +{xp.FeatureXp} XP\n" +
                        $"Buildings {xp.Buildings}, upgrades {xp.Upgrades}  +{xp.BuildingXp} XP\n" +
                        $"Own lifts running {xp.Lifts}  +{xp.LiftXp} XP\n" +
                        $"Visitors {xp.AverageVisitors}/day (last {rules.VisitorDays} days)  +{xp.VisitorXp} XP\n" +
                        $"XP per km: {PerColour(r => $"{rules.XpPerKmByRating.For((TrailRating)r)}")} · per feature: " +
                        PerColour(r => $"{rules.XpPerFeatureByRating.For((TrailRating)r)}") +
                        $" · per building {rules.XpPerBuilding}, upgrade {rules.XpPerUpgrade}, own lift {rules.XpPerLift}, visitor {rules.XpPerVisitor}";
        if (state.Rules.UnlockAll)
            _nextUnlocks.Text = "Sandbox: everything is unlocked.";
        else if (next is null)
            _nextUnlocks.Text = "";
        else
        {
            var groups = ParkProgress.UnlockGroupsAt(state, level + 1);
            _nextUnlocks.Text = groups.Count == 0 ? $"Level {level + 1} brings nothing new, but later levels do."
                : $"Level {level + 1} unlocks: " + string.Join(" · ", groups.Select(g => $"{g.Kind.ToLowerInvariant()}: {string.Join(", ", g.Names)}"));
        }
    }

    /// <summary>Key: a section ("arrivals", "parking", "drinks", "staff"): all are on one page, so nothing to do.</summary>
    public override void Focus(string key) => Refresh();

    private void CycleBeerHours()
    {
        int[] options = [0, 15 * GameTime.MinutesPerHour, State.Rules.CloseMinute, GameTime.MinutesPerDay];
        int index = Array.IndexOf(options, State.Park.BeerFromMinute);
        Ctx.Host.Enqueue(new SetBeerHoursCommand(options[(index + 1) % options.Length]));
    }

    // ---------------------------------------------------------------- helpers (as in the inspector windows)

    private static VBoxContainer Section(VBoxContainer into)
    {
        var section = new VBoxContainer();
        section.AddThemeConstantOverride("separation", 8);
        into.AddChild(section);
        return section;
    }

    private static void Heading(VBoxContainer into, string text) =>
        into.AddChild(UiTheme.Label(text.ToUpperInvariant(), 11, UiTheme.Accent, bold: true));

    private static Label Text(VBoxContainer into, string text, int size = 12, Color? color = null)
    {
        var label = UiTheme.Label(text, size, color ?? UiTheme.TextDim);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(ContentWidth, 0);
        into.AddChild(label);
        return label;
    }

    private void Tiles(VBoxContainer into, params (string Caption, Func<string> Value)[] tiles)
    {
        var row = Row(24);
        foreach (var (caption, value) in tiles)
        {
            row.AddChild(UiTheme.StatTile(caption, out var label, 18));
            _updates.Add(() => label.Text = value());
        }
        into.AddChild(row);
    }

    private void Lever(VBoxContainer into, string name, Func<string> value, Action lower, Action raise, string lowerTip, string raiseTip, Func<string> detail)
    {
        var row = Row(8);
        var caption = UiTheme.Label(name, 13, bold: true);
        caption.CustomMinimumSize = new Vector2(120, 0);
        row.AddChild(caption);
        row.AddChild(UiTheme.Button("−", lower, lowerTip));
        var label = UiTheme.Label("", 16, bold: true);
        label.CustomMinimumSize = new Vector2(84, 0);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        row.AddChild(label);
        row.AddChild(UiTheme.Button("+", raise, raiseTip));
        into.AddChild(row);
        var line = Text(into, "", 11);
        _updates.Add(() =>
        {
            label.Text = value();
            line.Text = detail();
        });
    }
}
