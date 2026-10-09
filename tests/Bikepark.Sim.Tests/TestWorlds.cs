using System.Text.Json;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

internal static class TestWorlds
{
    public static ScenarioDefinition Scenario(ulong seed = 1337) => new()
    {
        Id = "test",
        Name = "Test",
        Seed = seed,
        ParkName = "Test Park",
        StartingMoneyCents = 1_000_000,
        EntryFeeCents = 1500,
        Rules = new ParkRules(),
        // Same terrain for every world seed (Starter Valley's), so the demo network is valid everywhere.
        Terrain = new TerrainSettings { Seed = 1337 },
        TrailFeatureTypes = FeatureCatalog(),
        ToolTypes = ToolCatalog(),
    };

    /// <summary>The shipped tool catalog (data/tools.json).</summary>
    public static List<ToolType> ToolCatalog() =>
        ScenarioLoader.LoadToolCatalog(Path.Combine(RepoRoot(), "data", "tools.json"));

    /// <summary>The shipped trail feature catalog (data/trail_features.json).</summary>
    public static List<TrailFeatureType> FeatureCatalog() =>
        ScenarioLoader.LoadFeatureCatalog(Path.Combine(RepoRoot(), "data", "trail_features.json"));

    /// <summary>The demo network (gravel path + Blue and Red trail) from data/scripts, built at tick 0.</summary>
    public static IReadOnlyList<TimedCommand> DemoNetwork() =>
        JsonSerializer.Deserialize<List<TimedCommand>>(File.ReadAllText(Path.Combine(RepoRoot(), "data", "scripts", "demo_network.json")), SimJson.Indented)!;

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bikepark.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    public static WorldState Create(ulong seed = 1337) => ScenarioLoader.CreateWorld(Scenario(seed));

    /// <summary>
    /// Starter Valley as shipped: gondola run by Alpine Lift Co. (tier 1, every 4th cabin), valley parking and the old
    /// hiking route, built by the scenario's tick-0 commands. The terrain is pinned to seed 1337 for every world seed.
    /// </summary>
    public static ScenarioDefinition LiftScenario(ulong seed = 1337)
    {
        var scenario = ScenarioLoader.LoadFile(Path.Combine(RepoRoot(), "data", "scenarios", "demo_valley.json"));
        scenario.Seed = seed;
        scenario.Terrain = scenario.Terrain with { Seed = 1337 };
        return scenario;
    }

    /// <summary>
    /// The Starter Valley career (data/scenarios/starter_valley.json): the old ski hill with the derelict T-bar (lift 1),
    /// its parking (6) and track (way 8); the company gondola (lift 9, tier 0) with its parking and the hiking route (16).
    /// The terrain is pinned to seed 1337.
    /// </summary>
    public static ScenarioDefinition CareerScenario(ulong seed = 1337)
    {
        var scenario = ScenarioLoader.LoadFile(Path.Combine(RepoRoot(), "data", "scenarios", "starter_valley.json"));
        scenario.Seed = seed;
        scenario.Terrain = scenario.Terrain with { Seed = 1337 };
        return scenario;
    }

    /// <summary>data/scripts/career_opening.json: restore the T-bar, hire two, plan two trails, buy the foot forest.</summary>
    public static IReadOnlyList<TimedCommand> CareerOpening() =>
        JsonSerializer.Deserialize<List<TimedCommand>>(File.ReadAllText(Path.Combine(RepoRoot(), "data", "scripts", "career_opening.json")), SimJson.Indented)!;

    /// <summary>The demo trails from the plateau (data/scripts/demo_lift_network.json), built at tick 0.</summary>
    public static IReadOnlyList<TimedCommand> DemoLiftNetwork() =>
        JsonSerializer.Deserialize<List<TimedCommand>>(File.ReadAllText(Path.Combine(RepoRoot(), "data", "scripts", "demo_lift_network.json")), SimJson.Indented)!;

    /// <summary>A command script for the lift world: the demo trails plus bike access bookings.</summary>
    public static IReadOnlyList<TimedCommand> LiftScript() =>
    [
        .. DemoLiftNetwork(),
        new(600, new SetLiftBikeAccessCommand(1, 2)), // starts at the next opening (day 1)
        new(1500, new SetEntryFeeCommand(2000)),
        new(1600, new SetLiftBikeAccessCommand(1, 9)), // rejected: no such tier
        new(2500, new SetLiftBikeAccessCommand(1, 0)), // no bikes from day 2: riders pedal up
    ];

