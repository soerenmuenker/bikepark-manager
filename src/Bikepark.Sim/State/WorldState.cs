using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Land;
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

    /// <summary>The scenario's welcome (content, presentation only: the HUD shows it at tick 0), or null.</summary>
    public Scenarios.WelcomeNote? Welcome { get; set; }

    public ParkRules Rules { get; set; } = new();

    /// <summary>
    /// Terrain generation parameters. The terrain itself is derived from these (see <see cref="Simulation.Terrain"/>)
    /// and is not saved.
    /// </summary>
    public TerrainSettings Terrain { get; set; } = new();

    /// <summary>Authored terrain strokes (scenario content), applied in order to the generated terrain, before the pads.</summary>
    public List<TerrainStroke> TerrainSculpt { get; set; } = [];

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

    /// <summary>Gravel platforms the player built to connect paths and trails.</summary>
    public List<Platform> Platforms { get; set; } = [];

    /// <summary>Bar and shop models (content, copied from <c>data/facility_types.json</c>).</summary>
    public List<Facilities.FacilityType> FacilityTypes { get; set; } = [];

    /// <summary>Bars and shops the park built, in build order.</summary>
    public List<Facilities.Facility> Facilities { get; set; } = [];

    /// <summary>What bars and shops sell (content); the park's prices are in <see cref="ParkState.Prices"/>.</summary>
    public List<Facilities.ProductType> ProductTypes { get; set; } = [];

    public Facilities.FacilityRules FacilityRules { get; set; } = new();

    /// <summary>Brands that sponsor trails and the park (content), their rules (and the photo spots'), and the deals.</summary>
    public List<Sponsors.SponsorType> SponsorTypes { get; set; } = [];
    public Sponsors.SponsorRules SponsorRules { get; set; } = new();
    public List<Sponsors.Sponsorship> Sponsorships { get; set; } = [];

    /// <summary>Land the park can own (content, from the scenario). None = all land is the park's.</summary>
    public List<Parcel> Parcels { get; set; } = [];

    /// <summary>Ids of the parcels the park owns, in the order bought (those owned at start first).</summary>
    public List<string> OwnedParcelIds { get; set; } = [];

    public CrewRules CrewRules { get; set; } = new();

    /// <summary>Tool models (content, copied from <c>data/tools.json</c>).</summary>
    public List<ToolType> ToolTypes { get; set; } = [];

    /// <summary>The tools the park owns, in purchase order.</summary>
    public List<OwnedTool> Tools { get; set; } = [];

    /// <summary>Workers in hiring order.</summary>
    public List<CrewMember> Crew { get; set; } = [];

    /// <summary>Today's applicants for the crew (only with <see cref="CrewRules.Grades"/>), rolled by <c>StaffSystem</c>.</summary>
    public List<Applicant> Applicants { get; set; } = [];

    /// <summary>The day the applicants were rolled (-1 = not yet).</summary>
    public long ApplicantsDay { get; set; } = -1;

    /// <summary>Park staff roles (content, <c>data/staff_roles.json</c>), their rules, and how many of each the park employs.</summary>
    public List<Staff.StaffRole> StaffRoles { get; set; } = [];
    public Staff.StaffRules StaffRules { get; set; } = new();
    public List<Staff.StaffPosition> Staff { get; set; } = [];

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

    /// <summary>Loan offers (content, from the scenario).</summary>
    public Banking.LoanRules LoanRules { get; set; } = new();

    /// <summary>Loans the park is paying back, in the order taken.</summary>
    public List<Banking.Loan> Loans { get; set; } = [];

    public ParkStats Stats { get; set; } = new();

    /// <summary>The last closed days' reports (oldest first, at most <see cref="DayHistoryDays"/>), for charts after a load.</summary>
    public List<Events.DayReport> DayHistory { get; set; } = [];

    public const int DayHistoryDays = 60;

    /// <summary>Reviews, rating, influencers and park XP (off by default).</summary>
    public ReputationRules ReputationRules { get; set; } = new();

    public ReputationState Reputation { get; set; } = new();

    /// <summary>Crashes, helicopter rescues, crash closures and crash rates (off by default).</summary>
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
    /// <summary>The general entrance fee, paid on arrival (the park without lifts).</summary>
    public long EntryFeeCents { get; set; }

    /// <summary>The lift day pass, paid once per visit when a guest first boards a lift (0 = lifts are free).</summary>
    public long LiftTicketCents { get; set; }

    /// <summary>Prices the park set per product (products without one sell at their reference price).</summary>
    public List<Facilities.ProductPrice> Prices { get; set; } = [];

    /// <summary>The parking fee per car (0 = free parking), shared by the guests in it; only where there are parking lots.</summary>
    public long ParkingFeeCents { get; set; }

    /// <summary>Bars serve alcohol to riders from this minute of the day (0 = all day; after closing, après-bike always may).</summary>
    public int BeerFromMinute { get; set; }

    /// <summary>Game over: the books stayed below zero for <see cref="ParkRules.BankruptcyDays"/> days. The park never opens again.</summary>
    public bool Bankrupt { get; set; }
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

    /// <summary>The parking fee per car guests find fair (counted with the entry fee as one arrival cost), and the most it can be.</summary>
    public long ReferenceParkingFeeCents { get; set; }
    public long MaxParkingFeeCents { get; set; } = 2_000;
    public int Capacity { get; set; } = 150;
    public long DailyUpkeepCents { get; set; } = 25_000;
    public long SnackPriceCents { get; set; } = 400;

    /// <summary>
    /// The park goes bankrupt (game over) when the books close below zero this many days in a row (0 = never).
    /// </summary>
    public int BankruptcyDays { get; set; }

    /// <summary>Sandbox: no park-level gates (features, lifts, and later tools, loans and facilities need no level).</summary>
    public bool UnlockAll { get; set; }

    /// <summary>The park level counted for unlocks: the real level, or the top level in a sandbox.</summary>
    public int UnlockLevel(int level) => UnlockAll ? Reputation.ReputationRules.MaxLevel : level;

    // ---- Daily rhythm (all off by default, so older saves and scenarios behave as before) ----

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

    /// <summary>Mood a packed lunch brings (when the scenario has bars but none had room for the guest).</summary>
    public int PackedLunchHappiness { get; set; } = 5;

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
        if (ReferenceParkingFeeCents < 0 || MaxParkingFeeCents < 0) errors.Add("rules: parking fees must be >= 0");
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
        if (PackedLunchHappiness is < 0 or > 1000) errors.Add("rules.packedLunchHappiness must be within 0..1000");
        if (BankruptcyDays is < 0 or > 365) errors.Add("rules.bankruptcyDays must be within 0..365");
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

    /// <summary>Parking fees and campground nights (included in the revenue).</summary>
    public long TotalParkingCents { get; set; }
    public long ParkingTodayCents { get; set; }
    public long TotalCampingCents { get; set; }
    public long CampingTodayCents { get; set; }

    /// <summary>Sponsor money and clips sold at photo spots (included in the revenue).</summary>
    public long TotalSponsorCents { get; set; }
    public long SponsorTodayCents { get; set; }
    public long TotalClipCents { get; set; }
    public long ClipTodayCents { get; set; }

    /// <summary>Bike rentals and repairs (included in the revenue).</summary>
    public long TotalRentalCents { get; set; }
    public long RentalTodayCents { get; set; }
    public long TotalRepairCents { get; set; }
    public long RepairTodayCents { get; set; }

    /// <summary>Coffee, beer and other drinks sold (included in the revenue).</summary>
    public long TotalDrinksCents { get; set; }
    public long DrinksTodayCents { get; set; }

    /// <summary>Sales per product, in the order first sold.</summary>
    public List<Facilities.ProductSales> ProductSales { get; set; } = [];

    /// <summary>Bike access fees paid to lift companies (included in the expenses).</summary>
    public long TotalLiftFeesCents { get; set; }
    public long LiftFeesTodayCents { get; set; }

    /// <summary>Lift day passes sold to guests (included in the revenue).</summary>
    public long TotalLiftTicketsCents { get; set; }
    public long LiftTicketsTodayCents { get; set; }

    /// <summary>Crew wages, tool and wood purchases (included in the expenses).</summary>
    public long TotalWagesCents { get; set; }
    public long WagesTodayCents { get; set; }
    public long TotalToolsCents { get; set; }
    public long TotalWoodCents { get; set; }

    /// <summary>Land bought, lifts built or restored by contractors (included in the expenses).</summary>
    public long TotalLandCents { get; set; }
    public long TotalLiftBuildCents { get; set; }

    /// <summary>Lifts and parking lots torn down (renaturalized) by contractors (included in the expenses).</summary>
    public long TotalRemovalCents { get; set; }

    /// <summary>Daily upkeep of the park's own lifts (included in the expenses).</summary>
    public long TotalLiftUpkeepCents { get; set; }
    public long LiftUpkeepTodayCents { get; set; }

    /// <summary>Snacks and merch sold (in the revenue; lunch is in <see cref="TotalFoodCents"/>; today's food money has both).</summary>
    public long TotalSnackCents { get; set; }
    public long TotalMerchCents { get; set; }
    public long MerchTodayCents { get; set; }
    public long FoodTodayCents { get; set; }

    /// <summary>Building bars and shops, their upkeep and the goods sold in shops (included in the expenses).</summary>
    public long TotalFacilityBuildCents { get; set; }
    public long TotalFacilityUpkeepCents { get; set; }
    public long TotalMerchCostCents { get; set; }

    /// <summary>Money borrowed (not revenue) and installments paid back (included in the expenses).</summary>
    public long TotalBorrowedCents { get; set; }
    public long TotalLoanPaymentsCents { get; set; }
    public long LoanPaymentsTodayCents { get; set; }

    /// <summary>Days in a row the books closed below zero (see <see cref="ParkRules.BankruptcyDays"/>).</summary>
    public int DaysInDebt { get; set; }

    /// <summary>Today's money per <see cref="MoneyCategory"/> (index = category; grows on demand, cleared at the day's end).</summary>
    public List<long> TodayCents { get; set; } = [];

    /// <summary>The latest one-off bookings (land, lifts, buildings, tools, wood, removals, loans), oldest first.</summary>
    public List<MoneyTransaction> Transactions { get; set; } = [];

    public const int MaxTransactions = 40;

    /// <summary>Money borrowed: in at once, a transaction but not revenue.</summary>
    public void Borrow(long cents, long tick, string label)
    {
        MoneyCents += cents;
        TotalBorrowedCents += cents;
        Book(MoneyCategory.LoanTaken, cents);
        Log(tick, MoneyCategory.LoanTaken, cents, label);
    }

    public void Earn(MoneyCategory category, long cents)
    {
        MoneyCents += cents;
        TotalRevenueCents += cents;
        RevenueTodayCents += cents;
        Book(category, cents);
    }

    public void Spend(MoneyCategory category, long cents)
    {
        MoneyCents -= cents;
        TotalExpensesCents += cents;
        ExpensesTodayCents += cents;
        Book(category, cents);
    }

    /// <summary>A one-off expense: spent and listed in <see cref="Transactions"/>.</summary>
    public void Invest(MoneyCategory category, long cents, long tick, string label)
    {
        Spend(category, cents);
        Log(tick, category, -cents, label);
    }

    public long Today(MoneyCategory category) => Ledger.Get(TodayCents, category);

    private void Book(MoneyCategory category, long cents)
    {
        while (TodayCents.Count < Ledger.Count) TodayCents.Add(0);
        TodayCents[(int)category] += cents;
    }

    private void Log(long tick, MoneyCategory category, long cents, string label)
    {
        Transactions.Add(new MoneyTransaction(tick, category, cents, label));
        if (Transactions.Count > MaxTransactions) Transactions.RemoveRange(0, Transactions.Count - MaxTransactions);
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

    /// <summary>Arrivals who turned back at the ticket office queue (too few ticket office staff).</summary>
    public long TotalTurnedAwayTicketQueue { get; set; }
    public long TotalGuestsLeft { get; set; }
    public long TotalLeftUnhappy { get; set; }

    /// <summary>Guests who stayed for après-bike.</summary>
    public long TotalApresGuests { get; set; }

    /// <summary>Guests who came to camp, and the nights they spent.</summary>
    public long TotalCampers { get; set; }
    public long TotalCampNights { get; set; }

    /// <summary>Guests who rented a bike, would-be renters who found none free, bikes fixed, fleet bikes retired.</summary>
    public long TotalRenters { get; set; }
    public long RentersWithoutBike { get; set; }
    public long TotalRepairs { get; set; }
    public long BikesRetired { get; set; }

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

    /// <summary>Après-bike: done riding, sitting at a bar (<see cref="Guest.FacilityId"/>) until <see cref="Guest.BusyUntilTick"/>, then home.</summary>
    Apres = 9,

    /// <summary>A camper asleep at their campground (not in the park) until <see cref="Guest.WakeTick"/>.</summary>
    Camping = 10,
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

    /// <summary>When in that minute (ms) the rider reached the queue: only carriers leaving after it can take them.</summary>
    public int QueuedAtMs { get; set; }

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

    /// <summary>The bar the guest is having lunch at or sitting at for après-bike (0: a packed lunch where they are, or not eating).</summary>
    public int FacilityId { get; set; }

    /// <summary>Stopped at a bar on the way: the route leg they go on with afterwards (-1: not stopped on the way).</summary>
    public int ResumeLeg { get; set; } = -1;

    /// <summary>Already stopped at a bar on the way this lap (one stop per lap).</summary>
    public bool StoppedThisLap { get; set; }

    // ---- Food and drink (see Facilities.FacilityRules) ----

    /// <summary>Alcohol, 0..1000: bolder trail choices, more crashes; wears off by the minute.</summary>
    public int Alcohol { get; set; }

    /// <summary>A coffee's boost: climbing costs half the energy until then.</summary>
    public long CaffeineUntilTick { get; set; }

    /// <summary>Paid above the reference prices this visit (lowers the value-for-money part of the review).</summary>
    public long OverpaidCents { get; set; }

    // ---- Camping (multi-day guests) ----

    /// <summary>The campground the guest has a pitch at (0: a day guest).</summary>
    public int CampgroundId { get; set; }

    /// <summary>Nights still to spend at the campground (the evening they reach 0 they go home).</summary>
    public int NightsLeft { get; set; }

    /// <summary>A sleeping camper wakes up then (the next opening).</summary>
    public long WakeTick { get; set; }

    // ---- Bikes ----

    /// <summary>The rental bike the guest rides today (0: their own bike).</summary>
    public int RentalBikeId { get; set; }

    /// <summary>Riding a rental e-bike: climbing is cheap.</summary>
    public bool EBike { get; set; }

    /// <summary>The bike broke in a crash: rolling down slowly, then to the service shop (or a new rental bike), else home.</summary>
    public bool BikeBroken { get; set; }

    /// <summary>Wears protectors from the pro shop: crashes are less often serious.</summary>
    public bool Protected { get; set; }

    /// <summary>Filmed landing a jump with a camera this run (the feature; 0: not filmed), and how much they enjoyed it.</summary>
    public int FilmedFeatureId { get; set; }
    public int FilmedFun { get; set; }

    // ---- Visit, for the review written on leaving (see Reputation.ReviewMath) ----

    /// <summary>Sum of the fun of all finished runs (divide by <see cref="RunsCompleted"/>).</summary>
    public long VisitFunSum { get; set; }

    /// <summary>Runs on trails clearly harder than the rider's skill.</summary>
    public int ScaredRuns { get; set; }

    /// <summary>Difficulty score of the hardest trail ridden.</summary>
    public int HardestDifficulty { get; set; }

    /// <summary>Distinct trails ridden, in the order first ridden.</summary>
    public List<int> TrailsRidden { get; set; } = [];

    /// <summary>Runs per trail, parallel to <see cref="TrailsRidden"/>.</summary>
    public List<int> TrailRuns { get; set; } = [];

    /// <summary>Bars and shops the guest bought something at, in the order first used.</summary>
    public List<int> FacilitiesUsed { get; set; } = [];

    /// <summary>Lifts the guest rode, in the order first ridden.</summary>
    public List<int> LiftsUsed { get; set; } = [];

    /// <summary>The money the guest came with (what they spent = this minus <see cref="CashCents"/>).</summary>
    public long BroughtCents { get; set; }

    public long JumpFunSum { get; set; }
    public int JumpCount { get; set; }

    /// <summary>Time stuck behind slower riders on trails.</summary>
    public int HeldUpSeconds { get; set; }

    /// <summary>Minutes queued beyond the lift's grace time.</summary>
    public int QueueMinutes { get; set; }

    /// <summary>The entry fee paid on arrival (for the value-for-money part of the review).</summary>
    public long PaidEntryCents { get; set; }

    /// <summary>Bought the lift day pass this visit (counted in <see cref="PaidEntryCents"/> too).</summary>
    public bool HasLiftPass { get; set; }

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
