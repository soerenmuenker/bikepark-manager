using Bikepark.Sim.Lifts;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Facilities;

public enum FacilityKind : byte
{
    /// <summary>Food and drink: lunch (with a limited number of seats) and snacks.</summary>
    Bar = 0,

    /// <summary>Merchandise: guests on their way home buy something, the happier the likelier.</summary>
    Shop = 1,
}

/// <summary>A building model from <c>data/facility_types.json</c>, copied into the world.</summary>
public sealed class FacilityType
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public FacilityKind Kind { get; set; }
    public long BuildCostCents { get; set; }
    public long UpkeepPerDayCents { get; set; }

    /// <summary>The pad it stands on.</summary>
    public int LengthMeters { get; set; } = 10;
    public int WidthMeters { get; set; } = 8;

    /// <summary>Park level needed to build it (a sandbox has everything).</summary>
    public int RequiredLevel { get; set; }

    // ---- bar ----

    /// <summary>Guests who can have lunch at the same time.</summary>
    public int Seats { get; set; }
    public long LunchPriceCents { get; set; }
    public long SnackPriceCents { get; set; }

    // ---- shop ----

    public long MerchPriceCents { get; set; }

    /// <summary>What the goods cost the park, permille of the price.</summary>
    public int MerchCostPermille { get; set; }

    /// <summary>Chance that a perfectly happy guest leaving buys something, permille (scaled by mood).</summary>
    public int BuyChancePermille { get; set; }

    public List<string> Validate()
    {
        var errors = new List<string>();
        string p = $"facilityTypes[{Id}]";
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("facilityTypes: id is required");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add($"{p}.name is required");
        if (!Enum.IsDefined(Kind)) errors.Add($"{p}.kind is unknown");
        if (BuildCostCents < 0 || UpkeepPerDayCents < 0) errors.Add($"{p}: costs must be >= 0");
        if (LengthMeters is < 4 or > 60 || WidthMeters is < 4 or > 60) errors.Add($"{p}: lengthMeters and widthMeters must be within 4..60");
        if (RequiredLevel is < 0 or > Reputation.ReputationRules.MaxLevel)
            errors.Add($"{p}.requiredLevel must be within 0..{Reputation.ReputationRules.MaxLevel}");
        if (Kind == FacilityKind.Bar && Seats is < 1 or > 1000) errors.Add($"{p}.seats must be within 1..1000");
        if (LunchPriceCents < 0 || SnackPriceCents < 0 || MerchPriceCents < 0) errors.Add($"{p}: prices must be >= 0");
        if (MerchCostPermille is < 0 or > 1000) errors.Add($"{p}.merchCostPermille must be within 0..1000");
        if (BuyChancePermille is < 0 or > 1000) errors.Add($"{p}.buyChancePermille must be within 0..1000");
        return errors;
    }
}

/// <summary>A bar or shop the park built next to a valley station; it serves the guests at that lift's base.</summary>
public sealed class Facility
{
    public int Id { get; set; }
    public string TypeId { get; set; } = "";
    public string Name { get; set; } = "";
    public int TerrainEditId { get; set; }

    /// <summary>The lift whose valley station (base) it serves.</summary>
    public int LiftId { get; set; }

    public long TotalRevenueCents { get; set; }
    public long RevenueTodayCents { get; set; }

    /// <summary>Lunches served (bar) or guests who bought something (shop).</summary>
    public int Customers { get; set; }
}

/// <summary>Which facility serves a guest, and the money they bring. Pure.</summary>
public static class FacilityMath
{
    public static FacilityType? FindType(WorldState state, string typeId) => state.FacilityTypes.FirstOrDefault(t => t.Id == typeId);