    public static Simulation RunLift(ulong seed, long ticks, IEnumerable<TimedCommand>? commands = null)
    {
        var sim = new Simulation(ScenarioLoader.CreateWorld(LiftScenario(seed)));
        foreach (var c in commands ?? [])
            sim.Commands.Enqueue(c.Command, c.Tick);
        sim.RunTicks(ticks);
        return sim;
    }

    /// <summary>A fixed command script used by determinism tests.</summary>
    public static IReadOnlyList<TimedCommand> Script() =>
    [
        .. DemoNetwork(),
        // Demo network ids: path 1, Blue Line 2, Red Rocket 3 (built at once). Planned features and their jobs get ids
        // 4.. in pairs (feature, job): berm 4/5, double 6/7, drop 8/9, table 10/11.
        new(10, new PlaceTrailFeatureCommand(2, "berm", 18_000)),
        new(10, new PlaceTrailFeatureCommand(2, "double", 4_000)),
        new(10, new PlaceTrailFeatureCommand(1, "berm", 5_000)), // rejected: a gravel path
        new(10, new PlaceTrailFeatureCommand(3, "drop", 15_000)),
        new(11, new PlaceTrailFeatureCommand(3, "table", 1_000)),
        new(11, new PlaceTrailFeatureCommand(3, "kicker", 1_200)), // rejected: overlaps the table
        // Crew 12, 13; felling job 14 (forest east of the plateau).
        new(20, new HireCrewCommand()),
        new(20, new HireCrewCommand("Alex")),
        new(20, new BuyToolCommand("chainsaw")),
        new(20, new BuyToolCommand("chainsaw")), // rejected: already owned
        new(20, new FellTreesCommand(new PointCm(72_000, 52_000), 2_000)),
        new(30, new PrioritizeJobCommand(9)), // the drop: waits for wood, so the crew skips it
        new(30, new PrioritizeJobCommand(14)), // the felling first (it yields the drop's wood)
        new(700, new CancelJobCommand(14)), // trees cut so far stay cut
        new(900, new RemoveTrailFeatureCommand(2, 6)), // the double, while riders are out
        new(1100, new BuyWoodCommand(10)),
        new(2000, new DismissCrewCommand(13)),
        new(600, new SetEntryFeeCommand(2500)),
        new(1500, new RenameParkCommand("Gravity Hill")),
        new(2000, new SetEntryFeeCommand(99_999)), // rejected: above max
        new(3000, new SetEntryFeeCommand(800)),
        new(3000, new SetEntryFeeCommand(1200)), // same tick: applied after the previous one
        // Trail care (no wear in the test world): Red Rocket closed for a while.
        new(650, new SetTrailClosedCommand(3, true)),
        new(800, new SetTrailClosedCommand(3, false)),
        new(660, new RepairFeatureCommand(2, 4, 2)), // rejected: not built / in perfect condition
        new(660, new SetTrailClosedCommand(1, true)), // rejected: a path
        // Land and lifts (no parcels or lifts in the test world).
        new(700, new BuyParcelCommand("nowhere")), // rejected: unknown parcel
        new(700, new RestoreLiftCommand(99)), // rejected: no such lift
        // Trail editing: Red Rocket has planned features (crew jobs), so editing it is rejected; the path can't be split.
        new(720, new SplitTrailCommand(3, 20_000)), // rejected: the crew is working on it
        new(720, new RenaturalizeTrailCommand(1, 1_000, 5_000)), // rejected: a gravel path
        new(720, new BuildPlatformCommand("", new PointCm(-100, 0), new PointCm(0, 0))), // rejected: off the map
    ];

    public static Simulation Run(ulong seed, long days, IEnumerable<TimedCommand>? commands = null)
    {
        var sim = new Simulation(Create(seed));
        foreach (var c in commands ?? [])
            sim.Commands.Enqueue(c.Command, c.Tick);
        sim.RunDays(days);
        return sim;
    }
}
