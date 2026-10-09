using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Reputation;
using Bikepark.Sim.Safety;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;
using Bikepark.Sim.Weather;

namespace Bikepark.Sim.State;

/// <summary>
/// The complete, serializable state of one game. Everything the simulation needs to continue
/// deterministically must live here (including RNG state and pending commands); nothing else is saved.
/// Mutate only from systems and commands during <see cref="Simulation.Step"/>.
/// </summary>
public sealed class WorldState
{
    /// <summary>Next tick to be simulated (= number of ticks simulated so far).</summary>
    public long Tick { get; set; }

    /// <summary>Original seed, kept for reference and diagnostics.</summary>
    public ulong Seed { get; set; }

    public string ScenarioId { get; set; } = "";

    public SimRandom Rng { get; set; } = SimRandom.FromSeed(0);

    public ParkState Park { get; set; } = new();

    public ParkRules Rules { get; set; } = new();

    /// <summary>
    /// Terrain generation parameters. The terrain itself is derived from these (see <see cref="Simulation.Terrain"/>)
    /// and is not saved.
    /// </summary>
    public TerrainSettings Terrain { get; set; } = new();

    /// <summary>Flattened pads (stations, plateaus, parking lots), applied in order on top of the generated terrain.</summary>
    public List<TerrainEdit> TerrainEdits { get; set; } = [];

    /// <summary>Incremented whenever <see cref="TerrainEdits"/> changes; invalidates the derived terrain and network.</summary>
    public int TerrainRevision { get; set; }

    public TrailRules TrailRules { get; set; } = new();

    /// <summary>How trails wear (off by default).</summary>
    public WearRules WearRules { get; set; } = new();

    public WeatherRules WeatherRules { get; set; } = new();

    public WeatherState Weather { get; set; } = new();

    /// <summary>Access paths and trails, in build order. Only player input; geometry is derived.</summary>
    public List<Way> Ways { get; set; } = [];

    /// <summary>Incremented whenever <see cref="Ways"/> changes; invalidates the derived network.</summary>
    public int WaysRevision { get; set; }

    /// <summary>Trail feature models (content, copied from <c>data/trail_features.json</c>).</summary>
    public List<TrailFeatureType> TrailFeatureTypes { get; set; } = [];

    /// <summary>Lift models (content, copied from <c>data/lift_types.json</c>).</summary>
    public List<LiftType> LiftTypes { get; set; } = [];

    /// <summary>Lift companies and the bike access they offer (content, from the scenario).</summary>
    public List<LiftOperator> Operators { get; set; } = [];

    public LiftRules LiftRules { get; set; } = new();

    /// <summary>Lifts in build order.</summary>
    public List<Lift> Lifts { get; set; } = [];

    public List<ParkingLot> ParkingLots { get; set; } = [];

    public CrewRules CrewRules { get; set; } = new();

    /// <summary>Tool models (content, copied from <c>data/tools.json</c>).</summary>
    public List<ToolType> ToolTypes { get; set; } = [];

    /// <summary>Ids of the tools the park owns, in purchase order.</summary>
    public List<string> OwnedToolIds { get; set; } = [];

    /// <summary>Workers in hiring order.</summary>
    public List<CrewMember> Crew { get; set; } = [];

    /// <summary>Crew jobs in priority order (the crew works on the first workable ones).</summary>
    public List<Job> Jobs { get; set; } = [];

    /// <summary>Wood in stock (units; a felled tree gives <see cref="CrewRules.WoodPerTree"/>).</summary>
    public int WoodStock { get; set; }

    /// <summary>
    /// Scatter trees cut down outside the corridors of built ways, in felling order (position = tree identity). Trees in
    /// a built way's corridor are gone anyway and are dropped from this list when the way is finished.
    /// </summary>
    public List<PointCm> FelledTrees { get; set; } = [];

    public CrewStats CrewStats { get; set; } = new();

    public FinanceState Finance { get; set; } = new();

    public ParkStats Stats { get; set; } = new();

    /// <summary>Reviews, rating, influencers and park XP (off by default).</summary>
    public ReputationRules ReputationRules { get; set; } = new();

    public ReputationState Reputation { get; set; } = new();

    /// <summary>Crashes, helicopter rescues and insurance (off by default).</summary>
    public CrashRules CrashRules { get; set; } = new();

