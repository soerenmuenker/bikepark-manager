using System.Text.Json;
using Bikepark.Sim;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.Systems;
using Godot;

namespace Bikepark.Game;

/// <summary>
/// Owns the <see cref="Simulation"/> and drives it from Godot's frame loop with a fixed-step accumulator.
/// This is the only place real time is converted into ticks (1x = one game minute per 8 real seconds). Other nodes
/// read <see cref="Sim"/>.State, subscribe to <see cref="Sim"/>.Events, and change the world only via
/// <see cref="Enqueue"/>. Views that animate between ticks use <see cref="BeforeStep"/> and
/// <see cref="InterpolationAlpha"/>.
/// Command-line user args (after <c>--</c>): <c>--demo</c> builds the demo network (<c>--demo-planned</c>: as crew jobs),
/// <c>--demo-features</c> then plans the
/// demo features on it (simulates the first minute so the trails exist), <c>--demo-crew</c> adds a worker, tools and a
/// felling area (data/scripts/demo_crew.json), <c>--instant</c> turns on instant building (debug), <c>--speed=N</c> picks a speed index,
/// <c>--report</c> prints a KPI line every game hour (for headless checks), <c>--advance=N</c> simulates N ticks at start
/// (after <c>--demo</c>, e.g. 720 = noon on day 1), <c>--no-night-skip</c> turns off the automatic night skip,
/// <c>--script=&lt;file&gt;</c> queues a command script (path relative to the repo, at the script's ticks; put it before
/// <c>--advance</c>).
/// Night skip: whenever the park is quiet (<see cref="ParkSchedule.IsQuiet"/>: closed, empty, crew off) time
/// fast-forwards to <see cref="ParkSchedule.NextWakeTick"/> and then continues at the speed it had. Skips always step
/// every tick, so the sim sees each minute.
/// </summary>
public partial class SimHost : Node
{
    public static readonly int[] SpeedMultipliers = [0, 1, 4, 16, 60];

    /// <summary>Scenario path relative to the content root (the repo root in the editor, the executable's folder in exports).</summary>
    [Export] public string ScenarioFile { get; set; } = "data/scenarios/demo_valley.json";

    /// <summary>The scenarios offered on the start screen: the sandbox demo and the career.</summary>
    public const string DemoScenarioFile = "data/scenarios/demo_valley.json";
    public const string CareerScenarioFile = "data/scenarios/starter_valley.json";

    /// <summary>
    /// True until the player picked a scenario on the start screen (the demo world waits paused behind it). Debug runs
    /// that set up a world (<c>--demo</c>, <c>--scenario=</c>, <c>--script=</c>, <c>--advance=</c>) start right away.
    /// </summary>
    public bool ShowStartScreen { get; private set; }

    /// <summary>Game minutes per real second at 1x speed (a ~7 min lift ride takes ~1 min real time).</summary>
    [Export] public double TicksPerSecondAtNormalSpeed { get; set; } = 0.125;

    /// <summary>Upper bound on ticks per frame, so a slow frame never snowballs.</summary>
    [Export] public int MaxTicksPerFrame { get; set; } = 240;

    [Export] public string DemoNetworkFile { get; set; } = "data/scripts/demo_lift_network.json";

    [Export] public string DemoFeaturesFile { get; set; } = "data/scripts/demo_features.json";

    [Export] public string DemoCrewFile { get; set; } = "data/scripts/demo_crew.json";

    /// <summary>Debug: the build tools build ways and features at once instead of planning crew jobs.</summary>
    public bool InstantBuild { get; set; }

    /// <summary>Fast-forward through quiet nights (closed, empty park, crew off) automatically.</summary>
    public bool AutoSkipNights { get; set; } = true;

    /// <summary>Game minutes per real second during the night skip (a 12-hour night passes in about 2.5 s).</summary>
    [Export] public double NightSkipTicksPerSecond { get; set; } = 300;

    /// <summary>Game minutes per real second while turbo-skipping towards closing time.</summary>
    [Export] public double TurboTicksPerSecond { get; set; } = 120;

