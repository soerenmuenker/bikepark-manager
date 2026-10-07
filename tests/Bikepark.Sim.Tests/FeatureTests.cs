using System.Text.Json;
using System.Text.Json.Nodes;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Events;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

public class FeatureTests
{
    // Starter Valley (two workers hired at tick 0) + demo_lift_network.json: Flow Country = way 11, Red Rocket = way 12,
    // Old Hiking Route = way 8. Features here are placed instantly (debug); building them by the crew is in CrewTests.
    private const int FlowCountry = 11;
    private const int RedRocket = 12;
    private const int HikingRoute = 8;

    // ---------------------------------------------------------------- catalog

    [Fact]
    public void ShippedCatalog_LoadsIntoStarterValley_AndValidates()
    {
        var state = ScenarioLoader.CreateWorld(TestWorlds.LiftScenario());
        var ids = state.TrailFeatureTypes.Select(t => t.Id).ToList();
        Assert.Equal(["berm", "rollers", "table", "double", "wall_ride", "kicker", "drop"], ids);
        Assert.All(state.TrailFeatureTypes, t => Assert.Empty(t.Validate()));
        Assert.Equal(3, state.TrailFeatureTypes.Count(t => t.Material == FeatureMaterial.Wood));
    }

    [Fact]
    public void Catalog_RejectsUnknownFields_DuplicateIds_AndBadWindows()
    {
        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<List<TrailFeatureType>>("""[{ "id": "berm", "name": "Berm", "speed": 3 }]""", SimJson.Indented));

        var duplicate = TestWorlds.Scenario();
        duplicate.TrailFeatureTypes.Add(TestWorlds.FeatureCatalog()[0]);
        Assert.Throws<InvalidDataException>(() => ScenarioLoader.CreateWorld(duplicate));

