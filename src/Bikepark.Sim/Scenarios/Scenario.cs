using System.Text.Json;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.State;
using Bikepark.Sim.Terrain;

namespace Bikepark.Sim.Scenarios;

/// <summary>Scenario content as authored in <c>/data/scenarios/*.json</c>.</summary>
public sealed class ScenarioDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public ulong Seed { get; set; }
    public string ParkName { get; set; } = "Unnamed Bikepark";
    public long StartingMoneyCents { get; set; }
    public long EntryFeeCents { get; set; }
    public ParkRules Rules { get; set; } = new();

    /// <summary>Terrain parameters. A null terrain seed follows the scenario seed (and --seed overrides).</summary>
    public TerrainSettings Terrain { get; set; } = new();

    /// <summary>Optional scripted commands (e.g. tutorial events), queued when the scenario starts.</summary>
    public List<TimedCommand> Commands { get; set; } = [];
}

public static class ScenarioLoader
{
    public static ScenarioDefinition Parse(string json) =>
        JsonSerializer.Deserialize<ScenarioDefinition>(json, SimJson.Indented)
        ?? throw new InvalidDataException("Scenario file is empty.");

    public static ScenarioDefinition LoadFile(string path) => Parse(File.ReadAllText(path));

    /// <summary>Builds the initial world for a scenario. <paramref name="seedOverride"/> replaces the scenario's seed.</summary>
    public static WorldState CreateWorld(ScenarioDefinition scenario, ulong? seedOverride = null)
    {
        Validate(scenario);
        ulong seed = seedOverride ?? scenario.Seed;

        var state = new WorldState
        {
            Seed = seed,
            ScenarioId = scenario.Id,
            Rng = SimRandom.FromSeed(seed),
            Park = new ParkState { Name = scenario.ParkName, EntryFeeCents = scenario.EntryFeeCents },
            Rules = scenario.Rules,
            Terrain = scenario.Terrain with { Seed = scenario.Terrain.Seed ?? seed },
            Finance = new FinanceState { MoneyCents = scenario.StartingMoneyCents },
        };

        var queue = new CommandQueue(state);
        foreach (var timed in scenario.Commands)
            queue.Enqueue(timed.Command, timed.Tick);

        return state;
    }

    private static void Validate(ScenarioDefinition s)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(s.Id)) errors.Add("id is required");
        var r = s.Rules;
        if (r.OpenMinute < 0 || r.OpenMinute >= GameTime.MinutesPerDay) errors.Add("rules.openMinute out of range");
        if (r.CloseMinute <= r.OpenMinute || r.CloseMinute > GameTime.MinutesPerDay) errors.Add("rules.closeMinute must be after openMinute and within the day");
        if (r.BaseArrivalPermille < 0) errors.Add("rules.baseArrivalPermille must be >= 0");
        if (r.ReferenceEntryFeeCents <= 0) errors.Add("rules.referenceEntryFeeCents must be > 0");
        if (r.Capacity <= 0) errors.Add("rules.capacity must be > 0");
        if (s.EntryFeeCents < 0 || s.EntryFeeCents > r.MaxEntryFeeCents) errors.Add("entryFeeCents out of range");

        errors.AddRange(s.Terrain.Validate());

        if (errors.Count > 0)
            throw new InvalidDataException($"Invalid scenario '{s.Id}': {string.Join("; ", errors)}");
    }
}
