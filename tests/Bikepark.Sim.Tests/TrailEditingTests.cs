using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

public class TrailEditingTests
{
    // Demo valley + demo_lift_network.json: Flow Country 11, Red Rocket 12; the hiking route is a scenario path.
    private const int FlowCountry = 11;
    private const int RedRocket = 12;

    private static Simulation Valley()
    {
        var sim = TestWorlds.RunLift(1337, 0, TestWorlds.DemoLiftNetwork());
        sim.Step();
        return sim;
    }

    private static string? Reject(Simulation sim, ICommand command)
    {
        sim.Events.Clear();
        sim.Commands.Enqueue(command);
        sim.Step();
        return sim.Events.Pending.OfType<CommandRejected>().SingleOrDefault()?.Reason;
    }

    private static Way Way(Simulation sim, int id) => sim.State.Ways.Single(w => w.Id == id);

    private static long Length(Simulation sim, int id) => sim.Network.Geometry(id).LengthCm;

    [Fact]
    public void Split_MakesTwoConnectedTrails_ThatRidersUse()
    {
        var sim = Valley();
        long length = Length(sim, RedRocket);
        Assert.Null(Reject(sim, new SplitTrailCommand(RedRocket, length / 2)));
        var upper = Way(sim, RedRocket);
        var lower = sim.State.Ways.Single(w => w.Name == "Red Rocket 2");
        Assert.Equal(new WayJoin(RedRocket, Length(sim, RedRocket)), lower.StartJoin);
        Assert.Null(upper.EndJoin);
        Assert.InRange(Length(sim, RedRocket) + Length(sim, lower.Id), length * 97 / 100, length * 103 / 100);
        Assert.True(sim.Network.IsConnected(upper));
        Assert.True(sim.Network.IsConnected(lower));

        sim.RunDays(1);
        Assert.True(upper.Stats.Runs > 0);
        Assert.True(lower.Stats.Runs > 0);
    }

    [Fact]
    public void Edits_AreRejected_OnPathsAndTooCloseToTheEnds()
    {
        var sim = Valley();
        int hiking = sim.State.Ways.Single(w => w.Kind == WayKind.AccessPath).Id;
        Assert.Contains("gravel path", Reject(sim, new SplitTrailCommand(hiking, 50_000)));
        Assert.Contains("at least", Reject(sim, new SplitTrailCommand(RedRocket, 1_000)));
        Assert.Contains("No such trail", Reject(sim, new RenaturalizeTrailCommand(999, 0, 1_000)));
    }

