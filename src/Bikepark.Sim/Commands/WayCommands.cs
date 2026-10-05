using Bikepark.Sim.Events;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Commands;

/// <summary>
/// Builds an access path or trail instantly (Phase 2 debug build, free). Points are the player's raw clicks;
/// <see cref="WayPlanner"/> orients them, snaps the ends onto the network and validates.
/// </summary>
public sealed record BuildWayCommand(WayKind Kind, string Name, List<PointCm> Points) : ICommand
{
    public const int MaxNameLength = 40;

    public string? Validate(SimContext ctx)
    {
        if (Points is null) return "No points given.";
        if (Name is { Length: > MaxNameLength }) return $"Name cannot exceed {MaxNameLength} characters.";
        return Plan(ctx).FirstError;
    }

    public void Apply(SimContext ctx)
    {
        var plan = Plan(ctx);
        var state = ctx.State;
        int id = state.AllocateEntityId();
        int number = state.Ways.Count(w => w.Kind == Kind) + 1;
        var way = new Way
        {
            Id = id,
            Kind = Kind,
            Name = string.IsNullOrWhiteSpace(Name) ? (Kind == WayKind.AccessPath ? $"Path {number}" : $"Trail {number}") : Name.Trim(),
            Points = plan.Points,
            StartJoin = plan.StartJoin,
            EndJoin = plan.EndJoin,
        };
        state.Ways.Add(way);
        state.WaysRevision++;
        ctx.Publish(new WayBuilt(ctx.Tick, id));
    }

    private WayPlan Plan(SimContext ctx) =>
        WayPlanner.Plan(ctx.Terrain, ctx.Network, ctx.State.TrailRules, Kind, Points);

    // Value equality over the points, so identical commands compare equal (records compare lists by reference).
    public bool Equals(BuildWayCommand? other) =>
        other is not null && Kind == other.Kind && Name == other.Name
        && (ReferenceEquals(Points, other.Points) || (Points is not null && other.Points is not null && Points.SequenceEqual(other.Points)));

    public override int GetHashCode() => HashCode.Combine(Kind, Name, Points?.Count ?? -1);
}

/// <summary>Removes a way. Rejected while other ways attach to it. Riders using it start over at the base.</summary>
public sealed record DeleteWayCommand(int WayId) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var ways = ctx.State.Ways;
        if (ways.All(w => w.Id != WayId)) return "No such path or trail.";
        var dependent = ways.FirstOrDefault(w => w.StartJoin?.WayId == WayId || w.EndJoin?.WayId == WayId);
        return dependent is null ? null : $"'{dependent.Name}' is attached to it; remove that first.";
    }

    public void Apply(SimContext ctx)
    {
        var state = ctx.State;
        state.Ways.RemoveAll(w => w.Id == WayId);
        state.WaysRevision++;
        Systems.RiderSystem.ResetRidersUsing(ctx, WayId);
        ctx.Publish(new WayDeleted(ctx.Tick, WayId));
    }
}
