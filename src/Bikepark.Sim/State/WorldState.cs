using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

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

    /// <summary>Access paths and trails, in build order. Only player input; geometry is derived.</summary>
    public List<Way> Ways { get; set; } = [];

    /// <summary>Incremented whenever <see cref="Ways"/> changes; invalidates the derived network.</summary>
    public int WaysRevision { get; set; }

    /// <summary>Lift models (content, copied from <c>data/lift_types.json</c>).</summary>
    public List<LiftType> LiftTypes { get; set; } = [];

    /// <summary>Lift companies and the bike access they offer (content, from the scenario).</summary>
    public List<LiftOperator> Operators { get; set; } = [];

    public LiftRules LiftRules { get; set; } = new();

    /// <summary>Lifts in build order.</summary>
    public List<Lift> Lifts { get; set; } = [];

    public List<ParkingLot> ParkingLots { get; set; } = [];

    public FinanceState Finance { get; set; } = new();

    public ParkStats Stats { get; set; } = new();

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
}

public sealed class FinanceState
{
    public long MoneyCents { get; set; }
    public long TotalRevenueCents { get; set; }
    public long TotalExpensesCents { get; set; }
    public long RevenueTodayCents { get; set; }
    public long ExpensesTodayCents { get; set; }

    /// <summary>Bike access fees paid to lift companies (included in the expenses).</summary>
    public long TotalLiftFeesCents { get; set; }
    public long LiftFeesTodayCents { get; set; }

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

    /// <summary>The trail this lap is for (0 = none).</summary>
    public int TrailId { get; set; }

    public int LastTrailId { get; set; }
    public long RunStartTick { get; set; }
    public long RunFun { get; set; }
    public int RunSegments { get; set; }
    public int RunsCompleted { get; set; }
}
