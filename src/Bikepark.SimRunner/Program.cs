using System.Text.Json;
using Bikepark.Sim;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Reporting;
using Bikepark.Sim.Reputation;
using Bikepark.Sim.Safety;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.Trails;
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

        foreach (string commandsPath in options.CommandsPaths)
        {
            var script = JsonSerializer.Deserialize<List<TimedCommand>>(File.ReadAllText(commandsPath), SimJson.Indented) ?? [];
            foreach (var timed in script)
                sim.Commands.Enqueue(timed.Command, timed.Tick);
        }

        var dailyReports = new List<DayReport>();
        var rejections = new List<CommandRejected>();
        sim.Events.Subscribe<DayEnded>(e => dailyReports.Add(e.Report));
        sim.Events.Subscribe<CommandRejected>(rejections.Add);
        var leftReasons = new List<GuestLeaveReason>();
        sim.Events.Subscribe<GuestLeft>(e => leftReasons.Add(e.Reason));
        var turnedAway = new List<TurnAwayReason>();
        sim.Events.Subscribe<GuestTurnedAway>(e => turnedAway.Add(e.Reason));
        var completed = new List<string>();
        sim.Events.Subscribe<JobCompleted>(e => completed.Add($"{GameTime.Format(e.Tick)} {e.Title}"));

        var influencers = new List<string>();
        sim.Events.Subscribe<InfluencerArrived>(e => influencers.Add($"{GameTime.Format(e.Tick)} {e.Name} arrived"));
        sim.Events.Subscribe<InfluencerPosted>(e => influencers.Add($"{GameTime.Format(e.Tick)} {e.Post.Name} posted"));
        sim.Events.Subscribe<LevelChanged>(e => influencers.Add($"{GameTime.Format(e.Tick)} level {e.OldLevel} -> {e.NewLevel}"));

        var landLog = new List<string>();
        sim.Events.Subscribe<ParcelBought>(e => landLog.Add($"{GameTime.Format(e.Tick)} bought {e.ParcelId} for {e.PriceCents / 100} €"));
        sim.Events.Subscribe<LiftConstructionStarted>(e => landLog.Add(
            $"{GameTime.Format(e.Tick)} {(e.Restoration ? "restoring" : "building")} {sim.State.Lifts.FirstOrDefault(l => l.Id == e.LiftId)?.Name} for {e.CostCents / 100} €, ready {GameTime.Format(e.ReadyTick)}"));
        sim.Events.Subscribe<LiftStopped>(e => landLog.Add(
            $"{GameTime.Format(e.Tick)} {sim.State.Lifts.FirstOrDefault(l => l.Id == e.LiftId)?.Name} stopped until {GameTime.Format(e.UntilTick)} (crash on the track)"));
        sim.Events.Subscribe<LiftReady>(e => landLog.Add($"{GameTime.Format(e.Tick)} {sim.State.Lifts.FirstOrDefault(l => l.Id == e.LiftId)?.Name} is running"));

        var crashes = new List<string>();
        sim.Events.Subscribe<RiderCrashed>(e => crashes.Add(
            $"{GameTime.Format(e.Tick)} {e.Severity} {e.Cause} on {sim.State.Ways.FirstOrDefault(w => w.Id == e.WayId)?.Name} at {e.Cm / 100} m"
            + (e.FeatureId != 0 ? $" ({sim.State.Ways.SelectMany(w => w.Features).FirstOrDefault(f => f.Id == e.FeatureId)?.TypeId})" : "")));

        var hourly = new List<HourReport>();
        int runsThisHour = 0, lunchesThisHour = 0;
        sim.Events.Subscribe<RunFinished>(_ => runsThisHour++);
        sim.Events.Subscribe<GuestAteLunch>(_ => lunchesThisHour++);

        for (int day = 0; day < options.Days; day++)
        {
            if (options.IncludeHourly && day == options.Days - 1)
                RunDayHourly(sim, hourly, () => (runsThisHour, lunchesThisHour), () => runsThisHour = lunchesThisHour = 0);
            else
            {
                if (options.AutoRepair)
                    for (int minute = 0; minute < GameTime.MinutesPerDay; minute++)
                    {
                        sim.Step();
                        SendCrewToWarnings(sim);
                    }
                else
                    sim.RunDays(1);
                sim.Events.Dispatch();
            }
        }

        if (options.SavePath is { } savePath)
            SaveGame.Save(state, savePath);

        var leaveReasons = leftReasons.GroupBy(r => r).OrderBy(g => g.Key)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());
        var output = new RunnerOutput(
            Days: options.Days,
            Kpis: KpiReport.From(state, network: sim.Network),
            Reputation: ReputationReport(sim, influencers),
            Safety: SafetyReport(sim, crashes),
            Land: LandReport(sim, landLog),
            Ways: WayReports(sim),
            Lifts: LiftReports(sim),
            Crew: CrewReport(sim, completed),
            GuestsLeft: leaveReasons,
            TurnedAway: turnedAway.GroupBy(r => r).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            RejectedCommands: rejections.Select(r => new RejectedCommand(r.Tick, r.Command, r.Reason)).ToList(),
            Daily: options.IncludeDaily ? dailyReports : null,
            Hourly: options.IncludeHourly ? hourly : null);

        Console.WriteLine(JsonSerializer.Serialize(output, RunnerJson.Options));
    }

    private static ReputationReport? ReputationReport(Simulation sim, List<string> log)
    {
        var state = sim.State;
        var rules = state.ReputationRules;
        var xp = ParkProgress.Xp(state, sim.Network);
        int level = ParkProgress.Level(rules, xp.Total);
        var demand = ReputationMath.Demand(state);
        return new ReputationReport(
            Enabled: rules.Enabled,
            Stars: Stars(ReputationMath.RatingTenths(state)),
            Reviews: state.Reputation.TotalReviews,
            Groups: rules.Groups.Select(g => new GroupReport(g.Name, Stars(ReputationMath.RatingTenths(state, g.Group)),
                state.Reputation.ReviewsOf(g.Group), state.Reputation.Reviews.Count(r => r.Group == g.Group))).ToList(),
            DemandPermille: demand.Total,
            VisibilityPermille: demand.Visibility,
            RatingFactorPermille: demand.Rating,
            InfluencerPermille: demand.Influencer,
            Xp: xp.Total,
            Level: level,
            NextLevelXp: ParkProgress.NextLevelXp(rules, level),
            XpParts: xp,
            Posts: state.Reputation.Posts.Select(p =>
                $"day {p.Day} {p.Name}: {Stars(p.StarsTenths)} stars, demand {p.EffectPermille / 10:+0;-0;0} % until day {p.EndsDay}").ToList(),
            Log: log);
    }

    private static LandReport? LandReport(Simulation sim, List<string> log)
    {
        var state = sim.State;
        if (state.Parcels.Count == 0 && state.Finance.TotalLiftBuildCents == 0) return null;
        return new LandReport(
            ParkProgress.CurrentLevel(state, sim.Network),
            state.Parcels.Select(p => new ParcelReport(p.Id, p.Name, state.OwnedParcelIds.Contains(p.Id), p.RequiredLevel, p.PriceCents,
                Bikepark.Sim.Land.LandMath.AreaSquareMeters(p.Outline))).ToList(),
            state.Finance.TotalLandCents, state.Finance.TotalLiftBuildCents, state.Finance.TotalLiftUpkeepCents, log);
    }

    private static SafetyReport? SafetyReport(Simulation sim, List<string> log)
    {
        var state = sim.State;
        if (!state.CrashRules.Enabled) return null;
        var s = state.Safety;
        return new SafetyReport(
            s.TotalCrashes, s.TotalSerious, s.TotalCollisions, s.Evacuations,
            s.Evacuations == 0 ? null : (int)(s.SumRescueMinutes / s.Evacuations),
            s.TotalInsuranceCents, CrashMath.Premium(state.CrashRules, s),
            sim.Network.Crossings.Select(c =>
                $"{state.Ways.First(w => w.Id == c.WayA).Name} {c.CmA / 100} m x {state.Ways.First(w => w.Id == c.WayB).Name} {c.CmB / 100} m")
                .Concat(state.Ways.SelectMany(w => sim.Network.TowCrossingsOn(w.Id).Select(t =>
                    $"{w.Name} {t.Cm / 100} m x {state.Lifts.First(l => l.Id == t.LiftId).Name} track {t.LiftCm / 100} m"))).ToList(),
            log);
    }

    private static string? Stars(int? tenths) => tenths is { } t ? $"{t / 10}.{t % 10}" : null;

    /// <summary>Runs one day minute by minute and averages what is going on per hour (people counts are per-minute averages).</summary>
    private static void RunDayHourly(Simulation sim, List<HourReport> hourly, Func<(int Runs, int Lunches)> counts, Action resetCounts)
    {
        var state = sim.State;
        for (int hour = 0; hour < 24; hour++)
        {
            sim.Events.Dispatch();
            resetCounts();
            var phase = Bikepark.Sim.Systems.ParkSchedule.Phase(state, state.Tick);
            long guests = 0, trails = 0, eating = 0, queuing = 0, crew = 0;
            int liftMinutes = 0, rainMinutes = 0;
            for (int minute = 0; minute < GameTime.MinutesPerHour; minute++)
            {
                if (state.Lifts.Any(l => Bikepark.Sim.Systems.ParkSchedule.LiftRunning(state, l, state.Tick))) liftMinutes++;
                if (Bikepark.Sim.Weather.WeatherMath.IsRaining(state, state.Tick)) rainMinutes++;
                sim.Step();
                guests += state.Guests.Count;
                trails += state.Guests.Count(g => g.Activity == Bikepark.Sim.State.RiderActivity.Descending);
                eating += state.Guests.Count(g => g.Activity == Bikepark.Sim.State.RiderActivity.Eating);
                queuing += state.Guests.Count(g => g.Activity == Bikepark.Sim.State.RiderActivity.Queuing);
                crew += state.Crew.Count(m => m.JobId != 0);
            }
            sim.Events.Dispatch();
            var (runs, lunches) = counts();
            hourly.Add(new HourReport($"{hour:00}:00", phase.ToString(), (int)(guests / 60), (int)(trails / 60), (int)(eating / 60),
                (int)(queuing / 60), runs, lunches, Math.Round(crew / 60.0, 1), liftMinutes, rainMinutes, state.Weather.WetnessPermille,
                state.Ways.Count(w => w.Kind == WayKind.Trail && w.Built && !w.IsRideable)));
        }
    }

    /// <summary>Stand-in for the player (<c>--auto-repair</c>): sends a full crew to every feature below the warning level.</summary>
    private static void SendCrewToWarnings(Simulation sim)
    {
        var state = sim.State;
        foreach (var trail in state.Ways)
            foreach (var feature in trail.Features)
                if (feature.Built && feature.Condition / 1000 < state.WearRules.WarnBelowPermille
                    && Bikepark.Sim.Crew.Jobs.ForRepair(state, feature.Id) is null)
                    sim.Commands.Enqueue(new RepairFeatureCommand(trail.Id, feature.Id, state.CrewRules.MaxWorkersPerJob));
    }

    private static List<WayReport> WayReports(Simulation sim) => sim.State.Ways.Select(w =>
    {
        var geometry = sim.Network.Geometry(w.Id);
        bool trail = w.Kind == WayKind.Trail;
        return new WayReport(
            w.Id, w.Name, w.Kind,
            w.Built ? null : true,
            trail ? geometry.Rating : null,
            trail ? geometry.DifficultyScore : null,
            trail ? sim.Network.FeaturesOn(w.Id).Select(f => $"{f.Type.Id}@{f.StartCm / 100}m{(f.Feature.Built ? "" : " (planned)")}").ToList() : null,
            geometry.LengthCm / 100,
            Math.Abs(geometry.StartHeightCm - geometry.EndHeightCm) / 100,
            Gradient.Format(-geometry.MaxDropGradient),
            Gradient.Format(geometry.MaxClimbGradient),
            w.Stats.Runs,
            w.Stats.Runs == 0 ? null : Math.Round((double)w.Stats.SumRunMinutes / w.Stats.Runs, 1),
            w.Stats.Runs == 0 ? null : (int)(w.Stats.SumFun / w.Stats.Runs),
            trail && w.Built ? !w.IsRideable ? w.WornOut ? "worn out" : "closed" : "open" : null,
            trail ? w.Features.Where(f => f.Built).Select(f => $"{f.TypeId}@{f.DistanceCm / 100}m {TrailCondition.Permille(f) / 10.0:0.#} %"
                                                                 + (f.Crashes > 0 ? $", {f.Crashes} crashes" : "")).ToList() : null,
            trail ? TrailCondition.WorstPermille(w) : null,
            trail ? w.Stats.ClosedMinutes : null,
            trail ? w.Stats.Repairs : null,
            trail ? w.Stats.HeldUpSeconds / 60 : null,
            trail ? w.Stats.Crashes : null,
            trail ? w.Stats.SeriousCrashes : null,
            trail ? w.Stats.Collisions : null,
            trail && w.Stats.Runs > 0 ? Math.Round(w.Stats.Crashes * 1000.0 / w.Stats.Runs, 1) : null);
    }).ToList();

    private static CrewReport? CrewReport(Simulation sim, List<string> completed)
    {
        var state = sim.State;
        if (state.Crew.Count == 0 && state.Jobs.Count == 0 && completed.Count == 0) return null;
        var stats = state.CrewStats;
        var rules = state.CrewRules;
        return new CrewReport(
            state.Crew.Count,
            state.OwnedToolIds,
            state.WoodStock,
            stats.TreesFelled,
            stats.WoodFelled,
            stats.WoodBought,
            stats.WoodUsed,
            Math.Round(stats.CrewMinutesWorked / 60.0, 1),
            state.Finance.TotalWagesCents,
            state.Finance.TotalToolsCents + state.Finance.TotalWoodCents,
            completed,
            state.Jobs.Select(j => $"{Jobs.Title(state, j)}: {WorkCosts.ProgressPermille(rules, j) / 10} %" +
                                   (!Bikepark.Sim.Systems.JobSystem.IsWorkable(state, j) ? " (waiting)" : Jobs.IsWaitingForRiders(state, j) ? " (waiting for riders)" : "")).ToList());
    }

    private static List<LiftReport> LiftReports(Simulation sim)
    {
        var state = sim.State;
        return state.Lifts.Select(l =>
        {
            var type = LiftNetwork.FindType(state, l.TypeId)!;
            var (horizontal, rise, length) = LiftMath.Line(LiftNetwork.Pad(state, l.Valley.TerrainEditId)!, LiftNetwork.Pad(state, l.Mountain.TerrainEditId)!);
            var op = LiftNetwork.FindOperator(state, l.OperatorId);
            var tier = op is not null && l.BikeAccess is { } access ? op.BikeAccessTiers[access.TierIndex] : null;
            return new LiftReport(
                l.Id, l.Name, type.Name, op?.Name, tier?.Name, l.BikeCarrierPermille,
                LiftMath.BikeRidersPerHour(type, l.BikeCarrierPermille),
                length / 100, rise / 100, Math.Round(LiftMath.RideSeconds(type, length) / 60.0, 1),
                l.Stats.Riders,
                l.Stats.Riders == 0 ? null : Math.Round((double)l.Stats.SumWaitMinutes / l.Stats.Riders, 1),
                l.Stats.MaxQueue,
                l.Queue.Count,
                tier?.DailyFeeCents,
                l.Derelict ? l.ReadyTick > 0 ? "restoring" : "derelict" : l.ReadyTick > 0 ? "under construction" : "running",
                l.ReadyTick > 0 ? GameTime.Format(l.ReadyTick) : null,
                l.OperatorId is null ? type.UpkeepPerDayCents : null);
        }).ToList();
    }
}

