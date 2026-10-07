using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Commands;

/// <summary>
/// Plans an access path or trail: it is added unbuilt and a crew job is queued that fells the trees in its corridor and
/// then digs it (see <see cref="Jobs.QueueWay"/>). Scenario ways and <paramref name="Instant"/> (debug) ways are built
/// at once. Points are the player's raw clicks; <see cref="WayPlanner"/> orients them, snaps the ends onto the network
/// and validates.
/// </summary>
public sealed record BuildWayCommand(
    WayKind Kind, string Name, List<PointCm> Points, WayOrigin Origin = WayOrigin.Player, bool Instant = false) : ICommand
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
            StartHubId = plan.StartHubId,
            EndHubId = plan.EndHubId,
            Origin = Origin,
            Built = Instant || Origin == WayOrigin.Scenario,
        };
        if (!way.Built)
            Jobs.QueueWay(ctx, way, plan.Geometry!);
        state.Ways.Add(way);
        state.WaysRevision++;
        ctx.Publish(new WayBuilt(ctx.Tick, id));
    }

    private WayPlan Plan(SimContext ctx) =>
        WayPlanner.Plan(ctx.Terrain, ctx.Network, ctx.State.TrailRules, Kind, Points);

    // Value equality over the points, so identical commands compare equal (records compare lists by reference).
    public bool Equals(BuildWayCommand? other) =>
        other is not null && Kind == other.Kind && Name == other.Name && Origin == other.Origin && Instant == other.Instant
        && (ReferenceEquals(Points, other.Points) || (Points is not null && other.Points is not null && Points.SequenceEqual(other.Points)));

    public override int GetHashCode() => HashCode.Combine(Kind, Name, Points?.Count ?? -1);
}

/// <summary>
/// Removes a way (a planned one: cancels its job and those of its planned features). Rejected while other ways attach
/// to it. Riders using it start over at the base.
/// </summary>
public sealed record DeleteWayCommand(int WayId) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var ways = ctx.State.Ways;
        if (ways.All(w => w.Id != WayId)) return "No such path or trail.";
        if (ways.First(w => w.Id == WayId).Origin == WayOrigin.Scenario) return "This route belongs to the scenario and cannot be removed.";
        var dependent = ways.FirstOrDefault(w => w.StartJoin?.WayId == WayId || w.EndJoin?.WayId == WayId);
        return dependent is null ? null : $"'{dependent.Name}' is attached to it; remove that first.";
    }

    public void Apply(SimContext ctx)
    {
        var state = ctx.State;
        foreach (var job in state.Jobs.Where(j => j.WayId == WayId && j.Kind != JobKind.FellTrees).ToList())
            Jobs.Cancel(ctx, job);
        state.Ways.RemoveAll(w => w.Id == WayId);
        state.WaysRevision++;
        Systems.RiderSystem.ResetRidersUsing(ctx, WayId);
        ctx.Publish(new WayDeleted(ctx.Tick, WayId));
    }
}
