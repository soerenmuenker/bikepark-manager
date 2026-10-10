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
    public void Renaturalize_IsRejected_ForUnknownOrPlannedWays_AndOffYourLand()
    {
        var sim = Valley();
        Assert.Contains("No such", Reject(sim, new RenaturalizeTrailCommand(999, 0, 1_000)));
        var plan = WayPlanner.Plan(sim.Terrain, sim.Network, sim.State.TrailRules, WayKind.Trail,
            Sample(sim.Network.Geometry(RedRocket), 0, Length(sim, RedRocket) / 2));
        Assert.Null(Reject(sim, new BuildWayCommand(WayKind.Trail, "Planned", plan.Points)));
        int planned = sim.State.Ways.Single(w => w.Name == "Planned").Id;
        Assert.Contains("only planned", Reject(sim, new RenaturalizeTrailCommand(planned, 0, 1_000)));

        var career = new Simulation(Bikepark.Sim.Scenarios.ScenarioLoader.CreateWorld(TestWorlds.CareerScenario()));
        career.Step();
        var hiking = career.State.Ways.Single(w => w.Name == "Old Hiking Route");
        Assert.Contains("Not your land", Reject(career, new RenaturalizeTrailCommand(hiking.Id, 1_000, 5_000)));
    }

    [Fact]
    public void Renaturalize_CutsGravelPathsToo_WithoutNamingThem()
    {
        var sim = Valley();
        var path = sim.State.Ways.Single(w => w.Kind == WayKind.AccessPath);
        long length = Length(sim, path.Id);
        Assert.Null(Reject(sim, new RenaturalizeTrailCommand(path.Id, length / 2, length / 2 + 2_000)));
        var paths = sim.State.Ways.Where(w => w.Kind == WayKind.AccessPath).ToList();
        Assert.Equal(2, paths.Count);
        Assert.Equal("", paths[1].Name);
        Assert.Equal("a gravel path", paths[1].Label);
    }

    [Fact]
    public void Renaturalize_KeepsShortRemnants()
    {
        var sim = Valley();
        long length = Length(sim, RedRocket);
        Assert.Null(Reject(sim, new RenaturalizeTrailCommand(RedRocket, 500, length - 500)));
        Assert.InRange(Length(sim, RedRocket), 300, 800);
        Assert.InRange(Length(sim, sim.State.Ways.Single(w => w.Name == "Red Rocket Part 2").Id), 300, 800);
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
        var lower = sim.State.Ways.Single(w => w.Name == "Red Rocket Part 2");
        Assert.Equal("Red Rocket Part 1", upper.Name);
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

        // Drawing the gap again (built at once): it joins both pieces into one trail, which gets its name back.
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
        var lower = sim.State.Ways.Single(w => w.Name == "Red Rocket Part 2").Features.Select(f => (f.TypeId, f.DistanceCm)).ToList();
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
        Assert.Contains(sim.State.Ways, w => w.Name == "Flow Country Part 1" && w.Id == FlowCountry);
        Assert.Contains(sim.State.Ways, w => w.Name == "Flow Country Part 2");
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
        Assert.Null(Reject(sim, new RenaturalizeStructureCommand(platform.Id)));
        Assert.Empty(sim.State.Platforms);
        Assert.DoesNotContain(sim.State.TerrainEdits, e => e.OwnerId == platform.Id);
    }

    // ---------------------------------------------------------------- joins, names, structures

    [Fact]
    public void ATrailContinuingFromALooseEnd_LeavesInLine()
    {
        var sim = Valley();
        long length = Length(sim, RedRocket);
        Assert.Null(Reject(sim, new RenaturalizeTrailCommand(RedRocket, length / 2, length)));
        var end = sim.Network.Geometry(RedRocket);
        var joint = end.PositionAt(end.LengthCm);
        var before = end.PositionAt(end.LengthCm - 500);
        (long X, long Z) heading = (joint.X - before.X, joint.Z - before.Z);
        // Drawn from the loose end, turning sharply (about 60°) to a point lower down.
        int checkedTurns = 0;
        foreach (int sign in new[] { 1, -1 })
        {
            double a = sign * Math.PI / 3, c = Math.Cos(a), s = Math.Sin(a);
            long dx = (long)(heading.X * c - heading.Z * s), dz = (long)(heading.X * s + heading.Z * c);
            var target = new PointCm((int)(joint.X + dx * 6), (int)(joint.Z + dz * 6));
            if (sim.Terrain.HeightAt(target.X, target.Z) >= joint.Y) continue; // drawn uphill: the trail would run the other way
            checkedTurns++;
            var plan = WayPlanner.Plan(sim.Terrain, sim.Network, sim.State.TrailRules, WayKind.Trail, [new(joint.X + 300, joint.Z), target]);
            Assert.Equal(new WayJoin(RedRocket, end.LengthCm), plan.StartJoin);
            var g = plan.Geometry!;
            var p = g.PositionAt(300);
            (long X, long Z) leaving = (p.X - joint.X, p.Z - joint.Z);
            double cos = (heading.X * leaving.X + heading.Z * leaving.Z)
                         / Math.Sqrt((double)(heading.X * heading.X + heading.Z * heading.Z) * (leaving.X * leaving.X + leaving.Z * leaving.Z));
            Assert.True(cos > 0.97, $"leaves at {Math.Acos(cos) * 180 / Math.PI:F0}°");
        }
        Assert.True(checkedTurns > 0);
    }

    [Fact]
    public void Trails_CanBeRenamed_ButNotPaths_AndNamesAreUnique()
    {
        var sim = Valley();
        Assert.Null(Reject(sim, new RenameTrailCommand(RedRocket, "  Rocket Science ")));
        Assert.Equal("Rocket Science", Way(sim, RedRocket).Name);
        Assert.Contains("already", Reject(sim, new RenameTrailCommand(FlowCountry, "Rocket Science")));
        Assert.Contains("Enter", Reject(sim, new RenameTrailCommand(FlowCountry, " ")));
        int path = sim.State.Ways.First(w => w.Kind == WayKind.AccessPath).Id;
        Assert.Contains("Gravel paths", Reject(sim, new RenameTrailCommand(path, "Gravel")));
    }

    [Fact]
    public void Trails_GetFreeDefaultNames_PathsNone()
    {
        var sim = Valley();
        Assert.Equal("Trail 3", BuildWayCommand.DefaultTrailName(sim.State));
        Assert.Equal(("Red Rocket Part 1", "Red Rocket Part 2"), WayEditing.PartNames(sim.State, Way(sim, RedRocket)));
        Way(sim, RedRocket).Name = "Red Rocket Part 2";
        Assert.Equal(("Red Rocket Part 2", "Red Rocket Part 3"), WayEditing.PartNames(sim.State, Way(sim, RedRocket)));
    }

    [Fact]
    public void Structures_CanBeRenaturalized_LiftsAndParkingForMoney()
    {
        var sim = new Simulation(Bikepark.Sim.Scenarios.ScenarioLoader.CreateWorld(TestWorlds.CareerScenario()));
        sim.Step();
        var state = sim.State;
        var gondola = state.Lifts.Single(l => l.OperatorId is not null);
        var tbar = state.Lifts.Single(l => l.OperatorId is null);
        var lot = state.ParkingLots.Single(p => p.LiftId == tbar.Id);
        var track = state.Ways.Single(w => w.Name == "Ski Hill Track");
        Assert.Contains("belongs to", Reject(sim, new RenaturalizeStructureCommand(gondola.Id)));
        Assert.Contains("serves", Reject(sim, new RenaturalizeStructureCommand(tbar.Id)));

        long money = state.Finance.MoneyCents;
        Assert.Null(Reject(sim, new RenaturalizeStructureCommand(lot.Id)));
        Assert.Equal(lot.Spaces * state.LiftRules.ParkingRemovalCentsPerSpace, money - state.Finance.MoneyCents);
        long tbarCost = StructureRemoval.CostCents(state, tbar.Id);
        Assert.True(tbarCost > 0);
        money = state.Finance.MoneyCents;
        Assert.Null(Reject(sim, new RenaturalizeStructureCommand(tbar.Id)));
        Assert.Equal(tbarCost, money - state.Finance.MoneyCents);
        Assert.DoesNotContain(state.Lifts, l => l.Id == tbar.Id);
        Assert.DoesNotContain(state.TerrainEdits, e => e.OwnerId == tbar.Id || e.OwnerId == lot.Id);
        int[] stations = [tbar.Valley.Id, tbar.Mountain.Id];
        Assert.DoesNotContain(state.Ways, w => stations.Contains(w.StartHubId) || stations.Contains(w.EndHubId));
        Assert.Contains(state.Ways, w => w.Id == track.Id);
        Assert.Equal(state.Finance.TotalRemovalCents, tbarCost + lot.Spaces * state.LiftRules.ParkingRemovalCentsPerSpace);
        sim.RunDays(1);
    }

    private static List<PointCm> Sample(WayGeometry g, long from, long to)
    {
        var points = new List<PointCm>();
        for (int i = 0; i <= 6; i++)
        {
            var p = g.PositionAt(from + (to - from) * i / 6);
            points.Add(new PointCm(p.X + 2_000, p.Z));
        }
        return points;
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