internal sealed record LiftReport(
    int Id,
    string Name,
    string Type,
    string? Operator,
    string? BikeAccessTier,
    int BikeCarrierPermille,
    int BikeRidersPerHour,
    long LengthMeters,
    long RiseMeters,
    double RideMinutes,
    long Riders,
    double? AverageWaitMinutes,
    int MaxQueue,
    int QueueNow,
    long? DailyFeeCents,
    string Status,
    string? ReadyAt,
    long? UpkeepPerDayCents);

internal sealed record ParcelReport(string Id, string Name, bool Owned, int RequiredLevel, long PriceCents, long AreaSquareMeters);

internal sealed record LandReport(int Level, List<ParcelReport> Parcels, long TotalLandCents, long TotalLiftBuildCents, long TotalLiftUpkeepCents, List<string> Log);

internal sealed record HourReport(
    string Hour,
    string PhaseAtStart,
    int Guests,
    int OnTrails,
    int Eating,
    int Queuing,
    int RunsFinished,
    int LunchesStarted,
    double CrewWorking,
    int LiftMinutes,
    int RainMinutes,
    int WetnessPermille,
    int TrailsClosed);

internal sealed record RejectedCommand(long Tick, ICommand Command, string Reason);

internal sealed record CrewReport(
    int Workers,
    List<string> Tools,
    int WoodStock,
    long TreesFelled,
    long WoodFelled,
    long WoodBought,
    long WoodUsed,
    double CrewHoursWorked,
    long WagesCents,
    long ToolsAndWoodCents,
    List<string> JobsCompleted,
    List<string> JobsOpen);

