using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Land;

/// <summary>
/// A piece of land the park can own (content from the scenario, copied into the world). Its outline is a polygon in
/// map centimetres (any winding, no self-intersections). It can be bought once the park reaches
/// <see cref="RequiredLevel"/>, for <see cref="PriceCents"/>.
/// </summary>
public sealed class Parcel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<PointCm> Outline { get; set; } = [];
    public long PriceCents { get; set; }
    public int RequiredLevel { get; set; }
    public bool OwnedAtStart { get; set; }

    public List<string> Validate()
    {
        var errors = new List<string>();
        string p = $"parcels[{Id}]";
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("parcels: id is required");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add($"{p}.name is required");
        if (Outline.Count < 3) errors.Add($"{p}.outline needs at least 3 points");
        if (PriceCents < 0) errors.Add($"{p}.priceCents must be >= 0");
        if (RequiredLevel is < 0 or > Reputation.ReputationRules.MaxLevel) errors.Add($"{p}.requiredLevel must be within 0..{Reputation.ReputationRules.MaxLevel}");
        return errors;
    }
}

/// <summary>
/// Who owns which land (pure, integer). Without parcels (sandbox scenarios) all land is the park's. Every build check
/// (ways, lift stations, parking, felling) goes through <see cref="IsOwned"/>, via the planners.
/// </summary>
public static class LandMath
{
    public static bool IsOwned(WorldState state, long x, long z)
    {
        if (state.Parcels.Count == 0) return true;
        foreach (var parcel in state.Parcels)
            if (state.OwnedParcelIds.Contains(parcel.Id) && Contains(parcel.Outline, x, z))
                return true;
        return false;
    }

    /// <summary>A predicate for the planners (null when everything is owned, so they can skip the check).</summary>
    public static Func<long, long, bool>? OwnedPredicate(WorldState state) =>
        state.Parcels.Count == 0 ? null : (x, z) => IsOwned(state, x, z);

    /// <summary>The first parcel (in content order) containing the point, if any.</summary>
    public static Parcel? ParcelAt(WorldState state, long x, long z) => state.Parcels.FirstOrDefault(p => Contains(p.Outline, x, z));

    public static bool IsOwned(WorldState state, Parcel parcel) => state.OwnedParcelIds.Contains(parcel.Id);

    /// <summary>Point in polygon (even-odd ray casting, integer); points on the left/bottom edges count as inside.</summary>
    public static bool Contains(IReadOnlyList<PointCm> polygon, long x, long z)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            long xi = polygon[i].X, zi = polygon[i].Z, xj = polygon[j].X, zj = polygon[j].Z;
            if (zi > z == zj > z) continue;
            // x of the edge at height z, compared without division: x < xi + (z - zi) * (xj - xi) / (zj - zi).
            long lhs = (x - xi) * (zj - zi);
            long rhs = (z - zi) * (xj - xi);
            if (zj - zi > 0 ? lhs < rhs : lhs > rhs) inside = !inside;
        }
        return inside;
    }

    /// <summary>Area in square metres (shoelace).</summary>
    public static long AreaSquareMeters(IReadOnlyList<PointCm> polygon)
    {
        long twice = 0;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            twice += (long)polygon[j].X * polygon[i].Z - (long)polygon[i].X * polygon[j].Z;
        return Math.Abs(twice) / 2 / 10_000;
    }

    /// <summary>Why the park can't buy a parcel now, or null if it can.</summary>
    public static string? CannotBuy(WorldState state, Parcel parcel, int level)
    {
        if (IsOwned(state, parcel)) return $"{parcel.Name} is already yours.";
        if (level < parcel.RequiredLevel) return $"{parcel.Name} needs park level {parcel.RequiredLevel} (you are level {level}).";
        if (state.Finance.MoneyCents < parcel.PriceCents) return $"Not enough money for {parcel.Name}.";
        return null;
    }
}
