using System.Text.Json;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Lifts;
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

    public Trails.TrailRules TrailRules { get; set; } = new();

    public Trails.WearRules WearRules { get; set; } = new();

    public Weather.WeatherRules WeatherRules { get; set; } = new();

    /// <summary>Terrain parameters. A null terrain seed follows the scenario seed (and --seed overrides).</summary>
    public TerrainSettings Terrain { get; set; } = new();

    /// <summary>Lift catalog file, relative to the scenario file (e.g. <c>../lift_types.json</c>); loaded into <see cref="LiftTypes"/>.</summary>
    public string? LiftTypesFile { get; set; }

    /// <summary>Lift models available in this scenario (inline, plus those from <see cref="LiftTypesFile"/>).</summary>
    public List<LiftType> LiftTypes { get; set; } = [];

    /// <summary>Lift companies operating in the area and the bike access they rent out.</summary>
    public List<LiftOperator> Operators { get; set; } = [];

    public LiftRules LiftRules { get; set; } = new();

    /// <summary>Trail feature catalog file, relative to the scenario file; loaded into <see cref="TrailFeatureTypes"/>.</summary>
    public string? TrailFeaturesFile { get; set; }

    /// <summary>Trail features available in this scenario (inline, plus those from <see cref="TrailFeaturesFile"/>).</summary>
    public List<Trails.TrailFeatureType> TrailFeatureTypes { get; set; } = [];

    public CrewRules CrewRules { get; set; } = new();

    /// <summary>Tool catalog file, relative to the scenario file; loaded into <see cref="ToolTypes"/>.</summary>
    public string? ToolsFile { get; set; }

    /// <summary>Tools for sale in this scenario (inline, plus those from <see cref="ToolsFile"/>).</summary>
    public List<ToolType> ToolTypes { get; set; } = [];

    public int StartingWood { get; set; }

    public Reputation.ReputationRules ReputationRules { get; set; } = new();

    public Safety.CrashRules CrashRules { get; set; } = new();

    /// <summary>Optional scripted commands (e.g. tutorial events), queued when the scenario starts.</summary>
    public List<TimedCommand> Commands { get; set; } = [];
}

public static class ScenarioLoader
{
    public static ScenarioDefinition Parse(string json) =>
        JsonSerializer.Deserialize<ScenarioDefinition>(json, SimJson.Indented)
        ?? throw new InvalidDataException("Scenario file is empty.");

    /// <summary>Loads a scenario and the lift and trail feature catalogs it references.</summary>
    public static ScenarioDefinition LoadFile(string path)
    {
        var scenario = Parse(File.ReadAllText(path));
        string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        if (scenario.LiftTypesFile is { Length: > 0 } catalog)
        {
            scenario.LiftTypes = [.. scenario.LiftTypes, .. LoadCatalog<LiftType>(Path.Combine(directory, catalog), "Lift")];
            scenario.LiftTypesFile = null;
        }
        if (scenario.TrailFeaturesFile is { Length: > 0 } features)
        {
            scenario.TrailFeatureTypes = [.. scenario.TrailFeatureTypes, .. LoadFeatureCatalog(Path.Combine(directory, features))];
            scenario.TrailFeaturesFile = null;
        }
        if (scenario.ToolsFile is { Length: > 0 } tools)
        {
            scenario.ToolTypes = [.. scenario.ToolTypes, .. LoadToolCatalog(Path.Combine(directory, tools))];
            scenario.ToolsFile = null;
        }
        return scenario;
    }

    /// <summary>Loads a trail feature catalog (<c>data/trail_features.json</c>).</summary>
    public static List<Trails.TrailFeatureType> LoadFeatureCatalog(string path) => LoadCatalog<Trails.TrailFeatureType>(path, "Trail feature");

    /// <summary>Loads a tool catalog (<c>data/tools.json</c>).</summary>
    public static List<ToolType> LoadToolCatalog(string path) => LoadCatalog<ToolType>(path, "Tool");

    private static List<T> LoadCatalog<T>(string path, string what) =>
        JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), SimJson.Indented)
        ?? throw new InvalidDataException($"{what} catalog '{path}' is empty.");

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
            TrailRules = scenario.TrailRules,
            WearRules = scenario.WearRules,
            WeatherRules = scenario.WeatherRules,
            LiftTypes = scenario.LiftTypes,
            Operators = scenario.Operators,
            LiftRules = scenario.LiftRules,
            TrailFeatureTypes = scenario.TrailFeatureTypes,
            CrewRules = scenario.CrewRules,
            ToolTypes = scenario.ToolTypes,
            WoodStock = scenario.StartingWood,
            ReputationRules = scenario.ReputationRules,
            CrashRules = scenario.CrashRules,
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
        errors.AddRange(r.Validate());
        if (s.EntryFeeCents < 0 || s.EntryFeeCents > r.MaxEntryFeeCents) errors.Add("entryFeeCents out of range");

        errors.AddRange(s.Terrain.Validate());
        errors.AddRange(s.TrailRules.Validate());
        errors.AddRange(s.WearRules.Validate());
        errors.AddRange(s.WeatherRules.Validate());
        errors.AddRange(s.LiftRules.Validate());
        if (s.LiftTypesFile is { Length: > 0 }) errors.Add("liftTypesFile can only be resolved when loading from a file");
        foreach (var type in s.LiftTypes) errors.AddRange(type.Validate());
        if (s.LiftTypes.Select(t => t.Id).Distinct().Count() != s.LiftTypes.Count) errors.Add("liftTypes: ids must be unique");
        foreach (var op in s.Operators) errors.AddRange(op.Validate());
        if (s.Operators.Select(o => o.Id).Distinct().Count() != s.Operators.Count) errors.Add("operators: ids must be unique");

        if (s.TrailFeaturesFile is { Length: > 0 }) errors.Add("trailFeaturesFile can only be resolved when loading from a file");
        foreach (var type in s.TrailFeatureTypes) errors.AddRange(type.Validate());
        if (s.TrailFeatureTypes.Select(t => t.Id).Distinct().Count() != s.TrailFeatureTypes.Count) errors.Add("trailFeatureTypes: ids must be unique");

        errors.AddRange(s.CrewRules.Validate());
        if (s.ToolsFile is { Length: > 0 }) errors.Add("toolsFile can only be resolved when loading from a file");
        foreach (var tool in s.ToolTypes) errors.AddRange(tool.Validate());
        if (s.ToolTypes.Select(t => t.Id).Distinct().Count() != s.ToolTypes.Count) errors.Add("toolTypes: ids must be unique");
        if (s.StartingWood < 0) errors.Add("startingWood must be >= 0");
        errors.AddRange(s.ReputationRules.Validate());
        errors.AddRange(s.CrashRules.Validate());

        if (errors.Count > 0)
            throw new InvalidDataException($"Invalid scenario '{s.Id}': {string.Join("; ", errors)}");
    }
}