    private double _accumulator;
    private long _skipTargetTick = -1;
    private double _skipTicksPerSecond;
    private int _speedAfterSkip = 1;
    private bool _nightSkip;
    private bool _report;

    public Simulation Sim { get; private set; } = null!;

    public int SpeedIndex { get; private set; } = 1;

    public int Speed => SpeedMultipliers[SpeedIndex];

    /// <summary>True while a skip (night skip, to opening hours or turbo to closing hours) is running.</summary>
    public bool IsSkipping => _skipTargetTick >= 0;

    /// <summary>True while the automatic night skip runs.</summary>
    public bool IsSkippingNight => IsSkipping && _nightSkip;

    /// <summary>Raised when <see cref="Sim"/> is replaced (new game or load). Subscribers must re-subscribe to its events.</summary>
    public event Action<Simulation>? SimulationReplaced;

    /// <summary>Raised right before every simulation step, so views can remember the previous tick's positions.</summary>
    public event Action? BeforeStep;

    /// <summary>How far real time is between the last tick and the next one (0..1), for smooth animation.</summary>
    public float InterpolationAlpha => (float)Math.Clamp(_accumulator, 0, 1);

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        foreach (string arg in args)
            if (arg.StartsWith("--scenario=", StringComparison.Ordinal))
                ScenarioFile = arg[11..] switch
                {
                    "demo" or "demo_valley" => DemoScenarioFile,
                    "starter" or "starter_valley" or "career" => CareerScenarioFile,
                    var file => file,
                };
        var scenario = ScenarioLoader.LoadFile(ResolveContentPath(ScenarioFile));
        ReplaceSimulation(new Simulation(ScenarioLoader.CreateWorld(scenario)));
        foreach (string arg in args)
        {
            if (arg.StartsWith("--new-career=", StringComparison.Ordinal)) NewCareer(arg[13..]);
            else if (arg.StartsWith("--career=", StringComparison.Ordinal)) ContinueCareer(arg[9..]);
            else if (arg == "--list-careers")
                foreach (var career in CareerStore.List())
                    GD.Print($"career {career.Id}: {career.ParkName}, day {career.Day + 1}, level {career.Level}, {career.MoneyCents / 100} €");
        }
        // Debug runs that set up a world (--demo, --scenario=, --script=, --advance=, a career) skip the start screen.
        if (CurrentCareerId is null
            && !args.Any(a => a.StartsWith("--demo", StringComparison.Ordinal) || a.StartsWith("--scenario=", StringComparison.Ordinal)
                              || a.StartsWith("--script=", StringComparison.Ordinal) || a.StartsWith("--advance=", StringComparison.Ordinal)))
        {
            ShowStartScreen = true;
            SetSpeedIndex(0);
        }

