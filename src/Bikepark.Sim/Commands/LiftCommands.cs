using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Commands;

/// <summary>
/// Builds a lift: a valley station and a mountain plateau, each a flattened pad, and the line between them. With
/// <see cref="OperatorId"/> the lift is run by that lift company and the park rents bike access in tiers (starting at
/// <see cref="InitialTier"/>); otherwise the park owns it and all carriers take bikes. A lift the park builds itself
/// needs the type's park level, costs its build price (paid at once) and runs from the next opening after the
/// contractor's <see cref="LiftType.BuildDays"/>; both stations must be on the park's land. Scenario lifts
/// (<see cref="Origin"/>) and <see cref="Instant"/> (debug) builds are free and run at once; a scenario lift can start
/// <see cref="Derelict"/> (out of service until restored). Plateau size 0 = the lift type's default.
/// </summary>
public sealed record BuildLiftCommand(
    string TypeId,
    string Name,
    PointCm Valley,
    PointCm Mountain,
    int PlateauLengthMeters = 0,
    int PlateauWidthMeters = 0,
    string? OperatorId = null,
    int InitialTier = 0,
    WayOrigin Origin = WayOrigin.Player,
    bool Derelict = false,
    bool Instant = false) : ICommand
{
    public const int MaxNameLength = 40;

    /// <summary>The park builds and pays for it (not a scenario or debug lift, not a company lift).</summary>
    private bool Contracted => OperatorId is null && Origin == WayOrigin.Player && !Instant;

    public string? Validate(SimContext ctx)
    {
        if (Name is { Length: > MaxNameLength }) return $"Name cannot exceed {MaxNameLength} characters.";
        if (Derelict && Origin != WayOrigin.Scenario) return "Only scenario lifts can start derelict.";
        if (Contracted && LiftNetwork.FindType(ctx.State, TypeId) is { } type
            && LiftWorks.CannotBuild(ctx.State, Reputation.ParkProgress.CurrentLevel(ctx.State, ctx.Network), type) is { } locked)
            return locked;
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
            Derelict = Derelict,
        };
        if (Derelict) lift.BikeCarrierPermille = 0;
        long cost = 0;
        if (Contracted)
        {
            // The contractor builds it; until then it carries nobody.
            cost = plan.Type!.BuildCostCents;
            state.Finance.Spend(cost);
            state.Finance.TotalLiftBuildCents += cost;
            lift.ReadyTick = LiftWorks.ReadyTick(state, ctx.Tick, plan.Type.BuildDays);
            lift.BikeCarrierPermille = 0;
        }
        state.TerrainEdits.Add(valleyEdit);
        state.TerrainEdits.Add(mountainEdit);
        state.TerrainRevision++;
        state.Lifts.Add(lift);
        ctx.Publish(new LiftBuilt(ctx.Tick, id));
        if (lift.ReadyTick > 0)
            ctx.Publish(new LiftConstructionStarted(ctx.Tick, id, lift.ReadyTick, cost, Restoration: false));
    }

    private LiftPlan Plan(SimContext ctx) =>
        StructurePlanner.PlanLift(ctx.Terrain, ctx.Network, ctx.State, TypeId, Valley, Mountain,
            PlateauLengthMeters, PlateauWidthMeters, OperatorId, InitialTier, scenario: Origin == WayOrigin.Scenario);
}

/// <summary>Builds a parking lot instantly next to a valley station. Its length points towards <see cref="Toward"/>.</summary>
public sealed record BuildParkingLotCommand(string Name, PointCm Center, PointCm Toward, int Spaces, WayOrigin Origin = WayOrigin.Player) : ICommand
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
        StructurePlanner.PlanParking(ctx.Terrain, ctx.Network, ctx.State, Center, Toward, Spaces, scenario: Origin == WayOrigin.Scenario);
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
        if (TierIndex > 0 && !LiftWorks.StationsOwned(ctx.State, lift))
            return $"Buy the land at both stations of {lift.Name} first (Land menu): only then {op.Name} rents you bike access.";
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

