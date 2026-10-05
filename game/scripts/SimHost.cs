using System.Text.Json;
using Bikepark.Sim;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Godot;

namespace Bikepark.Game;

/// <summary>
/// Owns the <see cref="Simulation"/> and drives it from Godot's frame loop with a fixed-step accumulator.
/// This is the only place real time is converted into ticks (1x = one game minute per real second). Other nodes
/// read <see cref="Sim"/>.State, subscribe to <see cref="Sim"/>.Events, and change the world only via
/// <see cref="Enqueue"/>. Views that animate between ticks use <see cref="BeforeStep"/> and
/// <see cref="InterpolationAlpha"/>.
/// Command-line user args (after <c>--</c>): <c>--demo</c> builds the demo network, <c>--speed=N</c> picks a speed index,
/// <c>--report</c> prints a KPI line every game hour (for headless checks).
/// </summary>
public partial class SimHost : Node
{
    public static readonly int[] SpeedMultipliers = [0, 1, 3, 10, 30];

    /// <summary>Scenario path relative to the content root (the repo root in the editor, the executable's folder in exports).</summary>
    [Export] public string ScenarioFile { get; set; } = "data/scenarios/starter_valley.json";

    /// <summary>Game minutes per real second at 1x speed.</summary>
    [Export] public double TicksPerSecondAtNormalSpeed { get; set; } = 1;

    /// <summary>Upper bound on ticks per frame, so a slow frame never snowballs.</summary>
    [Export] public int MaxTicksPerFrame { get; set; } = 240;

    [Export] public string SavePath { get; set; } = "user://savegame.json";

    [Export] public string DemoNetworkFile { get; set; } = "data/scripts/demo_network.json";

    private double _accumulator;
    private bool _report;

    public Simulation Sim { get; private set; } = null!;

    public int SpeedIndex { get; private set; } = 1;

    public int Speed => SpeedMultipliers[SpeedIndex];

    /// <summary>Raised when <see cref="Sim"/> is replaced (new game or load). Subscribers must re-subscribe to its events.</summary>
    public event Action<Simulation>? SimulationReplaced;

    /// <summary>Raised right before every simulation step, so views can remember the previous tick's positions.</summary>
    public event Action? BeforeStep;

    /// <summary>How far real time is between the last tick and the next one (0..1), for smooth animation.</summary>
    public float InterpolationAlpha => (float)Math.Clamp(_accumulator, 0, 1);

    public override void _Ready()
    {
        var scenario = ScenarioLoader.LoadFile(ResolveContentPath(ScenarioFile));
        ReplaceSimulation(new Simulation(ScenarioLoader.CreateWorld(scenario)));

        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg == "--demo") LoadDemoNetwork();
            else if (arg == "--report") _report = true;
            else if (arg.StartsWith("--speed=", StringComparison.Ordinal) && int.TryParse(arg[8..], out int speed)) SetSpeedIndex(speed);
        }
    }

    public override void _Process(double delta)
    {
        if (Speed > 0)
        {
            _accumulator += delta * TicksPerSecondAtNormalSpeed * Speed;
            int ticks = (int)Math.Min(Math.Floor(_accumulator), MaxTicksPerFrame);
            _accumulator = Math.Min(_accumulator - ticks, MaxTicksPerFrame);
            for (int i = 0; i < ticks; i++)
            {
                BeforeStep?.Invoke();
                Sim.Step();
                if (_report && Sim.State.Tick % 60 == 0) Report();
            }
        }

        Sim.Events.Dispatch();
    }

    private void Report()
    {
        var k = Bikepark.Sim.Reporting.KpiReport.From(Sim.State, includeHash: false);
        GD.Print($"[report] {Bikepark.Sim.Core.GameTime.Format(Sim.State.Tick)} guests={k.GuestsInPark} " +
                 $"onTrails={k.RidersOnTrails} runs={k.RunsCompleted} fun={k.AverageRunFun} ways={k.AccessPaths}+{k.Trails}");
    }

    /// <summary>Queues the demo network (gravel path + two trails) from data/scripts.</summary>
    public void LoadDemoNetwork()
    {
        var script = JsonSerializer.Deserialize<List<TimedCommand>>(File.ReadAllText(ResolveContentPath(DemoNetworkFile)), SimJson.Indented) ?? [];
        foreach (var timed in script)
            Enqueue(timed.Command);
    }

    public long Enqueue(ICommand command) => Sim.Commands.Enqueue(command);

    public void SetSpeedIndex(int index)
    {
        SpeedIndex = Math.Clamp(index, 0, SpeedMultipliers.Length - 1);
        _accumulator = 0;
    }

    public void Save()
    {
        SaveGame.Save(Sim.State, ProjectSettings.GlobalizePath(SavePath));
        GD.Print($"Saved to {ProjectSettings.GlobalizePath(SavePath)}");
    }

    public bool Load()
    {
        string path = ProjectSettings.GlobalizePath(SavePath);
        if (!File.Exists(path))
            return false;
        ReplaceSimulation(new Simulation(SaveGame.Load(path)));
        return true;
    }

    private void ReplaceSimulation(Simulation sim)
    {
        Sim = sim;
        _accumulator = 0;
        SimulationReplaced?.Invoke(sim);
    }

    private static string ResolveContentPath(string relative)
    {
        // In the editor the project lives in <repo>/game and content in <repo>/data.
        // Exports are expected to ship the data folder next to the executable.
        string root = OS.HasFeature("editor")
            ? Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."))
            : Path.GetDirectoryName(OS.GetExecutablePath())!;
        return Path.Combine(root, relative);
    }
}
