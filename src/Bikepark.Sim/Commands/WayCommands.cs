using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Commands;

/// <summary>
/// Plans an access path or trail: it is added unbuilt and a crew job is queued that fells the trees in its corridor and
/// then digs it (see <see cref="Jobs.QueueWay"/>). Scenario ways and <paramref name="Instant"/> (debug) ways are built
/// at once. Points are the player's raw clicks; <see cref="WayPlanner"/> orients them, snaps the ends onto the network
/// and validates.
/// </summary>
public sealed record BuildWayCommand(
    WayKind Kind, string Name, List<PointCm> Points, WayOrigin Origin = WayOrigin.Player, bool Instant = false,
    WaySurface Surface = WaySurface.Gravel, List<RockFeaturePlacement>? RockFeatures = null, int DeckWidthCm = 0) : ICommand
{
    public const int MaxNameLength = 40;
    public const int MaxDeckWidthCm = 3000;

    /// <summary>How far a pad reaches from a bridge's end, along the bridge.</summary>
    private const int DeckPadHalfLengthCm = 600;

    public string? Validate(SimContext ctx)
    {
        if (Points is null) return "No points given.";
        if (Name is { Length: > MaxNameLength }) return $"Name cannot exceed {MaxNameLength} characters.";
        if (Kind == WayKind.Trail && !string.IsNullOrWhiteSpace(Name) && RenameTrailCommand.CannotName(ctx.State, Name) is { } taken) return taken;
        if (DeckWidthCm != 0 && (Origin != WayOrigin.Scenario || Kind != WayKind.AccessPath)) return "Only a scenario's roads can be bridges.";
        if (DeckWidthCm is < 0 or > MaxDeckWidthCm) return $"A bridge is at most {MaxDeckWidthCm / 100} m wide.";
        var plan = Plan(ctx);
        if (plan.FirstError is { } error) return error;
        return Kind == WayKind.Trail && plan.Geometry is { } geometry && Origin != WayOrigin.Scenario
            ? Trails.RockFeatures.CannotCross(ctx.Terrain, geometry, ctx.State.TrailRules, Choice(ctx), RockFeatures)
            : null;
    }

    /// <summary>The feature types over rock this build may use: what the park's level allows, or all for a debug instant build.</summary>
    private RockFeatureChoice Choice(SimContext ctx)
    {
        if (Instant) return new RockFeatureChoice(ctx.State.TrailFeatureTypes, _ => true);
        int level = Reputation.ParkProgress.CurrentLevel(ctx.State, ctx.Network);
        var state = ctx.State;
        return new RockFeatureChoice(state.TrailFeatureTypes, t => TrailFeatures.CannotPlace(state, level, t) is null);
    }

    public void Apply(SimContext ctx)
    {
        var plan = Plan(ctx);
        var state = ctx.State;
        int id = state.AllocateEntityId();
        var way = new Way
        {
            Id = id,
            Kind = Kind,
            Name = !string.IsNullOrWhiteSpace(Name) ? Name.Trim() : Kind == WayKind.Trail ? DefaultTrailName(state) : "",
            Points = plan.Points,
            StartJoin = plan.StartJoin,
            EndJoin = plan.EndJoin,
            StartHubId = plan.StartHubId,
            EndHubId = plan.EndHubId,
            Origin = Origin,
            Surface = Surface,
            Built = Instant || Origin == WayOrigin.Scenario,
            DeckWidthCm = DeckWidthCm,
        };
        if (DeckWidthCm > 0)
            AddDeckPads(state, id, plan.Geometry!);
        if (!way.Built)
            Jobs.QueueWay(ctx, way, plan.Geometry!);
        if (Origin != WayOrigin.Scenario)
            foreach (var placement in (RockFeatures ?? []).OrderBy(p => p.StartCm))
            {
                var feature = new TrailFeature { Id = state.AllocateEntityId(), TypeId = placement.TypeId, DistanceCm = placement.StartCm, LengthCm = placement.LengthCm, RampCm = placement.RampCm, Built = way.Built };
                way.Features.Add(feature);
                if (!way.Built) Jobs.QueueFeature(ctx, id, feature, TrailFeatures.FindType(state.TrailFeatureTypes, placement.TypeId)!);
            }
        // Through a stone field the trail gets a rock garden: the stones are there already, so there is nothing to build.
        if (Kind == WayKind.Trail && Origin != WayOrigin.Scenario && state.TrailFeatureTypes.FirstOrDefault(TrailFeatures.ComesFromStoneField) is { } garden)
        {
            foreach (var (from, to) in Trails.RockFeatures.StoneGardens(plan.Geometry!, RockFeatures))
                way.Features.Add(new TrailFeature { Id = state.AllocateEntityId(), TypeId = garden.Id, DistanceCm = from, LengthCm = to - from, Built = true, FromStoneField = true });
            way.Features.Sort((a, b) => a.DistanceCm.CompareTo(b.DistanceCm));
        }
        state.Ways.Add(way);
        state.WaysRevision++;
        ctx.Publish(new WayBuilt(ctx.Tick, id));
        if (way.Built) WayEditing.OnBuilt(ctx, way);
    }

    /// <summary>The ground meets the deck at both ends: it is what the ways joining there follow.</summary>
    private void AddDeckPads(State.WorldState state, int wayId, WayGeometry deck)
    {
        var (dirX, dirZ) = TerrainPad.Direction(deck.Xs[^1] - deck.Xs[0], deck.Zs[^1] - deck.Zs[0]);
        foreach (int i in new[] { 0, deck.SampleCount - 1 })
        {
            var pad = new TerrainPad(deck.Xs[i], deck.Zs[i], DeckPadHalfLengthCm, DeckWidthCm / 2 + 100, dirX, dirZ, deck.Ys[i], state.LiftRules.EmbankmentGradient);
            state.TerrainEdits.Add(new TerrainEdit(state.AllocateEntityId(), wayId, pad));
        }
        state.TerrainRevision++;
    }

    /// <summary>"Trail 1", "Trail 2", ...: the first number no way uses yet.</summary>
    public static string DefaultTrailName(State.WorldState state)
    {
        for (int n = state.Ways.Count(w => w.Kind == WayKind.Trail) + 1; ; n++)
            if (state.Ways.All(w => w.Name != $"Trail {n}")) return $"Trail {n}";
    }

    private WayPlan Plan(SimContext ctx) =>
        WayPlanner.Plan(ctx.Terrain, ctx.Network, ctx.State.TrailRules, Kind, Points,
            Origin == WayOrigin.Scenario ? null : Land.LandMath.OwnedPredicate(ctx.State), mayStandAlone: Origin == WayOrigin.Scenario, rockChoice: Choice(ctx), deck: DeckWidthCm > 0);

    // Value equality over the points, so identical commands compare equal (records compare lists by reference).
    public bool Equals(BuildWayCommand? other) =>
        other is not null && Kind == other.Kind && Name == other.Name && Origin == other.Origin && Instant == other.Instant && Surface == other.Surface && DeckWidthCm == other.DeckWidthCm
        && (RockFeatures ?? []).SequenceEqual(other.RockFeatures ?? [])
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
        return dependent is null ? null : $"{dependent.Label} is attached to it; remove that first.";
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

/// <summary>
/// Gives a section of a built trail or gravel path back to nature (instant, free): the way is gone there and trees grow
/// back. What is left above and below has a loose end (a trail stays closed until it is connected again); a trail cut in
/// two becomes "Part 1" and "Part 2".
/// </summary>
public sealed record RenaturalizeTrailCommand(int WayId, long FromCm, long ToCm) : ICommand
{
    public string? Validate(SimContext ctx) =>
        WayEditing.CannotRenaturalize(ctx.State, ctx.Network, ctx.State.Ways.FirstOrDefault(w => w.Id == WayId), Math.Min(FromCm, ToCm), Math.Max(FromCm, ToCm));

    public void Apply(SimContext ctx) =>
        WayEditing.Renaturalize(ctx, ctx.State.Ways.First(w => w.Id == WayId), Math.Min(FromCm, ToCm), Math.Max(FromCm, ToCm));
}

/// <summary>Renames a trail (gravel paths have no names). Trail names are unique.</summary>
public sealed record RenameTrailCommand(int WayId, string Name) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var way = ctx.State.Ways.FirstOrDefault(w => w.Id == WayId);
        if (way is null) return "No such trail.";
        if (way.Kind != WayKind.Trail) return "Gravel paths have no names.";
        if (Sponsors.SponsorMath.DealOn(ctx.State, WayId) is not null) return $"{way.Name} carries its sponsor's name while the deal runs.";
        return CannotName(ctx.State, Name, WayId);
    }

    /// <summary>Why a trail can't be called this (empty, too long, taken by another way), or null.</summary>
    public static string? CannotName(State.WorldState state, string? name, int wayId = 0)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0) return "Enter a name.";
        if (name.Length > BuildWayCommand.MaxNameLength) return $"Name cannot exceed {BuildWayCommand.MaxNameLength} characters.";
        if (state.Ways.Any(w => w.Id != wayId && w.Name == name)) return $"There is already a trail called {name}.";
        return null;
    }

    public void Apply(SimContext ctx)
    {
        var way = ctx.State.Ways.First(w => w.Id == WayId);
        way.Name = Name.Trim();
        ctx.Publish(new TrailRenamed(ctx.Tick, WayId, way.Name));
    }
}