        foreach (string arg in args)
        {
            if (arg == "--demo") LoadDemoNetwork();
            else if (arg == "--demo-planned") LoadDemoNetwork(planned: true);
            else if (arg == "--demo-features")
            {
                Sim.Step();
                LoadDemoFeatures();
            }
            else if (arg == "--demo-crew") LoadDemoCrew();
            else if (arg == "--instant") InstantBuild = true;
            else if (arg == "--report") _report = true;
            else if (arg == "--no-night-skip") AutoSkipNights = false;
            else if (arg.StartsWith("--script=", StringComparison.Ordinal))
            {
                var script = ReadScript(arg[9..]);
                foreach (var timed in script) Sim.Commands.Enqueue(timed.Command, timed.Tick);
                GD.Print($"Queued {script.Count} commands from {arg[9..]}");
            }
            else if (arg.StartsWith("--speed=", StringComparison.Ordinal) && int.TryParse(arg[8..], out int speed)) SetSpeedIndex(speed);
            else if (arg.StartsWith("--advance=", StringComparison.Ordinal) && long.TryParse(arg[10..], out long ticks)) Sim.RunTicks(ticks);
        }
    }

    public override void _Process(double delta)
    {
        if (AutoSkipNights && Speed > 0 && !IsSkipping && ParkSchedule.IsQuiet(Sim.State, Sim.State.Tick))
            StartSkip(ParkSchedule.NextWakeTick(Sim.State, Sim.State.Tick), NightSkipTicksPerSecond, SpeedIndex, night: true);

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
        var k = Bikepark.Sim.Reporting.KpiReport.From(Sim.State, includeHash: false, network: Sim.Network);
        GD.Print($"[report] {Bikepark.Sim.Core.GameTime.Format(Sim.State.Tick)} guests={k.GuestsInPark} " +
                 $"onTrails={k.RidersOnTrails} runs={k.RunsCompleted} fun={k.AverageRunFun} ways={k.AccessPaths}+{k.Trails} " +
                 $"queuing={k.GuestsQueuing} onLift={k.RidersOnLifts} liftRides={k.LiftRides} wait={k.AverageWaitMinutes}min " +
                 $"maxQueue={k.MaxQueue} money={k.MoneyCents / 100}");
    }

    /// <summary>
    /// Queues the demo trails from the plateau (data/scripts/demo_lift_network.json), built at once, or
    /// <paramref name="planned"/> as crew jobs (debug: <c>--demo-planned</c>).
    /// </summary>
    public void LoadDemoNetwork() => LoadDemoNetwork(planned: false);

    public void LoadDemoNetwork(bool planned)
    {
        foreach (var timed in ReadScript(DemoNetworkFile))
            Enqueue(planned && timed.Command is BuildWayCommand build ? build with { Instant = false } : timed.Command);
    }

    /// <summary>
    /// Queues the demo features (data/scripts/demo_features.json) on the demo trails. The script's way ids are those of a
    /// fresh world; they are mapped, in order, to the demo network's trails by name. Returns false if those aren't built.
    /// </summary>
    public bool LoadDemoFeatures()
    {
        var names = ReadScript(DemoNetworkFile).Select(t => t.Command).OfType<BuildWayCommand>().Select(c => c.Name).ToList();
        var script = ReadScript(DemoFeaturesFile);
        var scriptIds = script.Select(t => t.Command).OfType<PlaceTrailFeatureCommand>().Select(c => c.WayId).Distinct().Order().ToList();
        var ids = new Dictionary<int, int>();
        for (int i = 0; i < scriptIds.Count && i < names.Count; i++)
        {
            var way = Sim.State.Ways.FirstOrDefault(w => w.Name == names[i]);
            if (way is null) return false;
            ids[scriptIds[i]] = way.Id;
        }
        foreach (var command in script.Select(t => t.Command).OfType<PlaceTrailFeatureCommand>())
            if (ids.TryGetValue(command.WayId, out int wayId))
                Enqueue(command with { WayId = wayId, Instant = InstantBuild });
        return true;
    }

    /// <summary>Queues the demo crew setup (data/scripts/demo_crew.json): a worker, tools and a felling area.</summary>
    public void LoadDemoCrew()
    {
        foreach (var timed in ReadScript(DemoCrewFile))
            Enqueue(timed.Command);
    }

    private List<TimedCommand> ReadScript(string file) =>
        JsonSerializer.Deserialize<List<TimedCommand>>(File.ReadAllText(ResolveContentPath(file)), SimJson.Indented) ?? [];

    public long Enqueue(ICommand command) => Sim.Commands.Enqueue(command);

    public void SetSpeedIndex(int index)
    {
        SpeedIndex = Math.Clamp(index, 0, SpeedMultipliers.Length - 1);
        _skipTargetTick = -1;
        // Keep the progress towards the next tick: views interpolate with it, so pausing freezes the current frame
        // (resetting it would snap everything back to the previous minute). Drop any backlog of whole ticks.
        _accumulator = Math.Clamp(_accumulator, 0, 0.999);
    }

    /// <summary>Fast-forwards (as fast as the per-frame tick cap allows) to the next opening time, then continues at 1x.</summary>
    public void SkipToOpeningHours() => StartSkip(NextAt(Sim.State.Rules.OpenMinute), double.MaxValue, 1, night: false);

    /// <summary>Runs at turbo speed until the next closing time, then continues at 1x.</summary>
    public void TurboToClosingHours() => StartSkip(NextAt(Sim.State.Rules.CloseMinute), TurboTicksPerSecond, 1, night: false);

    private long NextAt(int minuteOfDay)
    {
        long tick = Sim.State.Tick;
        long target = GameTime.Day(tick) * GameTime.MinutesPerDay + minuteOfDay;
        return target <= tick ? target + GameTime.MinutesPerDay : target;
    }

    private void StartSkip(long targetTick, double ticksPerSecond, int speedAfter, bool night)
    {
        _skipTargetTick = targetTick;
        _skipTicksPerSecond = ticksPerSecond;
        _speedAfterSkip = Math.Max(1, speedAfter);
        _nightSkip = night;
        _accumulator = 0;
    }

    private void EndSkip()
    {
        _skipTargetTick = -1;
        _nightSkip = false;
        SetSpeedIndex(_speedAfterSkip);
    }

    /// <summary>
    /// Starts a new game from the start screen: the scenario fresh, plus (for the sandbox demo) the demo trails and their
    /// features, built at once. Runs at 1x.
    /// </summary>
    public void NewGame(string scenarioFile, bool demoContent)
    {
        ScenarioFile = scenarioFile;
        var scenario = ScenarioLoader.LoadFile(ResolveContentPath(scenarioFile));
        ReplaceSimulation(new Simulation(ScenarioLoader.CreateWorld(scenario)));
        if (demoContent)
        {
            LoadDemoNetwork();
            Sim.Step();
            bool instant = InstantBuild;
            InstantBuild = true;
            LoadDemoFeatures();
            InstantBuild = instant;
        }
        CurrentCareerId = null;
        ShowStartScreen = false;
        SetSpeedIndex(1);
    }

    // ---------------------------------------------------------------- careers

    /// <summary>The career being played (its save file id), or null (the Demo, which is never saved).</summary>
    public string? CurrentCareerId { get; private set; }

    /// <summary>Raised after the career was saved (back to the menu, quitting).</summary>
    public event Action<CareerInfo>? CareerSaved;

    /// <summary>Starts a new Starter Valley career with this park name and saves it at once (so it is in the list).</summary>
    public void NewCareer(string parkName)
    {
        ScenarioFile = CareerScenarioFile;
        var scenario = ScenarioLoader.LoadFile(ResolveContentPath(CareerScenarioFile));
        if (!string.IsNullOrWhiteSpace(parkName))
            scenario.ParkName = parkName.Trim()[..Math.Min(parkName.Trim().Length, RenameParkCommand.MaxLength)];
        ReplaceSimulation(new Simulation(ScenarioLoader.CreateWorld(scenario)));
        CurrentCareerId = CareerStore.NewId(Sim.State.Park.Name);
        SaveCareer();
        ShowStartScreen = false;
        SetSpeedIndex(1);
    }

    /// <summary>Continues a saved career where it was left.</summary>
    public void ContinueCareer(string id)
    {
        ReplaceSimulation(new Simulation(CareerStore.Load(id)));
        CurrentCareerId = id;
        ShowStartScreen = false;
        SetSpeedIndex(1);
    }

    /// <summary>Saves the current career (no-op in the Demo). Saves happen between frames, never in the middle of a tick.</summary>
    public bool SaveCareer()
    {
        if (CurrentCareerId is not { } id || Sim is null) return false;
        var info = CareerStore.Save(id, Sim);
        GD.Print($"Saved career {id} (day {info.Day + 1}) to {CareerStore.Directory}");
        CareerSaved?.Invoke(info);
        return true;
    }

    /// <summary>Back into the game that waited behind the start screen (the open career), at 1x.</summary>
    public void ResumeFromMenu()
    {
        ShowStartScreen = false;
        SetSpeedIndex(1);
    }

    /// <summary>Saves the career and shows the start screen (the game waits, paused, behind it).</summary>
    public void BackToMenu()
    {
        SaveCareer();
        ShowStartScreen = true;
        SetSpeedIndex(0);
    }

    // The career is saved automatically when the window closes (or the game quits otherwise).
    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) SaveCareer();
    }

    public override void _ExitTree() => SaveCareer();

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
