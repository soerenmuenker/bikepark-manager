using Bikepark.Sim.State;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Lifts;

/// <summary>Derives the network hubs and links of lifts and parking lots from the world. Pure.</summary>
public static class LiftNetwork
{
    public static (List<NetworkHub> Hubs, List<NetworkLink> Links) Build(WorldState state)
    {
        var hubs = new List<NetworkHub>();
        var links = new List<NetworkLink>();
        foreach (var lift in state.Lifts)
        {
            var type = FindType(state, lift.TypeId);
            var valley = Pad(state, lift.Valley.TerrainEditId);
            var mountain = Pad(state, lift.Mountain.TerrainEditId);
            if (type is null || valley is null || mountain is null) continue;
            hubs.Add(new NetworkHub(lift.Valley.Id, HubKind.ValleyStation, lift.Id, valley));
            hubs.Add(new NetworkHub(lift.Mountain.Id, HubKind.MountainStation, lift.Id, mountain));
            long length = LiftMath.Line(valley, mountain).LengthCm;
            long cost = (long)LiftMath.RideSeconds(type, length) * state.LiftRules.LiftRideCostCmPerMinute / 60;
            links.Add(new NetworkLink(LegKind.Lift, lift.Id, lift.Valley.Id, lift.Mountain.Id, length, cost, type.CorridorCm,
                Towed: type.Kind == LiftKind.TBar, Name: lift.Name));
        }
        foreach (var lot in state.ParkingLots)
        {
            var pad = Pad(state, lot.TerrainEditId);
            var lift = state.Lifts.FirstOrDefault(l => l.Id == lot.LiftId);
            var station = lift is null ? null : Pad(state, lift.Valley.TerrainEditId);
            if (pad is null || lift is null || station is null) continue;
            hubs.Add(new NetworkHub(lot.Id, HubKind.Parking, lot.Id, pad));
            long length = WalkLengthCm(pad, station);
            links.Add(new NetworkLink(LegKind.Walk, lot.Id, lot.Id, lift.Valley.Id, length, length, 0));
        }
        return (hubs, links);
    }

    /// <summary>Walking distance between a parking lot and a station: centre to centre.</summary>
    public static long WalkLengthCm(TerrainPad a, TerrainPad b)
    {
        long dx = a.CenterX - b.CenterX, dz = a.CenterZ - b.CenterZ;
        return Math.Max(100, FixedMath.ISqrt(dx * dx + dz * dz));
    }

    public static LiftType? FindType(WorldState state, string typeId) => state.LiftTypes.FirstOrDefault(t => t.Id == typeId);

    public static TerrainPad? Pad(WorldState state, int terrainEditId) => state.TerrainEdits.FirstOrDefault(e => e.Id == terrainEditId)?.Pad;

    public static LiftOperator? FindOperator(WorldState state, string? operatorId) =>
        operatorId is null ? null : state.Operators.FirstOrDefault(o => o.Id == operatorId);
}
