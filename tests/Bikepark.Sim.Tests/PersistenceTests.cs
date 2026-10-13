using System.Text.Json;
using System.Text.Json.Nodes;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;

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
    public void Load_MigratesVersion3_DropsThePreAndAfterHoursRules()
    {
        string current = SaveGame.Serialize(TestWorlds.Create());
        string v3 = current.Replace($"\"version\": {SaveGame.CurrentVersion}", "\"version\": 3")
            .Replace("\"rules\": {", "\"rules\": {\n      \"lastRideMinutes\": 45,\n      \"liftWarmupMinutes\": 30,")
            .Replace("\"crewRules\": {", "\"crewRules\": {\n      \"workStartMinute\": 450,\n      \"workEndMinute\": 1050,\n      \"overtimeMinutes\": 90,");
        Assert.Contains("lastRideMinutes", v3);
        Assert.Contains("overtimeMinutes", v3);

        var loaded = SaveGame.Deserialize(v3);

        Assert.Equal(StateHash.Compute(SaveGame.Deserialize(current)), StateHash.Compute(loaded));
    }

    [Fact]
    public void Load_MigratesVersion7_DropsTrailUpkeepAndInsurance()
    {
        var sim = new Simulation(TestWorlds.Create());
        sim.RunDays(1);
        string current = SaveGame.Serialize(sim.State);
        var root = JsonNode.Parse(current)!.AsObject();
        root["version"] = 7;
        var world = root["world"]!.AsObject();
        world["trailRules"]!.AsObject()["upkeepCentsPerKm"] = 5000;
        foreach (var type in world["trailFeatureTypes"]!.AsArray())
            type!.AsObject()["upkeepCentsPerDay"] = 500;
        world["finance"]!.AsObject()["totalTrailUpkeepCents"] = 1234;
        world["finance"]!.AsObject()["trailUpkeepTodayCents"] = 0;
        var crash = world["crashRules"]!.AsObject();
        int rateDays = crash["rateDays"]!.GetValue<int>();
        crash.Remove("rateDays");
        crash["insuranceDays"] = rateDays;
        crash["insuranceBaseCents"] = 5000;
        crash["insurancePerMinorCents"] = 1500;
        crash["insurancePerSeriousCents"] = 15000;
        crash["insurancePerFeatureByRating"] = new JsonObject { ["green"] = 0, ["blue"] = 100, ["red"] = 400, ["black"] = 1200 };
        world["safety"]!.AsObject()["totalInsuranceCents"] = 5000;
        world["safety"]!.AsObject()["insuranceTodayCents"] = 0;
        var days = world["dayHistory"]!.AsArray();
        Assert.NotEmpty(days);
        foreach (var day in days)
        {
            day!.AsObject()["trailUpkeepCents"] = 1234;
            day.AsObject()["insuranceCents"] = 5000;
        }
        string v7 = root.ToJsonString();

        var loaded = SaveGame.Deserialize(v7);

        Assert.Equal(StateHash.Compute(SaveGame.Deserialize(current)), StateHash.Compute(loaded));
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

    [Fact]
    public void SaveAtomic_WritesTheWholeSave_AndLeavesNoTempFile()
    {
        var sim = TestWorlds.Run(1337, 1, TestWorlds.DemoNetwork());
        string dir = Path.Combine(Path.GetTempPath(), $"bikepark-save-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "career.json");
            File.WriteAllText(path, "old");
            SaveGame.SaveAtomic(sim.State, path);
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(SaveGame.Load(path)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DayHistory_KeepsTheLastDays_AndIsSaved()
    {
        var sim = TestWorlds.Run(1337, 3, TestWorlds.DemoNetwork());
        Assert.Equal([0L, 1L, 2L], sim.State.DayHistory.Select(d => d.Day));
        Assert.True(sim.State.DayHistory.All(d => d.Visitors > 0));
        Assert.Equal(sim.State.DayHistory, SaveGame.Clone(sim.State).DayHistory);

        var state = TestWorlds.Create();
        var quick = new Simulation(state);
        quick.RunDays(WorldState.DayHistoryDays + 2);
        Assert.Equal(WorldState.DayHistoryDays, state.DayHistory.Count);
        Assert.Equal(2L, state.DayHistory[0].Day);
    }
}