/// <summary>
/// Restores a derelict lift the park owns (on its land): a contractor fixes it for half the type's build price; it runs
/// from the next opening after <see cref="LiftType.RestoreDays"/>.
/// </summary>
public sealed record RestoreLiftCommand(int LiftId) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var state = ctx.State;
        var lift = state.Lifts.FirstOrDefault(l => l.Id == LiftId);
        if (lift is null) return "No such lift.";
        if (!lift.Derelict) return $"{lift.Name} is not derelict.";
        if (lift.ReadyTick > 0) return $"{lift.Name} is already being restored.";
        if (lift.OperatorId is not null) return $"{lift.Name} belongs to a lift company.";
        if (!LiftWorks.StationsOwned(state, lift)) return $"{lift.Name} stands on land the park doesn't own.";
        var type = LiftNetwork.FindType(state, lift.TypeId);
        if (type is null) return "Unknown lift type.";
        if (state.Finance.MoneyCents < type.RestoreCostCents) return $"Not enough money: restoring costs {type.RestoreCostCents / 100:N0} €.";
        return null;
    }

    public void Apply(SimContext ctx)
    {
        var state = ctx.State;
        var lift = state.Lifts.First(l => l.Id == LiftId);
        var type = LiftNetwork.FindType(state, lift.TypeId)!;
        state.Finance.Spend(type.RestoreCostCents);
        state.Finance.TotalLiftBuildCents += type.RestoreCostCents;
        lift.ReadyTick = LiftWorks.ReadyTick(state, ctx.Tick, type.RestoreDays);
        ctx.Publish(new LiftConstructionStarted(ctx.Tick, lift.Id, lift.ReadyTick, type.RestoreCostCents, Restoration: true));
    }
}

/// <summary>Contractor work on lifts and who owns the ground under them.</summary>
public static class LiftWorks
{
    /// <summary>The lift warm-up of the opening <paramref name="days"/> days after today (0 days: tomorrow's at the earliest).</summary>
    public static long ReadyTick(State.WorldState state, long tick, int days)
    {
        long day = Core.GameTime.Day(tick) + Math.Max(1, days);
        return Core.GameTime.TicksForDays(day) + state.Rules.OpenMinute - state.Rules.LiftWarmupMinutes;
    }

    /// <summary>Why the park can't have a contractor build this lift type now (level, money), or null.</summary>
    public static string? CannotBuild(State.WorldState state, int level, LiftType type)
    {
        if (level < type.RequiredLevel) return $"A {type.Name} needs park level {type.RequiredLevel} (you are level {level}).";
        if (state.Finance.MoneyCents < type.BuildCostCents) return $"Not enough money: a {type.Name} costs {type.BuildCostCents / 100:N0} €.";
        return null;
    }

    /// <summary>Both station pads stand on the park's land.</summary>
    public static bool StationsOwned(State.WorldState state, Lift lift) =>
        new[] { lift.Valley, lift.Mountain }.All(station =>
            state.TerrainEdits.FirstOrDefault(e => e.Id == station.TerrainEditId) is not { } edit
            || Land.LandMath.IsOwned(state, edit.Pad.CenterX, edit.Pad.CenterZ));
}

/// <summary>Builds a small square gravel platform (instant, free) where paths and trails can start and end.</summary>
public sealed record BuildPlatformCommand(string Name, PointCm Center, PointCm Toward) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        if (Name is { Length: > BuildLiftCommand.MaxNameLength }) return $"Name cannot exceed {BuildLiftCommand.MaxNameLength} characters.";
        return StructurePlanner.PlanPlatform(ctx.Terrain, ctx.Network, ctx.State, Center, Toward).FirstError;
    }

    public void Apply(SimContext ctx)
    {
        var plan = StructurePlanner.PlanPlatform(ctx.Terrain, ctx.Network, ctx.State, Center, Toward);
        var state = ctx.State;
        int id = state.AllocateEntityId();
        var edit = new TerrainEdit(state.AllocateEntityId(), id, plan.Pad!);
        state.TerrainEdits.Add(edit);
        state.TerrainRevision++;
        state.Platforms.Add(new Platform
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(Name) ? $"Platform {state.Platforms.Count + 1}" : Name.Trim(),
            TerrainEditId = edit.Id,
        });
        ctx.Publish(new PlatformBuilt(ctx.Tick, id));
    }
}

/// <summary>Removes a gravel platform. Rejected while paths or trails attach to it.</summary>
public sealed record DeletePlatformCommand(int PlatformId) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var state = ctx.State;
        if (state.Platforms.All(p => p.Id != PlatformId)) return "No such platform.";
        var way = state.Ways.FirstOrDefault(w => w.StartHubId == PlatformId || w.EndHubId == PlatformId);
        return way is null ? null : $"'{way.Name}' is attached to it; remove that first.";
    }

    public void Apply(SimContext ctx)
    {
        var state = ctx.State;
        Systems.RiderSystem.ResetRidersUsing(ctx, PlatformId);
        state.Platforms.RemoveAll(p => p.Id == PlatformId);
        state.TerrainEdits.RemoveAll(e => e.OwnerId == PlatformId);
        state.TerrainRevision++;
        ctx.Publish(new PlatformDeleted(ctx.Tick, PlatformId));
    }
}
