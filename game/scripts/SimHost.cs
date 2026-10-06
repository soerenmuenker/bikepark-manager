using System.Text.Json;
using Bikepark.Sim;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Godot;

namespace Bikepark.Game;

/// <summary>
/// Owns the <see cref="Simulation"/> and drives it from Godot's frame loop with a fixed-step accumulator.
/// This is the only place real time is converted into ticks (1x = one game minute per 8 real seconds). Other nodes
/// read <see cref="Sim"/>.State, subscribe to <see cref="Sim"/>.Events, and change the world only via
/// <see cref="Enqueue"/>. Views that animate between ticks use <see cref="BeforeStep"/> and
/// <see cref="InterpolationAlpha"/>.
/// Command-line user args (after <c>--</c>): <c>--demo</c> builds the demo network, <c>--speed=N</c> picks a speed index,
/// <c>--report</c> prints a KPI line every game hour (for headless checks), <c>--advance=N</c> simulates N ticks at start
/// (after <c>--demo</c>, e.g. 720 = noon on day 1).
/// </summary>
public partial class SimHost : Node
{
    public static readonly int[] SpeedMultipliers = [0, 1, 4, 16, 60];

    /// <summary>Scenario path relative to the content root (the repo root in the editor, the executable's folder in exports).</summary>
    [Export] public string ScenarioFile { get; set; } = "data/scenarios/starter_valley.json";

    /// <summary>Game minutes per real second at 1x speed (a ~7 min lift ride takes ~1 min real time).</summary>
    [Export] public double TicksPerSecondAtNormalSpeed { get; set; } = 0.125;

    /// <summary>Upper bound on ticks per frame, so a slow frame never snowballs.</summary>
    [Export] public int MaxTicksPerFrame { get; set; } = 240;

    [Export] public string SavePath { get; set; } = "user://savegame.json";

    [Export] public string DemoNetworkFile { get; set; } = "data/scripts/demo_lift_network.json";

    /// <summary>Game minutes per real second while turbo-skipping towards closing time.</summary>
    [Export] public double TurboTicksPerSecond { get; set; } = 120;

    private double _accumulator;
    private long _skipTargetTick = -1;
    private double _skipTicksPerSecond;
    private bool _report;

    public Simulation Sim { get; private set; } = null!;

    public int SpeedIndex { get; private set; } = 1;

    public int Speed => SpeedMultipliers[SpeedIndex];

    /// <summary>True while a skip (to opening hours or turbo to closing hours) is running.</summary>
    public bool IsSkipping => _skipTargetTick >= 0;

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
            else if (arg.StartsWith("--advance=", StringComparison.Ordinal) && long.TryParse(arg[10..], out long ticks)) Sim.RunTicks(ticks);
        }
    }

    public override void _Process(double delta)
    {
        if (Speed > 0 || IsSkipping)
        {
            _accumulator += delta * (IsSkipping ? _skipTicksPerSecond : TicksPerSecondAtNormalSpeed * Speed);
            int ticks = (int)Math.Min(Math.Floor(_accumulator), MaxTicksPerFrame);
            if (IsSkipping)
                ticks = (int)Math.Min(ticks, _skipTargetTick - Sim.State.Tick);
            _accumulator = Math.Min(_accumulator - ticks, MaxTicksPerFrame);
            for (int i = 0; i < ticks; i++)
            {
                BeforeStep?.Invoke();
                Sim.Step();
                if (_report && Sim.State.Tick % 60 == 0) Report();
            }

            if (IsSkipping && Sim.State.Tick >= _skipTargetTick)
                EndSkip();
        }

        Sim.Events.Dispatch();
    }

    private void Report()
    {
        var k = Bikepark.Sim.Reporting.KpiReport.From(Sim.State, includeHash: false);
        GD.Print($"[report] {Bikepark.Sim.Core.GameTime.Format(Sim.State.Tick)} guests={k.GuestsInPark} " +
                 $"onTrails={k.RidersOnTrails} runs={k.RunsCompleted} fun={k.AverageRunFun} ways={k.AccessPaths}+{k.Trails} " +
                 $"queuing={k.GuestsQueuing} onLift={k.RidersOnLifts} liftRides={k.LiftRides} wait={k.AverageWaitMinutes}min " +
                 $"maxQueue={k.MaxQueue} money={k.MoneyCents / 100}");
    }

    /// <summary>Queues the demo trails from the plateau (data/scripts/demo_lift_network.json).</summary>
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
        _skipTargetTick = -1;
        _accumulator = 0;
    }

    /// <summary>Fast-forwards (as fast as the per-frame tick cap allows) to the next opening time, then continues at 1x.</summary>
    public void SkipToOpeningHours() => StartSkip(Sim.State.Rules.OpenMinute, double.MaxValue);

    /// <summary>Runs at turbo speed until the next closing time, then continues at 1x.</summary>
    public void TurboToClosingHours() => StartSkip(Sim.State.Rules.CloseMinute, TurboTicksPerSecond);

    private void StartSkip(int minuteOfDay, double ticksPerSecond)
    {
        long tick = Sim.State.Tick;
        long target = GameTime.Day(tick) * GameTime.MinutesPerDay + minuteOfDay;
        if (target <= tick) target += GameTime.MinutesPerDay;
        _skipTargetTick = target;
        _skipTicksPerSecond = ticksPerSecond;
        _accumulator = 0;
    }

    private void EndSkip()
    {
        _skipTargetTick = -1;
        SetSpeedIndex(1);
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
        _skipTargetTick = -1;
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