    public SafetyState Safety { get; set; } = new();

    /// <summary>Guests currently in the park, in arrival order.</summary>
    public List<Guest> Guests { get; set; } = [];

    public int NextEntityId { get; set; } = 1;

    /// <summary>Commands not yet applied, sorted by (Tick, Sequence). Managed by <see cref="CommandQueue"/>.</summary>
    public List<ScheduledCommand> PendingCommands { get; set; } = [];

    public long NextCommandSequence { get; set; }

    public int AllocateEntityId() => NextEntityId++;
}

public sealed class ParkState
{
    public string Name { get; set; } = "Unnamed Bikepark";
    public long EntryFeeCents { get; set; }
}

/// <summary>Balancing parameters, loaded from the scenario and saved with the game.</summary>
public sealed class ParkRules
{
    public int OpenMinute { get; set; } = 8 * GameTime.MinutesPerHour;
    public int CloseMinute { get; set; } = 20 * GameTime.MinutesPerHour;

    /// <summary>Expected arrivals per open minute at the reference fee, in thousandths (400 = 0.4/min).</summary>
    public int BaseArrivalPermille { get; set; } = 400;

    public long ReferenceEntryFeeCents { get; set; } = 1500;
    public long MaxEntryFeeCents { get; set; } = 10_000;
    public int Capacity { get; set; } = 150;
    public long DailyUpkeepCents { get; set; } = 25_000;
    public long SnackPriceCents { get; set; } = 400;

    // ---- Daily rhythm (all off by default, so older saves and scenarios behave as before) ----

    /// <summary>After closing, riders already on a lap finish it (the lift still serves its queue) for this long; then everyone goes home.</summary>
    public int LastRideMinutes { get; set; }

    /// <summary>The lifts start running (empty) this many minutes before opening.</summary>
    public int LiftWarmupMinutes { get; set; }

    /// <summary>
    /// Arrival rate over the day, in permille of <see cref="BaseArrivalPermille"/>: points sorted by minute, linearly
    /// interpolated, held flat before the first and after the last point. Empty = 1000 all day.
    /// </summary>
    public List<ArrivalPoint> ArrivalProfile { get; set; } = [];

    /// <summary>
    /// Guests plan one lunch break for a minute in this window (both 0 = no lunch), peaked in its middle, and take it at
    /// their first break between laps after that minute.
    /// </summary>
    public int LunchStartMinute { get; set; }
    public int LunchEndMinute { get; set; }
    public int LunchMinMinutes { get; set; } = 20;
    public int LunchMaxMinutes { get; set; } = 40;
    public long LunchPriceCents { get; set; } = 1_400;

    /// <summary>Energy (0..1000) and happiness a lunch restores.</summary>
    public int LunchEnergy { get; set; } = 250;
    public int LunchHappiness { get; set; } = 40;

    public bool HasLunch => LunchEndMinute > LunchStartMinute;

    public List<string> Validate()
    {
        var errors = new List<string>();
        if (OpenMinute < 0 || OpenMinute >= GameTime.MinutesPerDay) errors.Add("rules.openMinute out of range");
        if (CloseMinute <= OpenMinute || CloseMinute > GameTime.MinutesPerDay) errors.Add("rules.closeMinute must be after openMinute and within the day");
        if (BaseArrivalPermille < 0) errors.Add("rules.baseArrivalPermille must be >= 0");
        if (ReferenceEntryFeeCents <= 0) errors.Add("rules.referenceEntryFeeCents must be > 0");
        if (Capacity <= 0) errors.Add("rules.capacity must be > 0");
        if (LastRideMinutes < 0 || CloseMinute + LastRideMinutes > GameTime.MinutesPerDay)
            errors.Add("rules.lastRideMinutes must be >= 0 and end within the day");
        if (LiftWarmupMinutes < 0 || LiftWarmupMinutes > OpenMinute) errors.Add("rules.liftWarmupMinutes must be within 0..openMinute");
        for (int i = 0; i < ArrivalProfile.Count; i++)
        {
            var p = ArrivalProfile[i];
            if (p.Minute < 0 || p.Minute >= GameTime.MinutesPerDay || p.Permille is < 0 or > 10_000)
                errors.Add("rules.arrivalProfile: minute must be within the day and permille within 0..10000");
            if (i > 0 && p.Minute <= ArrivalProfile[i - 1].Minute) errors.Add("rules.arrivalProfile: minutes must increase");
        }
        if (LunchStartMinute < 0 || LunchEndMinute < 0 || LunchEndMinute > GameTime.MinutesPerDay || LunchEndMinute < LunchStartMinute)
            errors.Add("rules: lunch window must satisfy 0 <= lunchStartMinute <= lunchEndMinute <= 1440");
        if (LunchMinMinutes < 1 || LunchMaxMinutes < LunchMinMinutes || LunchMaxMinutes > 240)
            errors.Add("rules: lunch minutes must satisfy 1 <= lunchMinMinutes <= lunchMaxMinutes <= 240");
        if (LunchPriceCents < 0) errors.Add("rules.lunchPriceCents must be >= 0");
        if (LunchEnergy is < 0 or > 1000) errors.Add("rules.lunchEnergy must be within 0..1000");
        if (LunchHappiness is < 0 or > 1000) errors.Add("rules.lunchHappiness must be within 0..1000");
        return errors;
    }
}