        var bad = TestWorlds.Scenario();
        bad.TrailFeatureTypes[0].MinGradient = 10;
        bad.TrailFeatureTypes[0].MaxGradient = -10;
        Assert.Contains("gradient window", Assert.Throws<InvalidDataException>(() => ScenarioLoader.CreateWorld(bad)).Message);
    }

    // ---------------------------------------------------------------- planning

    [Fact]
    public void Planner_AcceptsGoodSpots_AndNamesEachProblem()
    {
        var sim = DemoWorld();
        string Plan(int way, string type, long meters) =>
            FeaturePlanner.Plan(sim.Network, sim.State.TrailRules, sim.State.TrailFeatureTypes, way, type, meters * 100)
                .Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Error)?.Code ?? "ok";

        Assert.Equal("ok", Plan(FlowCountry, "berm", 40));
        Assert.Equal("ok", Plan(RedRocket, "drop", 100));
        Assert.Equal("unknownType", Plan(FlowCountry, "loop", 40));
        Assert.Equal("noSuchTrail", Plan(999, "berm", 40));
        Assert.Equal("notATrail", Plan(HikingRoute, "table", 100));
        Assert.Equal("tooCloseToStart", Plan(FlowCountry, "rollers", 5));
        Assert.Equal("tooCloseToEnd", Plan(FlowCountry, "rollers", 1280));
        Assert.Equal("needsBend", Plan(FlowCountry, "berm", 600)); // straight stretch
        Assert.Equal("tooFlat", Plan(FlowCountry, "drop", 600)); // only -1.0 there
        Assert.Equal("tooSteep", Plan(FlowCountry, "table", 140)); // -5.0 segment

        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(FlowCountry, "table", 22_000, Instant: true));
        sim.Step();
        Assert.Equal("overlaps", Plan(FlowCountry, "kicker", 228)); // within 5 m of the table's end (232 m)
        Assert.Equal("ok", Plan(FlowCountry, "kicker", 237));
    }

    [Fact]
    public void PlaceCommand_UsesThePlanner()
    {
        var sim = DemoWorld();
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(FlowCountry, "berm", 60_000, Instant: true));
        sim.Step();
        var rejected = Assert.Single(sim.Events.Pending.OfType<CommandRejected>());
        Assert.Equal("A berm needs a bend in the trail.", rejected.Reason);
    }

    // ---------------------------------------------------------------- commands

    [Fact]
    public void Place_AddsSortedFeatures_WithFreshIds_AndRemoveDeletesThem()
    {
        var sim = DemoWorld();
        int revision = sim.State.WaysRevision;
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(RedRocket, "double", 30_000, Instant: true));
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(RedRocket, "drop", 10_000, Instant: true));
        sim.Step();

        var way = sim.State.Ways.Single(w => w.Id == RedRocket);
        Assert.Equal(["drop", "double"], way.Features.Select(f => f.TypeId));
        Assert.Equal(revision + 2, sim.State.WaysRevision);
        Assert.Equal(2, sim.Events.Pending.OfType<TrailFeaturePlaced>().Count());
        Assert.Equal(2, sim.Network.FeaturesOn(RedRocket).Count);
        Assert.Empty(sim.Network.FeaturesOn(HikingRoute));

        int doubleId = way.Features[1].Id;
        sim.Commands.Enqueue(new RemoveTrailFeatureCommand(RedRocket, doubleId));
        sim.Commands.Enqueue(new RemoveTrailFeatureCommand(RedRocket, doubleId)); // already gone
        sim.Step();
        Assert.Equal(["drop"], way.Features.Select(f => f.TypeId));
        Assert.Single(sim.Events.Pending.OfType<TrailFeatureRemoved>());
        Assert.Equal("No such feature.", sim.Events.Pending.OfType<CommandRejected>().Single().Reason);
    }

    [Fact]
    public void DeletingATrail_TakesItsFeatures()
    {
        var sim = DemoWorld();
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(RedRocket, "drop", 10_000, Instant: true));
        sim.Commands.Enqueue(new DeleteWayCommand(RedRocket));
        sim.Step();
        Assert.DoesNotContain(sim.State.Ways, w => w.Id == RedRocket);
        Assert.Empty(sim.Network.FeaturesOn(RedRocket));
    }

    [Fact]
    public void ChangingFeatures_KeepsRidersOnTheirRoutes()
    {
        var sim = DemoWorld();
        sim.RunTicks(10 * 60); // 10:00, riders out on the trails
        var descending = sim.State.Guests.Where(g => g.Activity == RiderActivity.Descending).ToList();
        Assert.NotEmpty(descending);
        var before = descending.Select(g => (g.Id, g.TrailId, g.RouteProgressCm, g.Route.Count)).ToList();

        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(RedRocket, "kicker", 17_000, Instant: true));
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(FlowCountry, "rollers", 50_000, Instant: true));
        sim.Step();
        Assert.DoesNotContain(sim.Events.Pending, e => e is CommandRejected);

        // Riders still on their run were not sent back to the base: same route, further along it.
        int stillRiding = 0;
        foreach (var (id, trail, progress, legs) in before)
        {
            var guest = sim.State.Guests.Single(g => g.Id == id);
            if (guest.Activity != RiderActivity.Descending || guest.TrailId != trail) continue;
            stillRiding++;
            Assert.Equal(legs, guest.Route.Count);
            Assert.True(guest.RouteProgressCm > progress);
        }
        Assert.True(stillRiding > 0);
    }

    // ---------------------------------------------------------------- effect

    [Fact]
    public void Features_RaiseSegmentDifficulty_AndSetARatingFloor()
    {
        var sim = DemoWorld();
        var before = sim.Network.Geometry(RedRocket);
        Assert.Equal(TrailRating.Red, before.Rating);

        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(RedRocket, "drop", 10_000, Instant: true));
        sim.Step();
        var after = sim.Network.Geometry(RedRocket);

        Assert.Equal(TrailRating.Black, after.Rating);
        Assert.Equal(700, after.DifficultyScore);
        var covered = after.Segments.Where(s => s.StartCm < 10_500 && s.EndCm > 10_000).ToList();
        Assert.NotEmpty(covered);
        Assert.All(covered, s => Assert.Equal(700, s.FeatureDifficulty));
        Assert.All(after.Segments.Except(covered), s => Assert.Equal(0, s.FeatureDifficulty));
        // The shape does not change.
        Assert.Equal(before.LengthCm, after.LengthCm);
    }

    [Fact]
    public void FeatureFun_FollowsStyle()
    {
        var types = TestWorlds.FeatureCatalog();
        var berm = types.Single(t => t.Id == "berm");
        var drop = types.Single(t => t.Id == "drop");
        var flow = new Guest { Skill = 800, Style = RiderStyle.Flow };
        var tech = new Guest { Skill = 800, Style = RiderStyle.Technical };

        Assert.True(RiderSystem.FeatureFun(flow, berm) > RiderSystem.FeatureFun(tech, berm));
        Assert.True(RiderSystem.FeatureFun(tech, drop) > RiderSystem.FeatureFun(flow, drop));

        var novice = new Guest { Skill = 300, Style = RiderStyle.Technical };
        Assert.True(RiderSystem.FeatureFun(novice, drop) < RiderSystem.FeatureFun(tech, drop) / 2); // scared
    }

    [Fact]
    public void Riders_ScoreFeatures_AndAvoidTheHardenedTrail()
    {
        var without = TestWorlds.RunLift(1337, 1440, TestWorlds.DemoLiftNetwork());
        var with = TestWorlds.RunLift(1337, 1440, [.. TestWorlds.DemoLiftNetwork(), .. DemoFeatures()]);

        Assert.Equal(19, with.State.Ways.Sum(w => w.Features.Count));
        long Runs(Simulation s, int id) => s.State.Ways.Single(w => w.Id == id).Stats.Runs;
        long Fun(Simulation s, int id) => s.State.Ways.Single(w => w.Id == id).Stats.SumFun / Math.Max(1, Runs(s, id));
        Assert.True(Runs(with, RedRocket) < Runs(without, RedRocket), "black trail draws fewer riders");
        Assert.True(Fun(with, FlowCountry) > Fun(without, FlowCountry), "berms and rollers make Flow Country more fun");
    }

    // ---------------------------------------------------------------- persistence

    [Fact]
    public void Features_SurviveSaveAndLoad()
    {
        var sim = DemoWorld();
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(RedRocket, "drop", 10_000, Instant: true));
        sim.Step();
        var loaded = new Simulation(SaveGame.Deserialize(SaveGame.Serialize(sim.State)));
        Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(loaded.State));
        Assert.Equal(TrailRating.Black, loaded.Network.Geometry(RedRocket).Rating);
    }

    [Fact]
    public void Version2SaveWithoutFeatures_StillLoads()
    {
        var sim = DemoWorld();
        var root = JsonNode.Parse(SaveGame.Serialize(sim.State))!.AsObject();
        var world = root["world"]!.AsObject();
        world.Remove("trailFeatureTypes");
        foreach (var way in world["ways"]!.AsArray())
            way!.AsObject().Remove("features");
        foreach (string old in new[] { "featureStartMarginMeters", "featureEndMarginMeters", "featureGapMeters", "maxFeaturesPerTrail" })
            world["trailRules"]!.AsObject().Remove(old);

        var loaded = new Simulation(SaveGame.Deserialize(root.ToJsonString()));
        Assert.Empty(loaded.State.TrailFeatureTypes);
        Assert.All(loaded.State.Ways, w => Assert.Empty(w.Features));
        loaded.RunTicks(60);
    }

    [Fact]
    public void Save_UsesStableFeatureDiscriminators()
    {
        string json = JsonSerializer.Serialize<ICommand>(new PlaceTrailFeatureCommand(1, "berm", 100), SimJson.Compact);
        Assert.Contains("\"type\":\"placeTrailFeature\"", json);
        json = JsonSerializer.Serialize<ICommand>(new RemoveTrailFeatureCommand(1, 2), SimJson.Compact);
        Assert.Contains("\"type\":\"removeTrailFeature\"", json);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The demo features, placed instantly.</summary>
    internal static IReadOnlyList<TimedCommand> DemoFeatures(bool instant = true) =>
        JsonSerializer.Deserialize<List<TimedCommand>>(
                File.ReadAllText(Path.Combine(TestWorlds.RepoRoot(), "data", "scripts", "demo_features.json")), SimJson.Indented)!
            .Select(c => instant && c.Command is PlaceTrailFeatureCommand place ? c with { Command = place with { Instant = true } } : c)
            .ToList();

    /// <summary>Starter Valley with the demo trails built (after tick 0).</summary>
    private static Simulation DemoWorld()
    {
        var sim = new Simulation(ScenarioLoader.CreateWorld(TestWorlds.LiftScenario()));
        foreach (var c in TestWorlds.DemoLiftNetwork())
            sim.Commands.Enqueue(c.Command, 0);
        sim.Step();
        Assert.DoesNotContain(sim.Events.Pending, e => e is CommandRejected);
        sim.Events.Clear();
        return sim;
    }
}
