using System.Text.Json;
using Bikepark.Sim;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Events;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Reporting;
using Bikepark.Sim.Scenarios;
using Bikepark.SimRunner.Terrain;

namespace Bikepark.SimRunner;

/// <summary>
/// Headless runner.
/// <list type="bullet">
///   <item>default: load a scenario, run N days, print KPIs as JSON (see <see cref="RunnerOptions.Usage"/>);</item>
///   <item><c>terrain</c>: generate the scenario's terrain, write top-down PNG maps, print terrain stats.</item>
/// </list>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        bool terrain = args.Length > 0 && args[0] == "terrain";
        try
        {
            if (terrain)
                TerrainCommand.Run(args[1..]);
            else
                Run(RunnerOptions.Parse(args));
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine(terrain ? TerrainCommand.Usage : RunnerOptions.Usage);
            return 2;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static void Run(RunnerOptions options)
    {
        var scenario = ScenarioLoader.LoadFile(options.ScenarioPath);
        var state = ScenarioLoader.CreateWorld(scenario, options.Seed);
        var sim = new Simulation(state);

        if (options.CommandsPath is { } commandsPath)
        {
            var script = JsonSerializer.Deserialize<List<TimedCommand>>(File.ReadAllText(commandsPath), SimJson.Indented) ?? [];
            foreach (var timed in script)
                sim.Commands.Enqueue(timed.Command, timed.Tick);
        }

        var dailyReports = new List<DayReport>();
        var rejections = new List<CommandRejected>();
        sim.Events.Subscribe<DayEnded>(e => dailyReports.Add(e.Report));
        sim.Events.Subscribe<CommandRejected>(rejections.Add);

        for (int day = 0; day < options.Days; day++)
        {
            sim.RunDays(1);
            sim.Events.Dispatch();
        }

        if (options.SavePath is { } savePath)
            SaveGame.Save(state, savePath);

        var output = new RunnerOutput(
            Days: options.Days,
            Kpis: KpiReport.From(state),
            RejectedCommands: rejections.Select(r => new RejectedCommand(r.Tick, r.Command, r.Reason)).ToList(),
            Daily: options.IncludeDaily ? dailyReports : null);

        Console.WriteLine(JsonSerializer.Serialize(output, RunnerJson.Options));
    }
}

internal sealed record RejectedCommand(long Tick, ICommand Command, string Reason);

internal sealed record RunnerOutput(
    int Days,
    KpiReport Kpis,
    List<RejectedCommand> RejectedCommands,
    List<DayReport>? Daily);

internal static class RunnerJson
{
    public static readonly JsonSerializerOptions Options = new(SimJson.Indented)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed record RunnerOptions(
    string ScenarioPath,
    int Days,
    ulong? Seed,
    string? CommandsPath,
    bool IncludeDaily,
    string? SavePath)
{
    public const string Usage =
        "usage: Bikepark.SimRunner --scenario <path> [--days N=30] [--seed S] [--commands <path>] [--daily] [--save <path>]";

    public static RunnerOptions Parse(string[] args)
    {
        string? scenario = null;
        int days = 30;
        ulong? seed = null;
        string? commands = null;
        bool daily = false;
        string? save = null;

        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");

            switch (args[i])
            {
                case "--scenario": scenario = Next(); break;
                case "--days":
                    days = int.TryParse(Next(), out var d) && d >= 0 ? d : throw new ArgumentException("--days must be a non-negative integer");
                    break;
                case "--seed":
                    seed = ulong.TryParse(Next(), out var s) ? s : throw new ArgumentException("--seed must be an unsigned integer");
                    break;
                case "--commands": commands = Next(); break;
                case "--daily": daily = true; break;
                case "--save": save = Next(); break;
                case "-h" or "--help": throw new ArgumentException("help requested");
                default: throw new ArgumentException($"unknown argument '{args[i]}'");
            }
        }

        if (scenario is null)
            throw new ArgumentException("--scenario is required");
        return new RunnerOptions(scenario, days, seed, commands, daily, save);
    }
}
