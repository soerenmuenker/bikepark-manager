using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Commands;

/// <summary>
/// Builds a lift instantly (Phase 3 debug build, free): a valley station and a mountain plateau, each a flattened pad,
/// and the line between them. With <see cref="OperatorId"/> the lift is run by that lift company and the park rents
/// bike access in tiers (starting at <see cref="InitialTier"/>); otherwise the park owns it and all carriers take bikes.
/// Plateau size 0 = the lift type's default.
/// </summary>
public sealed record BuildLiftCommand(
    string TypeId,
    string Name,
    PointCm Valley,
    PointCm Mountain,
    int PlateauLengthMeters = 0,
    int PlateauWidthMeters = 0,
    string? OperatorId = null,
    int InitialTier = 0) : ICommand
{
    public const int MaxNameLength = 40;

    public string? Validate(SimContext ctx)
    {
        if (Name is { Length: > MaxNameLength }) return $"Name cannot exceed {MaxNameLength} characters.";
        return Plan(ctx).FirstError;
    }

    public void Apply(SimContext ctx)
    {
        var plan = Plan(ctx);
        var state = ctx.State;
        int id = state.AllocateEntityId();
        var valleyEdit = new TerrainEdit(state.AllocateEntityId(), id, plan.ValleyPad!);
        var mountainEdit = new TerrainEdit(state.AllocateEntityId(), id, plan.MountainPad!);
        var op = LiftNetwork.FindOperator(state, OperatorId);
        var lift = new Lift
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(Name) ? $"Lift {state.Lifts.Count + 1}" : Name.Trim(),
            TypeId = TypeId,
            OperatorId = op?.Id,
            Valley = new LiftStation { Id = state.AllocateEntityId(), TerrainEditId = valleyEdit.Id },
            Mountain = new LiftStation { Id = state.AllocateEntityId(), TerrainEditId = mountainEdit.Id },
            BikeCarrierPermille = op is null ? 1000 : op.BikeAccessTiers[InitialTier].BikeCarrierPermille,
            BikeAccess = op is null ? null : new BikeAccess { TierIndex = InitialTier },
        };
        state.TerrainEdits.Add(valleyEdit);
        state.TerrainEdits.Add(mountainEdit);
        state.TerrainRevision++;
        state.Lifts.Add(lift);
        ctx.Publish(new LiftBuilt(ctx.Tick, id));
    }

    private LiftPlan Plan(SimContext ctx) =>
        StructurePlanner.PlanLift(ctx.Terrain, ctx.Network, ctx.State, TypeId, Valley, Mountain,
            PlateauLengthMeters, PlateauWidthMeters, OperatorId, InitialTier);
}

/// <summary>Builds a parking lot instantly next to a valley station. Its length points towards <see cref="Toward"/>.</summary>
public sealed record BuildParkingLotCommand(string Name, PointCm Center, PointCm Toward, int Spaces) : ICommand
{
    public const int MaxNameLength = 40;

    public string? Validate(SimContext ctx)
    {
        if (Name is { Length: > MaxNameLength }) return $"Name cannot exceed {MaxNameLength} characters.";
        return Plan(ctx).FirstError;
    }

    public void Apply(SimContext ctx)
    {
        var plan = Plan(ctx);
        var state = ctx.State;
        int id = state.AllocateEntityId();
        var edit = new TerrainEdit(state.AllocateEntityId(), id, plan.Pad!);
        state.TerrainEdits.Add(edit);
        state.TerrainRevision++;
        state.ParkingLots.Add(new ParkingLot
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(Name) ? $"Parking {state.ParkingLots.Count + 1}" : Name.Trim(),
            Spaces = Spaces,
            TerrainEditId = edit.Id,
            LiftId = plan.LiftId,
        });
        ctx.Publish(new ParkingLotBuilt(ctx.Tick, id));
    }

    private ParkingPlan Plan(SimContext ctx) =>
        StructurePlanner.PlanParking(ctx.Terrain, ctx.Network, ctx.State, Center, Toward, Spaces);
}

/// <summary>
/// Books a different bike access tier with the lift company. It takes effect at the next opening (the active tier
/// is billed for the whole day).
/// </summary>
public sealed record SetLiftBikeAccessCommand(int LiftId, int TierIndex) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var lift = ctx.State.Lifts.FirstOrDefault(l => l.Id == LiftId);
        if (lift is null) return "No such lift.";
        var op = LiftNetwork.FindOperator(ctx.State, lift.OperatorId);
        if (op is null || lift.BikeAccess is null) return "The park runs this lift itself; there is no bike access to book.";
        if (TierIndex < 0 || TierIndex >= op.BikeAccessTiers.Count) return "No such bike access tier.";
        return null;
    }

    public void Apply(SimContext ctx)
    {
        var lift = ctx.State.Lifts.First(l => l.Id == LiftId);
        lift.BikeAccess!.PendingTierIndex = TierIndex == lift.BikeAccess.TierIndex ? null : TierIndex;
        ctx.Publish(new BikeAccessBooked(ctx.Tick, LiftId, TierIndex));
    }
}

/// <summary>Removes a lift and its pads. Rejected while ways or parking lots attach to its stations.</summary>
public sealed record DeleteLiftCommand(int LiftId) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var state = ctx.State;
        var lift = state.Lifts.FirstOrDefault(l => l.Id == LiftId);
        if (lift is null) return "No such lift.";
        int[] hubs = [lift.Valley.Id, lift.Mountain.Id];
        var way = state.Ways.FirstOrDefault(w => hubs.Contains(w.StartHubId) || hubs.Contains(w.EndHubId));
        if (way is not null) return $"'{way.Name}' is attached to it; remove that first.";
        var lot = state.ParkingLots.FirstOrDefault(p => p.LiftId == LiftId);
        return lot is null ? null : $"'{lot.Name}' serves it; remove that first.";
    }

    public void Apply(SimContext ctx)
    {
        var state = ctx.State;
        var lift = state.Lifts.First(l => l.Id == LiftId);
        Systems.RiderSystem.ResetRidersUsing(ctx, LiftId);
        state.Lifts.Remove(lift);
        state.TerrainEdits.RemoveAll(e => e.OwnerId == LiftId);
        state.TerrainRevision++;
        ctx.Publish(new LiftDeleted(ctx.Tick, LiftId));
    }
}

/// <summary>Removes a parking lot and its pad. Rejected while ways attach to it.</summary>
public sealed record DeleteParkingLotCommand(int ParkingLotId) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var state = ctx.State;
        if (state.ParkingLots.All(p => p.Id != ParkingLotId)) return "No such parking lot.";
        var way = state.Ways.FirstOrDefault(w => w.StartHubId == ParkingLotId || w.EndHubId == ParkingLotId);
        return way is null ? null : $"'{way.Name}' is attached to it; remove that first.";
    }

    public void Apply(SimContext ctx)
    {
        var state = ctx.State;
        Systems.RiderSystem.ResetRidersUsing(ctx, ParkingLotId);
        state.ParkingLots.RemoveAll(p => p.Id == ParkingLotId);
        state.TerrainEdits.RemoveAll(e => e.OwnerId == ParkingLotId);
        state.TerrainRevision++;
        ctx.Publish(new ParkingLotDeleted(ctx.Tick, ParkingLotId));
    }
}