    [Fact]
    public void Renaturalize_LeavesLooseClosedPieces_AndDrawingTheGapAgainMakesOneTrail()
    {
        var sim = Valley();
        long length = Length(sim, RedRocket);
        var original = sim.Network.Geometry(RedRocket);
        long from = length * 40 / 100, to = length * 55 / 100;
        var gap = new List<PointCm>();
        for (int i = 0; i <= 4; i++)
        {
            var p = original.PositionAt(from + (to - from) * i / 4);
            gap.Add(new PointCm(p.X, p.Z));
        }

        Assert.Null(Reject(sim, new RenaturalizeTrailCommand(RedRocket, from, to)));
        var upper = Way(sim, RedRocket);
        var lower = sim.State.Ways.Single(w => w.Name == "Red Rocket 2");
        Assert.Null(upper.EndJoin);
        Assert.Null(lower.StartJoin);
        Assert.False(sim.Network.IsConnected(upper));
        Assert.False(sim.Network.IsConnected(lower));
        Assert.False(sim.Network.IsRideable(upper));

        // Riders never start on the loose pieces.
        long runsBefore = upper.Stats.Runs + lower.Stats.Runs;
        sim.RunTicks(GameTime.MinutesPerDay);
        Assert.Equal(runsBefore, upper.Stats.Runs + lower.Stats.Runs);
        Assert.True(Way(sim, FlowCountry).Stats.Runs > 0);

        // Drawing the gap again (built at once): it joins both pieces into one trail.
        var joined = new List<TrailsJoined>();
        sim.Events.Subscribe<TrailsJoined>(joined.Add);
        Assert.Null(Reject(sim, new BuildWayCommand(WayKind.Trail, "Gap", gap, Instant: true)));
        sim.Events.Dispatch();
        Assert.Equal(2, joined.Count);
        var trails = sim.State.Ways.Where(w => w.Kind == WayKind.Trail).Select(w => w.Name).Order().ToList();
        Assert.Equal(["Flow Country", "Red Rocket"], trails);
        Assert.True(sim.Network.IsConnected(Way(sim, RedRocket)));
        Assert.InRange(Length(sim, RedRocket), length * 95 / 100, length * 105 / 100);

        var loaded = SaveGame.Clone(sim.State);
        Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(loaded));
    }

    [Fact]
    public void RenaturalizingAWholeTrail_RemovesIt()
    {
        var sim = Valley();
        Assert.Null(Reject(sim, new RenaturalizeTrailCommand(RedRocket, 0, Length(sim, RedRocket))));
        Assert.DoesNotContain(sim.State.Ways, w => w.Id == RedRocket);
    }

    [Fact]
    public void Renaturalize_RemovesTheFeaturesInTheSection_AndMovesTheOnesBelow()
    {
        var features = System.Text.Json.JsonSerializer.Deserialize<List<TimedCommand>>(
                File.ReadAllText(Path.Combine(TestWorlds.RepoRoot(), "data", "scripts", "demo_features.json")), SimJson.Indented)!
            .Select(c => c with { Command = ((PlaceTrailFeatureCommand)c.Command) with { Instant = true } });
        var sim = TestWorlds.RunLift(1337, 0, TestWorlds.DemoLiftNetwork().Concat(features));
        sim.Step();
        var before = Way(sim, RedRocket).Features.Select(f => (f.TypeId, f.DistanceCm)).ToList();
        const long from = 15_000, to = 26_000;
        Assert.Null(Reject(sim, new RenaturalizeTrailCommand(RedRocket, from, to)));

        var upper = Way(sim, RedRocket).Features.Select(f => (f.TypeId, f.DistanceCm)).ToList();
        var lower = sim.State.Ways.Single(w => w.Name == "Red Rocket 2").Features.Select(f => (f.TypeId, f.DistanceCm)).ToList();
        Assert.Equal(before.Where(f => f.DistanceCm < from), upper);
        Assert.Equal(before.Where(f => f.DistanceCm >= to).Select(f => (f.TypeId, f.DistanceCm - to)), lower);
        Assert.True(upper.Count + lower.Count < before.Count);
    }

    [Fact]
    public void AGravelPathEndingOnATrail_SplitsIt()
    {
        var sim = Valley();
        var flow = sim.Network.Geometry(FlowCountry);
        var hiking = sim.Network.Geometry(sim.State.Ways.Single(w => w.Kind == WayKind.AccessPath).Id);
        // A path from the hiking route to the middle third of Flow Country (both ends snap): the first layout that plans.
        List<PointCm>? points = null;
        for (long t = flow.LengthCm / 3; t < flow.LengthCm * 2 / 3 && points is null; t += 2_000)
        {
            var trail = flow.PositionAt(t);
            for (long d = 0; d < hiking.LengthCm && points is null; d += 1_000)
            {
                var p = hiking.PositionAt(d);
                long dist2 = (long)(p.X - trail.X) * (p.X - trail.X) + (long)(p.Z - trail.Z) * (p.Z - trail.Z);
                if (dist2 < 4_000L * 4_000 || dist2 > 15_000L * 15_000) continue;
                List<PointCm> candidate = [new(p.X, p.Z), new(trail.X, trail.Z)];
                var plan = WayPlanner.Plan(sim.Terrain, sim.Network, sim.State.TrailRules, WayKind.AccessPath, candidate);
                if (plan.IsValid && plan.StartJoin is not null && plan.EndJoin?.WayId == FlowCountry) points = candidate;
            }
        }
        Assert.NotNull(points);
        Assert.Null(Reject(sim, new BuildWayCommand(WayKind.AccessPath, "Link", points!, Instant: true)));
        Assert.Contains(sim.State.Ways, w => w.Name == "Flow Country 2");
        Assert.True(sim.Network.IsConnected(Way(sim, FlowCountry)));
    }

    // ---------------------------------------------------------------- platforms

    [Fact]
    public void Platforms_AreHubsWaysCanAttachTo_AndCanBeRemoved()
    {
        var sim = Valley();
        var flow = sim.Network.Geometry(FlowCountry);
        var spot = flow.PositionAt(flow.LengthCm / 3);
        // Somewhere open next to the trail.
        PointCm? center = null;
        foreach (int dx in new[] { 4_000, -4_000, 6_000, -6_000 })
            foreach (int dz in new[] { 0, 4_000, -4_000 })
            {
                var c = new PointCm(spot.X + dx, spot.Z + dz);
                if (center is null && StructurePlanner.PlanPlatform(sim.Terrain, sim.Network, sim.State, c, new PointCm(c.X + 100, c.Z)).IsValid)
                    center = c;
            }
        Assert.NotNull(center);
        Assert.Null(Reject(sim, new BuildPlatformCommand("Link", center!.Value, new PointCm(center.Value.X + 100, center.Value.Z))));
        var platform = Assert.Single(sim.State.Platforms);
        Assert.Contains(sim.Network.Hubs, h => h.Id == platform.Id && h.Kind == HubKind.Platform);
        Assert.Null(Reject(sim, new DeletePlatformCommand(platform.Id)));
        Assert.Empty(sim.State.Platforms);
        Assert.DoesNotContain(sim.State.TerrainEdits, e => e.OwnerId == platform.Id);
    }

    [Fact]
    public void Platforms_NeedYourLand()
    {
        var sim = new Simulation(Bikepark.Sim.Scenarios.ScenarioLoader.CreateWorld(TestWorlds.CareerScenario()));
        sim.Step();
        var plan = StructurePlanner.PlanPlatform(sim.Terrain, sim.Network, sim.State, new PointCm(30_000, 90_000), new PointCm(30_100, 90_000));
        Assert.Contains(plan.Issues, i => i.Code == "notYourLand");
    }
}
