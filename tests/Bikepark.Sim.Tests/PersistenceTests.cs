using System.Text.Json;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;

namespace Bikepark.Sim.Tests;

public class PersistenceTests
{
    [Fact]
    public void RoundTrip_PreservesFullState_IncludingRngAndPendingCommands()
    {
        var sim = new Simulation(TestWorlds.Create());
        sim.RunTicks(800); // mid-day, guests in the park
        sim.Commands.Enqueue(new SetEntryFeeCommand(2000), atTick: 5000);
        sim.Commands.Enqueue(new RenameParkCommand("Later"), atTick: 6000);

        Assert.NotEmpty(sim.State.Guests);

        string json = SaveGame.Serialize(sim.State);
        var loaded = SaveGame.Deserialize(json);

        Assert.Equal(json, SaveGame.Serialize(loaded));
        Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(loaded));
        Assert.Equal(sim.State.Rng.State, loaded.Rng.State);
        Assert.IsType<RenameParkCommand>(loaded.PendingCommands[1].Command);
    }

    [Fact]
    public void Save_UsesStableCommandDiscriminators()
    {
        var state = TestWorlds.Create();
        new Simulation(state).Commands.Enqueue(new SetEntryFeeCommand(10), atTick: 1);

        string json = SaveGame.Serialize(state);

        Assert.Contains("\"type\": \"setEntryFee\"", json);
        Assert.Contains($"\"version\": {SaveGame.CurrentVersion}", json);
    }

    [Fact]
    public void Load_RejectsUnknownVersion()
    {
        string json = SaveGame.Serialize(TestWorlds.Create())
            .Replace($"\"version\": {SaveGame.CurrentVersion}", "\"version\": 999");
        Assert.Throws<InvalidDataException>(() => SaveGame.Deserialize(json));
    }

    [Fact]
    public void Load_MigratesVersion1_TrailGradientRules()
    {
        // A v1 save still carries the old permille gradient limits; they are dropped and the new defaults apply.
        string v2 = SaveGame.Serialize(TestWorlds.Create());
        var defaults = new Trails.TrailRules();
        string v1 = v2.Replace($"\"version\": {SaveGame.CurrentVersion}", "\"version\": 1")
            .Replace($"\"pathMaxGradient\": {defaults.PathMaxGradient},",
                     "\"pathMaxGradePermille\": 150,\n      \"trailMaxDownGradePermille\": 700,\n      \"trailMaxUpGradePermille\": 80,");
        Assert.Contains("pathMaxGradePermille", v1);

        var loaded = SaveGame.Deserialize(v1);

        Assert.Equal(defaults.PathMaxGradient, loaded.TrailRules.PathMaxGradient);
        Assert.Equal(StateHash.Compute(SaveGame.Deserialize(v2)), StateHash.Compute(loaded));
    }

    [Fact]
    public void ShippedScenarios_LoadAndValidate()
    {
        string dir = Path.Combine(RepoRoot(), "data", "scenarios");
        var files = Directory.GetFiles(dir, "*.json");
        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            var scenario = ScenarioLoader.LoadFile(file);
            var state = ScenarioLoader.CreateWorld(scenario);
            Assert.Equal(scenario.Id, state.ScenarioId);
        }
    }

    [Fact]
    public void Scenario_WithUnknownField_IsRejected()
    {
        const string json = """{ "id": "x", "entryFeeCents": 100, "typoField": 1 }""";
        Assert.Throws<JsonException>(() => ScenarioLoader.Parse(json));
    }

    [Fact]
    public void Scenario_ScriptedCommands_AreQueued()
    {
        var scenario = TestWorlds.Scenario();
        scenario.Commands.Add(new TimedCommand(10, new SetEntryFeeCommand(900)));

        var state = ScenarioLoader.CreateWorld(scenario);

        var pending = Assert.Single(state.PendingCommands);
        Assert.Equal(10, pending.Tick);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bikepark.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
