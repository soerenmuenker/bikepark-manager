using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Terrain;

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
    public long TotalGuestsLeft { get; set; }
    public long TotalLeftUnhappy { get; set; }

    /// <summary>Sum of happiness of all guests at the moment they left; divide by <see cref="TotalGuestsLeft"/>.</summary>
    public long SumExitHappiness { get; set; }
}

public sealed class Guest
{
    public int Id { get; set; }
    public long CashCents { get; set; }

    /// <summary>0..1000.</summary>
    public int Happiness { get; set; }

    public long ArrivedTick { get; set; }
    public int PlannedStayMinutes { get; set; }
}
