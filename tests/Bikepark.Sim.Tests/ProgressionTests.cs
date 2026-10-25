using System.Text.Json.Nodes;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Events;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Reputation;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

/// <summary>Park levels unlock content (features first), XP per trail colour and feature colour.</summary>
public class ProgressionTests
{
    // demo_lift_network.json on the Demo terrain: Flow Country = way 11, Red Rocket = way 12 (see FeatureTests).
    private const int FlowCountry = 11;
    private const int RedRocket = 12;

    /// <summary>The Demo with its trails built, but with the level gates on (not a sandbox), after tick 0.</summary>
    private static Simulation GatedWorld()
    {
        var scenario = TestWorlds.LiftScenario();
        scenario.Rules.UnlockAll = false;
        var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
        foreach (var c in TestWorlds.DemoLiftNetwork())
            sim.Commands.Enqueue(c.Command, 0);
        sim.Step();
        Assert.DoesNotContain(sim.Events.Pending, e => e is CommandRejected);
        sim.Events.Clear();
        return sim;
    }

    private static string? Reject(Simulation sim, ICommand command)
    {
        sim.Events.Clear();
        sim.Commands.Enqueue(command);
        sim.Step();
        return sim.Events.Pending.OfType<CommandRejected>().SingleOrDefault()?.Reason;
    }

    [Fact]
    public void ShippedCatalog_UnlocksBlueFirst_ThenRed_ThenBlack()
    {
        var types = TestWorlds.FeatureCatalog();
        Assert.All(types.Where(t => t.Rating <= TrailRating.Blue), t => Assert.Equal(0, t.RequiredLevel));
        int firstRed = types.Where(t => t.Rating == TrailRating.Red).Min(t => t.RequiredLevel);
        int lastRed = types.Where(t => t.Rating == TrailRating.Red).Max(t => t.RequiredLevel);
        int firstBlack = types.Where(t => t.Rating == TrailRating.Black).Min(t => t.RequiredLevel);
        Assert.True(firstRed > 0);
        Assert.True(firstBlack > lastRed);
    }

    [Fact]
    public void LockedFeature_IsRejected_UntilTheParkReachesItsLevel()
    {
        var sim = GatedWorld();
        int level = ParkProgress.CurrentLevel(sim.State, sim.Network);
        sim.State.TrailFeatureTypes.Single(t => t.Id == "double").RequiredLevel = level + 1;
        Assert.Contains($"needs park level {level + 1}", Reject(sim, new PlaceTrailFeatureCommand(RedRocket, "double", 30_000)));
        Assert.Null(Reject(sim, new PlaceTrailFeatureCommand(RedRocket, "double", 30_000, Instant: true))); // debug skips it

        var reached = GatedWorld();
        reached.State.TrailFeatureTypes.Single(t => t.Id == "double").RequiredLevel = level;
        Assert.Null(Reject(reached, new PlaceTrailFeatureCommand(RedRocket, "double", 30_000)));
    }

    [Fact]
    public void Sandbox_HasEveryFeatureAndLift()
    {
        var sim = new Simulation(ScenarioLoader.CreateWorld(TestWorlds.LiftScenario()));
        Assert.True(sim.State.Rules.UnlockAll);
        var black = sim.State.TrailFeatureTypes.Single(t => t.Id == "double");
        Assert.Null(TrailFeatures.CannotPlace(sim.State, 0, black));
        sim.State.Rules.UnlockAll = false;
        Assert.NotNull(TrailFeatures.CannotPlace(sim.State, 0, black));
        Assert.Null(TrailFeatures.CannotPlace(sim.State, black.RequiredLevel, black));
    }

    [Fact]
    public void Xp_CountsBuiltFeaturesByTheirOwnRating()
    {
        var sim = GatedWorld();
        var before = ParkProgress.Xp(sim.State, sim.Network);
        Assert.Equal(0, before.TotalFeatures);

        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(RedRocket, "drop", 10_000, Instant: true));    // red
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(FlowCountry, "table", 22_000, Instant: true)); // blue
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(RedRocket, "double", 30_000, Instant: true));  // black
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(FlowCountry, "rollers", 40_000)); // only planned: no XP yet
        sim.Step();

        var rules = sim.State.ReputationRules;
        var after = ParkProgress.Xp(sim.State, sim.Network);
        Assert.Equal([1, 1, 1], after.Features);
        Assert.Equal(rules.XpPerFeatureByRating.Red + rules.XpPerFeatureByRating.Blue + rules.XpPerFeatureByRating.Black, after.FeatureXp);
        Assert.Equal(after.TrailXp + after.FeatureXp + after.VisitorXp, after.Total);
    }

    [Fact]
    public void Xp_PerKm_IsWorthMoreOnHarderTrails()
    {
        var rules = new ReputationRules();
        Assert.True(rules.XpPerKmByRating.Blue < rules.XpPerKmByRating.Red);
        Assert.True(rules.XpPerKmByRating.Red < rules.XpPerKmByRating.Black);
        Assert.Equal(rules.XpPerKmByRating.Red, rules.XpPerKmByRating.For(TrailRating.Red));
    }

    [Fact]
    public void UnlocksAt_ListsFeaturesLiftsAndLand_OfThatLevel()
    {
        var sim = new Simulation(ScenarioLoader.CreateWorld(TestWorlds.CareerScenario()));
        Assert.Contains("Wooden kicker (red)", ParkProgress.UnlocksAt(sim.State, 3));
        Assert.Contains("land: South slope", ParkProgress.UnlocksAt(sim.State, 3));
        Assert.Contains("Chairlift", ParkProgress.UnlocksAt(sim.State, 5));
        Assert.DoesNotContain(ParkProgress.UnlocksAt(sim.State, 0), u => u.StartsWith("land: Old ski hill"));
    }

    [Fact]
    public void Load_MigratesVersion5_DropsTheOldXpRules()
    {
        string current = SaveGame.Serialize(TestWorlds.Create());
        var root = JsonNode.Parse(current)!.AsObject();
        root["version"] = 5;
        var rep = root["world"]!["reputationRules"]!.AsObject();
        rep.Remove("xpPerKmByRating");
        rep.Remove("xpPerFeatureByRating");
        rep["xpPerTrailKm"] = 150;
        rep["xpPerRating"] = 50;
        rep["xpPerFeatureKind"] = 25;
        foreach (var type in root["world"]!["trailFeatureTypes"]!.AsArray())
            type!.AsObject().Remove("requiredLevel");

        var loaded = SaveGame.Deserialize(root.ToJsonString());

        Assert.Equal(new ReputationRules().XpPerKmByRating.Black, loaded.ReputationRules.XpPerKmByRating.Black);
        Assert.All(loaded.TrailFeatureTypes, t => Assert.Equal(0, t.RequiredLevel));
    }
}