internal sealed record WayReport(
    int Id,
    string Name,
    WayKind Kind,
    bool? Planned,
    TrailRating? Rating,
    int? Difficulty,
    List<string>? Features,
    long LengthMeters,
    long DropMeters,
    string? MaxDropGradient,
    string? MaxClimbGradient,
    long Runs,
    double? AverageRunMinutes,
    int? AverageFun,
    string? Status,
    List<string>? FeatureConditions,
    int? WorstFeaturePermille,
    long? ClosedMinutes,
    int? Repairs,
    long? HeldUpMinutes,
    int? Crashes,
    int? SeriousCrashes,
    int? Collisions,
    double? CrashesPer1000Runs);

internal sealed record SafetyReport(
    long Crashes,
    long Serious,
    long Collisions,
    long Evacuations,
    int? AverageRescueMinutes,
    long InsuranceCents,
    long PremiumTomorrowCents,
    List<string> Crossings,
    List<string> Log);

internal sealed record GroupReport(string Name, string? Stars, long Reviews, int InWindow);

internal sealed record ReputationReport(
    bool Enabled,
    string? Stars,
    long Reviews,
    List<GroupReport> Groups,
    int DemandPermille,
    int VisibilityPermille,
    int RatingFactorPermille,
    int InfluencerPermille,
    int Xp,
    int Level,
    int? NextLevelXp,
    ParkProgress.XpBreakdown XpParts,
    List<string> Posts,
    List<string> Log);