/// <summary>One point of <see cref="ParkRules.ArrivalProfile"/>.</summary>
public sealed record ArrivalPoint(int Minute, int Permille);

public sealed class FinanceState
{
    public long MoneyCents { get; set; }
    public long TotalRevenueCents { get; set; }
    public long TotalExpensesCents { get; set; }
    public long RevenueTodayCents { get; set; }
    public long ExpensesTodayCents { get; set; }

    /// <summary>Lunch sales (included in the revenue).</summary>
    public long TotalFoodCents { get; set; }

    /// <summary>Bike access fees paid to lift companies (included in the expenses).</summary>
    public long TotalLiftFeesCents { get; set; }
    public long LiftFeesTodayCents { get; set; }

    /// <summary>Crew wages, tool and wood purchases (included in the expenses).</summary>
    public long TotalWagesCents { get; set; }
    public long WagesTodayCents { get; set; }
    public long TotalToolsCents { get; set; }
    public long TotalWoodCents { get; set; }

    public void Earn(long cents)
    {
        MoneyCents += cents;
        TotalRevenueCents += cents;
        RevenueTodayCents += cents;
    }

    public void Spend(long cents)
    {
        MoneyCents -= cents;
        TotalExpensesCents += cents;
        ExpensesTodayCents += cents;
    }
}

public sealed class ParkStats
{
    public long TotalVisitors { get; set; }
    public long TotalTurnedAway { get; set; }
    public int VisitorsToday { get; set; }
    public int TurnedAwayToday { get; set; }

    /// <summary>Guests turned away because the parking lots were full (included in <see cref="TotalTurnedAway"/>).</summary>
    public long TotalTurnedAwayParkingFull { get; set; }
    public long TotalGuestsLeft { get; set; }
    public long TotalLeftUnhappy { get; set; }

    /// <summary>Sum of happiness of all guests at the moment they left; divide by <see cref="TotalGuestsLeft"/>.</summary>
    public long SumExitHappiness { get; set; }
}

public enum RiderStyle : byte
{
    Flow = 0,
    Technical = 1,
    Casual = 2,
}

public enum RiderActivity : byte
{
    /// <summary>No access path yet: the guest just hangs around (pre-trail behaviour).</summary>
    Wandering = 0,

    /// <summary>Between laps, at <see cref="Guest.LocationWayId"/>/<see cref="Guest.LocationCm"/>.</summary>
    Idle = 1,

    /// <summary>On an access path (usually climbing).</summary>
    Climbing = 2,

    /// <summary>On a trail.</summary>
    Descending = 3,

    /// <summary>On foot between the parking lot and the valley station.</summary>
    Walking = 4,

    /// <summary>In a lift queue (<see cref="Lift.Queue"/>).</summary>
    Queuing = 5,

    /// <summary>Riding a lift up.</summary>
    OnLift = 6,

    /// <summary>Having lunch where the last run ended, until <see cref="Guest.BusyUntilTick"/>.</summary>
    Eating = 7,

    /// <summary>Seriously injured: lies where they fell on the trail (blocking it) until the helicopter at <see cref="Guest.RescueAtTick"/>.</summary>
    Injured = 8,
}

