using Bikepark.Sim;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Godot;

namespace Bikepark.Game;

/// <summary>
/// Owns the <see cref="Simulation"/> and drives it from Godot's frame loop with a fixed-step accumulator.
/// This is the only place real time is converted into ticks. Other nodes read <see cref="Sim"/>.State,
/// subscribe to <see cref="Sim"/>.Events, and change the world only via <see cref="Enqueue"/>.
/// </summary>
public partial class SimHost : Node
{
    public static readonly int[] SpeedMultipliers = [0, 1, 4, 16];

    /// <summary>Scenario path relative to the content root (the repo root in the editor, the executable's folder in exports).</summary>
    [Export] public string ScenarioFile { get; set; } = "data/scenarios/starter_valley.json";

    /// <summary>Game minutes per real second at 1x speed.</summary>
    [Export] public double TicksPerSecondAtNormalSpeed { get; set; } = 10;

    /// <summary>Upper bound on ticks per frame, so a slow frame never snowballs.</summary>
    [Export] public int MaxTicksPerFrame { get; set; } = 240;

    [Export] public string SavePath { get; set; } = "user://savegame.json";

    private double _accumulator;

    public Simulation Sim { get; private set; } = null!;

    public int SpeedIndex { get; private set; } = 1;

    public int Speed => SpeedMultipliers[SpeedIndex];

    /// <summary>Raised when <see cref="Sim"/> is replaced (new game or load). Subscribers must re-subscribe to its events.</summary>
    public event Action<Simulation>? SimulationReplaced;

    public override void _Ready()
    {
        var scenario = ScenarioLoader.LoadFile(ResolveContentPath(ScenarioFile));
        ReplaceSimulation(new Simulation(ScenarioLoader.CreateWorld(scenario)));
    }

    public override void _Process(double delta)
    {
        if (Speed > 0)
        {
            _accumulator += delta * TicksPerSecondAtNormalSpeed * Speed;
            int ticks = (int)Math.Min(Math.Floor(_accumulator), MaxTicksPerFrame);
            _accumulator = Math.Min(_accumulator - ticks, MaxTicksPerFrame);
            Sim.RunTicks(ticks);
        }

        Sim.Events.Dispatch();
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