    /// <summary>
    /// The lift whose base the guest is at (0: none): idle at its valley station, at a parking lot serving it, in its
    /// queue, or idle on a way within <see cref="LiftRules.StationReachMeters"/> of its valley station.
    /// </summary>
    public static int BaseLiftOf(WorldState state, WayNetwork network, Guest guest)
    {
        if (guest.Activity == RiderActivity.Queuing)
            return state.Lifts.FirstOrDefault(l => l.Queue.Contains(guest.Id))?.Id ?? 0;
        if (guest.Activity is not (RiderActivity.Idle or RiderActivity.Eating)) return 0;
        if (guest.LocationHubId != 0)
        {
            if (state.Lifts.FirstOrDefault(l => l.Valley.Id == guest.LocationHubId) is { } lift) return lift.Id;
            if (state.ParkingLots.FirstOrDefault(p => p.Id == guest.LocationHubId) is { } lot) return lot.LiftId;
            return 0;
        }
        if (guest.LocationWayId == 0 || !network.TryGetGeometry(guest.LocationWayId, out var geometry)) return 0;
        var at = geometry.PositionAt(guest.LocationCm);
        long reach = state.LiftRules.StationReachMeters * 100L;
        foreach (var lift in state.Lifts)
            if (LiftNetwork.Pad(state, lift.Valley.TerrainEditId) is { } pad && pad.DistanceOutside(at.X, at.Z) <= reach)
                return lift.Id;
        return 0;
    }

    /// <summary>The park's facilities of a kind at a lift's base, in build order.</summary>
    public static IEnumerable<(Facility Facility, FacilityType Type)> At(WorldState state, int liftId, FacilityKind kind)
    {
        if (liftId == 0) yield break;
        foreach (var facility in state.Facilities)
            if (facility.LiftId == liftId && FindType(state, facility.TypeId) is { } type && type.Kind == kind)
                yield return (facility, type);
    }

    /// <summary>Guests having lunch at the bar right now.</summary>
    public static int SeatsTaken(WorldState state, Facility bar) =>
        state.Guests.Count(g => g.Activity == RiderActivity.Eating && g.FacilityId == bar.Id);

    /// <summary>A bar at the guest's base with a free seat, or null.</summary>
    public static (Facility Facility, FacilityType Type)? FreeBar(WorldState state, WayNetwork network, Guest guest)
    {
        foreach (var bar in At(state, BaseLiftOf(state, network, guest), FacilityKind.Bar))
            if (SeatsTaken(state, bar.Facility) < bar.Type.Seats)
                return bar;
        return null;
    }

    /// <summary>
    /// True if lunch and snacks need a bar: the scenario has bars to build. Scenarios without facility content keep
    /// the old rule (lunch and snacks anywhere, at the park's prices).
    /// </summary>
    public static bool BarsNeeded(WorldState state) => state.FacilityTypes.Any(t => t.Kind == FacilityKind.Bar);

    /// <summary>Why the park can't build this type now (level, money), or null.</summary>
    public static string? CannotBuild(WorldState state, int level, FacilityType type)
    {
        if (state.Rules.UnlockLevel(level) < type.RequiredLevel)
            return $"The {type.Name.ToLowerInvariant()} needs park level {type.RequiredLevel} (you are level {level}).";
        if (state.Finance.MoneyCents < type.BuildCostCents)
            return $"Not enough money: the {type.Name.ToLowerInvariant()} costs {type.BuildCostCents / 100:N0} €.";
        return null;
    }

    /// <summary>The bar or shop whose pad is at the point (within <paramref name="marginCm"/>), or null.</summary>
    public static Facility? AtPoint(WorldState state, long x, long z, long marginCm = 0) =>
        state.Facilities.FirstOrDefault(f => LiftNetwork.Pad(state, f.TerrainEditId) is { } pad && pad.DistanceOutside(x, z) <= marginCm);

    /// <summary>Takes money from a guest at a facility: revenue for the park and the facility.</summary>
    public static void Sell(WorldState state, Facility facility, Guest guest, long cents)
    {
        guest.CashCents -= cents;
        state.Finance.Earn(cents);
        facility.TotalRevenueCents += cents;
        facility.RevenueTodayCents += cents;
    }
}