public sealed class Guest
{
    public int Id { get; set; }
    public long CashCents { get; set; }

    /// <summary>Mood, 0..1000.</summary>
    public int Happiness { get; set; }

    public long ArrivedTick { get; set; }
    public int PlannedStayMinutes { get; set; }

    // ---- Rider ----

    /// <summary>Riding skill, 0..1000 (compare with trail difficulty).</summary>
    public int Skill { get; set; }

    public RiderStyle Style { get; set; }

    /// <summary>0..1000; climbing drains it, tired riders go home.</summary>
    public int Energy { get; set; } = 1000;

    public RiderActivity Activity { get; set; }

    /// <summary>Where an idle rider is: a way and distance along it (a network node), or a hub (station, parking lot).</summary>
    public int LocationWayId { get; set; }
    public long LocationCm { get; set; }
    public int LocationHubId { get; set; }

    /// <summary>When the rider joined the current lift queue.</summary>
    public long QueueSinceTick { get; set; }

    /// <summary>Current lap: legs (walks, lifts, paths) to the chosen trail's start, then the trail itself (last leg).</summary>
    public List<RouteLeg> Route { get; set; } = [];

    /// <summary>Distance covered along the whole <see cref="Route"/>.</summary>
    public long RouteProgressCm { get; set; }

    /// <summary>Index of the leg the rider is on.</summary>
    public int LegIndex { get; set; }

    /// <summary>
    /// Milliseconds the rider has been waiting at the entrance of the trail leg they are on; -1 once they have dropped in
    /// (or when they are not on a trail).
    /// </summary>
    public int EntryWaitMs { get; set; } = -1;

    /// <summary>The trail this lap is for (0 = none).</summary>
    public int TrailId { get; set; }

    public int LastTrailId { get; set; }
    public long RunStartTick { get; set; }
    public long RunFun { get; set; }
    public int RunSegments { get; set; }
    public int RunsCompleted { get; set; }

    // ---- Daily rhythm ----

    /// <summary>Minute of the day from which the guest wants lunch (-1: no lunch planned, e.g. arrived after the window).</summary>
    public int LunchMinute { get; set; } = -1;

    public bool HadLunch { get; set; }

    /// <summary>When the current break (<see cref="RiderActivity.Eating"/>) ends.</summary>
    public long BusyUntilTick { get; set; }

    // ---- Visit, for the review written on leaving (see Reputation.ReviewMath) ----

    /// <summary>Sum of the fun of all finished runs (divide by <see cref="RunsCompleted"/>).</summary>
    public long VisitFunSum { get; set; }

    /// <summary>Runs on trails clearly harder than the rider's skill.</summary>
    public int ScaredRuns { get; set; }

    /// <summary>Difficulty score of the hardest trail ridden.</summary>
    public int HardestDifficulty { get; set; }

    /// <summary>Distinct trails ridden, in the order first ridden.</summary>
    public List<int> TrailsRidden { get; set; } = [];

    public long JumpFunSum { get; set; }
    public int JumpCount { get; set; }

    /// <summary>Time stuck behind slower riders on trails.</summary>
    public int HeldUpSeconds { get; set; }

    /// <summary>Minutes queued beyond the lift's grace time.</summary>
    public int QueueMinutes { get; set; }

    /// <summary>The entry fee paid on arrival (for the value-for-money part of the review).</summary>
    public long PaidEntryCents { get; set; }

    /// <summary>An influencer: their review becomes a FakeSocial post.</summary>
    public bool IsInfluencer { get; set; }

    // ---- Crashes (see Safety.CrashRules) ----

    /// <summary>Hurt in a crash: minor riders ride down slowly and go home, serious ones wait for the helicopter.</summary>
    public InjurySeverity Injury { get; set; }

    public long CrashTick { get; set; }
    public int CrashWayId { get; set; }
    public CrashCause CrashCause { get; set; }

    /// <summary>The feature they crashed on (0 = none).</summary>
    public int CrashFeatureId { get; set; }

    /// <summary>When the helicopter has flown a seriously injured rider out.</summary>
    public long RescueAtTick { get; set; }

    /// <summary>Riders this guest saw crashed on the trail ahead of them (it makes the park feel less safe).</summary>
    public int CrashesSeen { get; set; }
}