internal sealed record RunnerOutput(
    int Days,
    KpiReport Kpis,
    ReputationReport? Reputation,
    SafetyReport? Safety,
    LandReport? Land,
    List<WayReport> Ways,
    List<LiftReport> Lifts,
    CrewReport? Crew,
    Dictionary<string, int> GuestsLeft,
    Dictionary<string, int> TurnedAway,
    List<RejectedCommand> RejectedCommands,
    List<DayReport>? Daily,
    List<HourReport>? Hourly);

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
    IReadOnlyList<string> CommandsPaths,
    bool IncludeDaily,
    string? SavePath,
    bool IncludeHourly = false,
    bool AutoRepair = false)
{
    public const string Usage =
        "usage: Bikepark.SimRunner --scenario <path> [--days N=30] [--seed S] [--commands <path>]... [--daily] [--hourly] [--auto-repair] [--save <path>]";

    public static RunnerOptions Parse(string[] args)
    {
        string? scenario = null;
        int days = 30;
        ulong? seed = null;
        var commands = new List<string>();
        bool daily = false;
        bool hourly = false;
        bool autoRepair = false;
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
                case "--commands": commands.Add(Next()); break;
                case "--daily": daily = true; break;
                case "--hourly": hourly = true; break;
                case "--auto-repair": autoRepair = true; break;
                case "--save": save = Next(); break;
                case "-h" or "--help": throw new ArgumentException("help requested");
                default: throw new ArgumentException($"unknown argument '{args[i]}'");
            }
        }

        if (scenario is null)
            throw new ArgumentException("--scenario is required");
        return new RunnerOptions(scenario, days, seed, commands, daily, save, hourly, autoRepair);
    }
}
