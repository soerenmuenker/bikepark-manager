using Bikepark.Sim.State;
using Bikepark.Sim.Terrain;

namespace Bikepark.Sim.Lifts;

/// <summary>
/// A lift. Only player/scenario input and running state are stored: its stations (each on a flattened pad), who
/// runs it, how many carriers take bikes, the queue and the dispatch clock. Line length and ride time are derived
/// (<see cref="LiftMath"/>).
/// </summary>
public sealed class Lift
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string TypeId { get; set; } = "";

    /// <summary>The lift company running it, or null if the park owns it.</summary>
    public string? OperatorId { get; set; }

    public LiftStation Valley { get; set; } = new();

    /// <summary>The top station; its pad is the plateau that paths and trails start from.</summary>
    public LiftStation Mountain { get; set; } = new();

    /// <summary>The bike usage grade in effect: share of carriers equipped for bikes, 0..1000.</summary>
    public int BikeCarrierPermille { get; set; }

    /// <summary>The rented bike access, for lifts run by an operator.</summary>
    public BikeAccess? BikeAccess { get; set; }

    /// <summary>Riders waiting at the valley station, first in line first.</summary>
    public List<int> Queue { get; set; } = [];

    /// <summary>Carriers sent up so far; carrier k is bike-equipped per <see cref="LiftMath.IsBikeCarrier"/>.</summary>
    public long CarriersDispatched { get; set; }

    /// <summary>Time towards the next carrier, carried over between ticks.</summary>
    public int DispatchRemainderMs { get; set; }

    public LiftStats Stats { get; set; } = new();

    /// <summary>Rusty and out of service (the park can restore it); carries nobody.</summary>
    public bool Derelict { get; set; }

    /// <summary>A contractor is building or restoring it: it runs from this tick on (0 = not under construction).</summary>
    public long ReadyTick { get; set; }

    /// <summary>
    /// Stopped after a crash on its track (T-bar) until this tick: nobody boards, nobody on it moves (0 = not stopped).
    /// </summary>
    public long StoppedUntilTick { get; set; }

    public bool IsStopped(long tick) => tick < StoppedUntilTick;

    /// <summary>Running and taking riders (not derelict, not under construction).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool InService => !Derelict && ReadyTick == 0;
}

/// <summary>A station: its own id (the network hub) and the pad it stands on.</summary>
public sealed class LiftStation
{
    public int Id { get; set; }
    public int TerrainEditId { get; set; }
}

public sealed class BikeAccess
{
    /// <summary>Index into the operator's tiers currently in effect (and billed).</summary>
    public int TierIndex { get; set; }

    /// <summary>Tier booked to start at the next opening.</summary>
    public int? PendingTierIndex { get; set; }
}

public sealed class LiftStats
{
    public long Riders { get; set; }
    public int RidersToday { get; set; }
    public long SumWaitMinutes { get; set; }
    public int MaxQueueToday { get; set; }
    public int MaxQueue { get; set; }
}

/// <summary>A parking lot: guests arrive here by car and walk to the valley station of <see cref="LiftId"/>.</summary>
public sealed class ParkingLot
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Spaces { get; set; }
    public int TerrainEditId { get; set; }

    /// <summary>The lift whose valley station it serves.</summary>
    public int LiftId { get; set; }
}

/// <summary>Derived numbers and shared helpers for lifts. Pure.</summary>
public static class LiftMath
{
    /// <summary>
    /// Whether carrier number <paramref name="k"/> is equipped for bikes at the given share (Bresenham spread:
    /// 250 ‰ is exactly every 4th carrier). The view uses the same rule to colour cabins.
    /// </summary>
    public static bool IsBikeCarrier(long k, int permille) =>
        permille >= 1000 || (permille > 0 && (k + 1) * permille / 1000 > k * permille / 1000);

    /// <summary>Riders with bikes the lift can carry per hour.</summary>
    public static int BikeRidersPerHour(LiftType type, int permille) =>
        (int)(3600L * 1000 / type.IntervalSeconds * permille / 1000 * type.BikesPerCarrier / 1000);

    /// <summary>Expected minutes until a rider joining the back of the queue boards (rounded up), or -1 if no carrier takes bikes.</summary>
    public static int ExpectedWaitMinutes(LiftType type, int permille, int queueLength)
    {
        long perHour = BikeRidersPerHour(type, permille);
        if (perHour <= 0) return -1;
        return (int)((queueLength * 60L + perHour - 1) / perHour);
    }

    /// <summary>Horizontal length, height gain and slope length of a lift line between two pads, in cm.</summary>
    public static (long HorizontalCm, int RiseCm, long LengthCm) Line(TerrainPad valley, TerrainPad mountain)
    {
        long dx = mountain.CenterX - valley.CenterX, dz = mountain.CenterZ - valley.CenterZ;
        long horizontal = FixedMath.ISqrt(dx * dx + dz * dz);
        int rise = mountain.TargetHeightCm - valley.TargetHeightCm;
        return (horizontal, rise, FixedMath.ISqrt(horizontal * horizontal + (long)rise * rise));
    }

    /// <summary>Guests the parking lots hold (spaces × guests per car), or null without parking lots.</summary>
    public static int? ParkingCapacity(WorldState state)
    {
        if (state.ParkingLots.Count == 0) return null;
        long spaces = state.ParkingLots.Sum(p => (long)p.Spaces);
        return (int)(spaces * state.LiftRules.GuestsPerCarPermille / 1000);
    }

    /// <summary>Ride time in seconds (rounded up).</summary>
    public static int RideSeconds(LiftType type, long lengthCm) => (int)((lengthCm + type.SpeedCmPerS - 1) / type.SpeedCmPerS);
}
