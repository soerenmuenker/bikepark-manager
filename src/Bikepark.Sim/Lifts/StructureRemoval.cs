using Bikepark.Sim.Events;
using Bikepark.Sim.Land;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Lifts;

/// <summary>
/// Renaturalizing structures: a lift the park owns, a parking lot or a gravel platform is removed at once and its pads
/// go back to the natural ground. Lifts and parking lots are torn down by a contractor (paid at once, no waiting);
/// platforms are free. Paths and trails that started or ended on the structure get a loose end there.
/// </summary>
public static class StructureRemoval
{
    /// <summary>What a structure is, for the tool and messages (null: no such structure).</summary>
    public static (string Name, string What)? Describe(WorldState state, int id) =>
        state.Lifts.FirstOrDefault(l => l.Id == id) is { } lift ? (lift.Name, LiftNetwork.FindType(state, lift.TypeId)?.Name ?? "lift")
        : state.ParkingLots.FirstOrDefault(p => p.Id == id) is { } lot ? (lot.Name, "parking lot")
        : state.Platforms.FirstOrDefault(p => p.Id == id) is { } platform ? (platform.Name, "gravel platform")
        : null;

    /// <summary>The lift, parking lot or platform owning a network hub (a lift for either station), or 0.</summary>
    public static int OwnerOfHub(WorldState state, int hubId) =>
        state.Lifts.FirstOrDefault(l => l.Valley.Id == hubId || l.Mountain.Id == hubId)?.Id
        ?? state.ParkingLots.FirstOrDefault(p => p.Id == hubId)?.Id
        ?? state.Platforms.FirstOrDefault(p => p.Id == hubId)?.Id
        ?? 0;

    /// <summary>What tearing it down costs: a share of a lift's build price, per space of a parking lot, platforms free.</summary>
    public static long CostCents(WorldState state, int id)
    {
        var rules = state.LiftRules;
        if (state.Lifts.FirstOrDefault(l => l.Id == id) is { } lift)
            return (LiftNetwork.FindType(state, lift.TypeId)?.BuildCostCents ?? 0) * rules.RemovalPermille / 1000;
        if (state.ParkingLots.FirstOrDefault(p => p.Id == id) is { } lot)
            return lot.Spaces * rules.ParkingRemovalCentsPerSpace;
        return 0;
    }

    /// <summary>Why it can't be renaturalized now, or null.</summary>
    public static string? CannotRemove(WorldState state, int id)
    {
        if (Describe(state, id) is not { } info) return "No such lift, parking lot or platform.";
        if (state.Lifts.FirstOrDefault(l => l.Id == id) is { } lift)
        {
            if (LiftNetwork.FindOperator(state, lift.OperatorId) is { } op) return $"{lift.Name} belongs to {op.Name}.";
            if (state.ParkingLots.FirstOrDefault(p => p.LiftId == id) is { } lot) return $"{lot.Name} serves {lift.Name}: renaturalize it first.";
        }
        if (!PadsOwned(state, id)) return $"{info.Name} stands on land the park doesn't own.";
        long cost = CostCents(state, id);
        if (cost > 0 && state.Finance.MoneyCents < cost) return $"Not enough money: tearing down {info.Name} costs {cost / 100:N0} €.";
        return null;
    }

    /// <summary>Removes it (pads, the network hubs, riders using it start over), pays the contractor.</summary>
    public static void Remove(SimContext ctx, int id)
    {
        var state = ctx.State;
        var (name, _) = Describe(state, id)!.Value;
        long cost = CostCents(state, id);
        var hubs = new List<int> { id };
        if (state.Lifts.FirstOrDefault(l => l.Id == id) is { } lift)
        {
            hubs.Add(lift.Valley.Id);
            hubs.Add(lift.Mountain.Id);
            state.Lifts.Remove(lift);
        }
        state.ParkingLots.RemoveAll(p => p.Id == id);
        state.Platforms.RemoveAll(p => p.Id == id);
        state.TerrainEdits.RemoveAll(e => e.OwnerId == id);
        foreach (var way in state.Ways)
        {
            if (hubs.Contains(way.StartHubId)) way.StartHubId = 0;
            if (hubs.Contains(way.EndHubId)) way.EndHubId = 0;
        }
        state.TerrainRevision++;
        state.WaysRevision++;
        if (cost > 0)
        {
            state.Finance.Spend(cost);
            state.Finance.TotalRemovalCents += cost;
        }
        Systems.RiderSystem.ResetRidersUsing(ctx, [.. hubs]);
        ctx.Publish(new StructureRenaturalized(ctx.Tick, id, name, cost));
    }

    private static bool PadsOwned(WorldState state, int id) =>
        state.TerrainEdits.Where(e => e.OwnerId == id).All(e => LandMath.IsOwned(state, e.Pad.CenterX, e.Pad.CenterZ));
}
