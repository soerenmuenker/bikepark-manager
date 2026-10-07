using System.Diagnostics;
using System.Text.Json;
using Bikepark.Sim;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.SimRunner.Terrain;

/// <summary>
/// <c>terrain</c> subcommand: generates a scenario's terrain, writes top-down PNG maps, prints stats as JSON.
/// </summary>
internal static class TerrainCommand
{
    public const string Usage =
        "usage: Bikepark.SimRunner terrain --scenario <path> [--out <file.png>] [--mode relief|slope|surface|all] [--seed S] [--scatter] [--commands <file>]";

    public static void Run(string[] args)
    {
        var options = Parse(args);
        var scenario = ScenarioLoader.LoadFile(options.ScenarioPath);
        var state = ScenarioLoader.CreateWorld(scenario, options.Seed);

        var stopwatch = Stopwatch.StartNew();
        var sim = new Simulation(state);
        _ = sim.BaseTerrain;
        long generationMs = stopwatch.ElapsedMilliseconds;

        // The scenario's own commands (lift, parking, hiking route) and build commands (e.g. a way network) are applied
        // in one step so the map can show them.
        var rejected = new List<string>();
        if (options.CommandsPath is { } commandsPath)
        {
            var script = JsonSerializer.Deserialize<List<TimedCommand>>(File.ReadAllText(commandsPath), SimJson.Indented) ?? [];
            foreach (var timed in script)
                sim.Commands.Enqueue(timed.Command, 0);
        }
        if (state.PendingCommands.Count > 0)
        {
            sim.Step();
            rejected.AddRange(sim.Events.Pending.OfType<CommandRejected>().Select(r => $"{r.Command.GetType().Name}: {r.Reason}"));
        }
        var grid = sim.Terrain;
        var network = sim.Network;

        var gone = Forest.GoneFilter(network, state);
        var scatter = TerrainScatter.CollectAll(grid).Where(s => !gone(s.XCm, s.ZCm)).ToList();
        var images = new List<string>();
        if (options.OutPath is { } outPath)
        {
            MapMode[] modes = options.Mode is { } m ? [m] : Enum.GetValues<MapMode>();
            foreach (var mode in modes)
            {
                string path = modes.Length == 1 ? outPath : WithSuffix(outPath, mode.ToString().ToLowerInvariant());
                byte[] pixels = TerrainMapRenderer.Render(grid, mode, options.Scatter ? scatter : null, network);
                PngWriter.WriteRgb(path, grid.Samples, grid.Samples, pixels);
                images.Add(path);
            }
        }

        var stats = Stats(scenario.Id, grid, scatter, generationMs, images, network, rejected);
        Console.WriteLine(JsonSerializer.Serialize(stats, SimJson.Indented));
    }

    private static object Stats(
        string scenarioId, TerrainGrid grid, List<ScatterInstance> scatter, long generationMs, List<string> images,
        WayNetwork network, List<string> rejected)
    {
        int count = grid.Samples * grid.Samples;
        var surfaces = new int[Enum.GetValues<TerrainSurface>().Length];
        long slopeSum = 0;
        int steep = 0;
        for (int z = 0; z < grid.Samples; z++)
            for (int x = 0; x < grid.Samples; x++)
            {
                surfaces[(int)grid.SurfaceAtSample(x, z)]++;
                int slope = grid.SlopeAtSample(x, z);
                slopeSum += slope;
                if (slope >= 700) steep++;
            }

        return new
        {
            scenarioId,
            seed = grid.Seed,
            sizeMeters = grid.SizeMeters,
            minHeightCm = grid.MinHeightCm,
            maxHeightCm = grid.MaxHeightCm,
            peak = new { x = grid.Peak.X, z = grid.Peak.Z, heightCm = grid.MaxHeightCm },
            surfacePercent = Enum.GetValues<TerrainSurface>().ToDictionary(
                s => s.ToString().ToLowerInvariant(), s => Math.Round(100.0 * surfaces[(int)s] / count, 1)),
            meanSlopePermille = slopeSum / count,
            steepPercent = Math.Round(100.0 * steep / count, 1),
            trees = scatter.Count(s => s.Kind == ScatterKind.Tree),
            rocks = scatter.Count(s => s.Kind == ScatterKind.Rock),
            ways = network.Ways.Select(w => new
            {
                w.Name,
                kind = w.Kind.ToString(),
                rating = w.Kind == WayKind.Trail ? network.Geometry(w.Id).Rating.ToString() : null,
                lengthMeters = network.Geometry(w.Id).LengthCm / 100,
            }),
            lifts = network.Links.Where(l => l.Kind == LegKind.Lift).Select(l => new
            {
                id = l.Id,
                lengthMeters = l.LengthCm / 100,
                valley = network.FindHub(l.FromHubId)!.Pad,
                mountain = network.FindHub(l.ToHubId)!.Pad,
            }),
            hubs = network.Hubs.Select(h => new { h.Id, kind = h.Kind.ToString(), x = h.Pad.CenterX / 100, z = h.Pad.CenterZ / 100, heightCm = h.Pad.TargetHeightCm }),
            rejectedCommands = rejected,
            terrainHash = grid.ComputeHash(),
            generationMs,
            images,
        };
    }

    private static string WithSuffix(string path, string suffix)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        return Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)}_{suffix}{Path.GetExtension(path)}");
    }

    private sealed record Options(string ScenarioPath, string? OutPath, MapMode? Mode, ulong? Seed, bool Scatter, string? CommandsPath);

    private static Options Parse(string[] args)
    {
        string? scenario = null, outPath = null;
        MapMode? mode = MapMode.Relief;
        ulong? seed = null;
        bool scatter = false;
        string? commands = null;

        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");

            switch (args[i])
            {
                case "--scenario": scenario = Next(); break;
                case "--out": outPath = Next(); break;
                case "--mode":
                    string value = Next();
                    mode = value == "all" ? null
                        : Enum.TryParse<MapMode>(value, ignoreCase: true, out var parsed) ? parsed
                        : throw new ArgumentException("--mode must be relief, slope, surface or all");
                    break;
                case "--seed":
                    seed = ulong.TryParse(Next(), out var s) ? s : throw new ArgumentException("--seed must be an unsigned integer");
                    break;
                case "--scatter": scatter = true; break;
                case "--commands": commands = Next(); break;
                case "-h" or "--help": throw new ArgumentException("help requested");
                default: throw new ArgumentException($"unknown argument '{args[i]}'");
            }
        }

        if (scenario is null)
            throw new ArgumentException("--scenario is required");
        return new Options(scenario, outPath, mode, seed, scatter, commands);
    }
}
