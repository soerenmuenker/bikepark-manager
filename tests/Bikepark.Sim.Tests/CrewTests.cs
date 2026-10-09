using System.Text.Json;
using System.Text.Json.Nodes;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

public class CrewTests
{
    // Test world (TestWorlds.Scenario) + demo_network.json: path 1, Blue Line 2, Red Rocket 3, built at once.
    private const int BlueLine = 2;
    private const int RedRocket = 3;

    // Starter Valley + demo_lift_network.json: planned trails get a job id after their own, so look Red Rocket up by name.
    private static int ValleyRedRocket(Simulation sim) => sim.State.Ways.Single(w => w.Name == "Red Rocket").Id;

    private static readonly PointCm Forest = new(72_000, 52_000);

    // ---------------------------------------------------------------- content

    [Fact]
    public void ShippedContent_LoadsIntoStarterValley_AndValidates()
    {
        var state = ScenarioLoader.CreateWorld(TestWorlds.LiftScenario());
        Assert.Equal(["shovel_set", "mini_excavator", "chainsaw", "cordless_kit"], state.ToolTypes.Select(t => t.Id));
        Assert.All(state.ToolTypes, t => Assert.Empty(t.Validate()));
        Assert.Empty(state.CrewRules.Validate());
        Assert.Equal(20, state.WoodStock);
        Assert.All(state.TrailFeatureTypes, t => Assert.True(t.WorkMinutes > 0));
        Assert.Equal(70, state.TrailFeatureTypes.Sum(t => t.Wood)); // wall-ride 30, kicker 15, drop 25

        var sim = new Simulation(state);
        sim.Step();
        Assert.Equal(2, sim.State.Crew.Count); // hired by the scenario
    }

    [Fact]
    public void Content_RejectsUnknownFields_BadTools_AndBadRules()
    {
        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<List<ToolType>>("""[{ "id": "saw", "name": "Saw", "workType": "felling", "weight": 3 }]""", SimJson.Indented));
        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<List<ToolType>>("""[{ "id": "saw", "name": "Saw", "workType": "welding" }]""", SimJson.Indented));

        var badTool = TestWorlds.Scenario();
        badTool.ToolTypes[0].PriceCents = -1;
        Assert.Contains("priceCents", Assert.Throws<InvalidDataException>(() => ScenarioLoader.CreateWorld(badTool)).Message);

        var duplicate = TestWorlds.Scenario();
        duplicate.ToolTypes.Add(TestWorlds.ToolCatalog()[0]);
        Assert.Throws<InvalidDataException>(() => ScenarioLoader.CreateWorld(duplicate));

        var badRules = TestWorlds.Scenario();
        badRules.CrewRules.WorkEndMinute = badRules.CrewRules.WorkStartMinute;
        Assert.Contains("work hours", Assert.Throws<InvalidDataException>(() => ScenarioLoader.CreateWorld(badRules)).Message);

        var badFeature = TestWorlds.Scenario();
        badFeature.TrailFeatureTypes[0].WorkMinutes = 0;
        Assert.Contains("workMinutes", Assert.Throws<InvalidDataException>(() => ScenarioLoader.CreateWorld(badFeature)).Message);
    }

    // ---------------------------------------------------------------- planning

    [Fact]
    public void PlannedTrail_IsNotRidden_UntilTheCrewHasBuiltIt()
    {
        var sim = ValleyWorld(instantTrails: false);
        var trail = sim.State.Ways.Single(w => w.Id == ValleyRedRocket(sim));
        Assert.False(trail.Built);
        Assert.Equal(2, sim.State.Jobs.Count(j => j.Kind == JobKind.BuildWay));
        Assert.Empty(sim.Network.Trails);
        Assert.True(sim.Network.TryGetGeometry(ValleyRedRocket(sim), out _)); // still drawn, and features can go on it

        // Only Red Rocket gets built: a full crew with the best tools, nothing else in the queue.
        foreach (var job in sim.State.Jobs.Where(j => j.WayId != ValleyRedRocket(sim)).ToList())
            sim.Commands.Enqueue(new CancelJobCommand(job.Id));
        sim.Commands.Enqueue(new HireCrewCommand());
        sim.Commands.Enqueue(new BuyToolCommand("mini_excavator"));
        sim.Commands.Enqueue(new BuyToolCommand("chainsaw"));
        sim.RunTicks(GameTime.TicksForDays(1) - 1);

        Assert.Equal(0, sim.State.Ways.Single(w => w.Id == ValleyRedRocket(sim)).Stats.Runs);
        Assert.All(sim.State.Guests, g => Assert.Equal(0, g.TrailId));
        sim.RunTicks(GameTime.TicksForDays(2));
        Assert.True(trail.Built);
        Assert.Empty(sim.State.Jobs);
        Assert.Contains(sim.Network.Trails, t => t.Id == ValleyRedRocket(sim));
        Assert.True(trail.Stats.Runs > 0);
    }

    [Fact]
    public void PlannedFeature_HasNoEffect_UntilBuilt()
    {
        var sim = TestWorld();
        var before = sim.Network.Geometry(RedRocket);
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(RedRocket, "double", 30_000));
        sim.Step();

        var job = Assert.Single(sim.State.Jobs);
        Assert.Equal(JobKind.BuildFeature, job.Kind);
        Assert.Equal(400, job.WorkMinutes);
        Assert.Equal(WorkType.Digging, job.MainWorkType);
        var feature = Assert.Single(sim.Network.FeaturesOn(RedRocket));
        Assert.False(feature.Feature.Built);
        Assert.Equal(before.DifficultyScore, sim.Network.Geometry(RedRocket).DifficultyScore);

        for (int i = 0; i < 3; i++)
            sim.Commands.Enqueue(new HireCrewCommand());
        sim.RunTicks(420 + 320 - sim.State.Tick); // three workers: 320 minutes
        Assert.Empty(sim.State.Jobs);
        Assert.True(feature.Feature.Built);
        Assert.Equal(720, sim.Network.Geometry(RedRocket).DifficultyScore);
    }

    [Fact]
    public void InstantAndScenarioWays_AreBuiltAtOnce()
    {
        var sim = ValleyWorld(instantTrails: true);
        Assert.All(sim.State.Ways, w => Assert.True(w.Built));
        Assert.Empty(sim.State.Jobs);
    }

    // ---------------------------------------------------------------- jobs

    [Fact]
    public void Work_HappensInWorkHours_AndFinishesOnTime()
    {
        var sim = TestWorld();
        sim.Commands.Enqueue(new HireCrewCommand());
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(BlueLine, "berm", 18_000)); // 200 crew-minutes
        sim.RunTicks(420 - sim.State.Tick); // until 07:00
        var job = Assert.Single(sim.State.Jobs);
        Assert.Equal(0, job.Progress);
        Assert.Equal(0, sim.State.Crew[0].JobId);

        sim.RunTicks(60);
        Assert.Equal(sim.State.Crew[0].JobId, job.Id);
        Assert.Equal(60_000, job.Progress);
        Assert.Equal(300, WorkCosts.ProgressPermille(sim.State.CrewRules, job));

        sim.RunTicks(619 - sim.State.Tick);
        Assert.Single(sim.State.Jobs);
        sim.Step(); // tick 619: the 200th minute
        Assert.Empty(sim.State.Jobs);
        Assert.True(sim.State.Ways.Single(w => w.Id == BlueLine).Features.Single().Built);
        Assert.Equal(0, sim.State.Crew[0].JobId);
        Assert.Contains(sim.Events.Pending, e => e is JobCompleted { Title: "Berm on Blue Line at 180 m" });
    }

    [Fact]
    public void Work_StopsAtTheEndOfTheDay_AndAToolSpeedsItUp()
    {
        var sim = TestWorld();
        sim.Commands.Enqueue(new HireCrewCommand());
        sim.Commands.Enqueue(new BuyToolCommand("shovel_set"));
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(RedRocket, "double", 30_000));
        sim.Step();
        sim.State.Jobs[0].WorkMinutes = 960; // longer than a day, so the shift end is what stops it
        sim.RunTicks(480 - sim.State.Tick);
        Assert.Equal(60 * 1250, sim.State.Jobs[0].Progress);

        sim.RunTicks(1100 - sim.State.Tick); // past 17:00: 600 minutes worked
        Assert.Equal(600 * 1250, sim.State.Jobs[0].Progress);
        Assert.All(sim.State.Crew, m => Assert.Equal(0, m.JobId));
    }

    [Fact]
    public void Crew_FillsJobsInQueueOrder_UpToTheLimit()
    {
        var sim = TestWorld();
        for (int i = 0; i < 4; i++)
            sim.Commands.Enqueue(new HireCrewCommand());
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(BlueLine, "berm", 18_000));
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(RedRocket, "double", 30_000));
        sim.RunTicks(421);

        int first = sim.State.Jobs[0].Id, second = sim.State.Jobs[1].Id;
        Assert.Equal(3, sim.State.Crew.Count(m => m.JobId == first));
        Assert.Equal(1, sim.State.Crew.Count(m => m.JobId == second));
        Assert.Equal(["Worker 1", "Worker 2", "Worker 3", "Worker 4"], sim.State.Crew.Select(m => m.Name));

        // A free worker takes the job at the front of the queue.
        sim.Commands.Enqueue(new PrioritizeJobCommand(second));
        sim.Commands.Enqueue(new HireCrewCommand());
        sim.Step();
        Assert.Equal(second, sim.State.Jobs[0].Id);
        Assert.Equal(2, sim.State.Crew.Count(m => m.JobId == second));
    }

    [Fact]
    public void WoodFeature_WaitsForWood_AndTakesItWhenWorkStarts()
    {
        var sim = TestWorld();
        sim.Commands.Enqueue(new HireCrewCommand());
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(RedRocket, "drop", 15_000)); // 25 wood
        sim.RunTicks(430);
        var job = Assert.Single(sim.State.Jobs);
        Assert.False(JobSystem.IsWorkable(sim.State, job));
        Assert.False(job.WoodTaken);
        Assert.Equal(0, sim.State.Crew[0].JobId);

        sim.Commands.Enqueue(new BuyWoodCommand(25));
        sim.Step();
        Assert.True(job.WoodTaken);
        Assert.Equal(0, sim.State.WoodStock);
        Assert.Equal(25, sim.State.CrewStats.WoodUsed);
        Assert.Equal(25 * sim.State.CrewRules.WoodPriceCents, sim.State.Finance.TotalWoodCents);
        Assert.Equal(job.Id, sim.State.Crew[0].JobId);
        Assert.Equal(WorkType.Carpentry, job.CurrentWorkType);

        // Removing the planned feature gives the wood back.
        int featureId = sim.State.Ways.Single(w => w.Id == RedRocket).Features.Single().Id;
        sim.Commands.Enqueue(new RemoveTrailFeatureCommand(RedRocket, featureId));
        sim.Step();
        Assert.Empty(sim.State.Jobs);
        Assert.Equal(25, sim.State.WoodStock);
        Assert.Equal(0, sim.State.Crew[0].JobId);
    }

    [Fact]
    public void Feature_WaitsForItsTrail()
    {
        var sim = ValleyWorld(instantTrails: false);
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(ValleyRedRocket(sim), "double", 20_000));
        sim.Commands.Enqueue(new PrioritizeJobCommand(0)); // rejected: no such job
        sim.Step();
        var featureJob = sim.State.Jobs.Single(j => j.Kind == JobKind.BuildFeature);
        Assert.False(JobSystem.IsWorkable(sim.State, featureJob));
        sim.Commands.Enqueue(new PrioritizeJobCommand(featureJob.Id));
        sim.RunTicks(500);
        Assert.Equal(featureJob.Id, sim.State.Jobs[0].Id);
        Assert.DoesNotContain(sim.State.Crew, m => m.JobId == featureJob.Id); // they dig the trails instead
        Assert.Equal("No such job.", sim.Events.Pending.OfType<CommandRejected>().Single().Reason);
    }

    // ---------------------------------------------------------------- trees and wood

    [Fact]
    public void Felling_CutsTreesOneByOne_AndEachGivesWood()
    {
        var sim = TestWorld();
        sim.Commands.Enqueue(new HireCrewCommand());
        sim.Commands.Enqueue(new FellTreesCommand(Forest, 2_000));
        sim.Step();
        var job = Assert.Single(sim.State.Jobs);
        int trees = job.Trees.Count;
        Assert.True(trees > 10);
        Assert.Equal(WorkType.Felling, job.CurrentWorkType);
        Assert.Equal(trees * 20L, WorkCosts.TotalMinutes(sim.State.CrewRules, job));

        sim.RunTicks(420 + 20 - sim.State.Tick);
        Assert.Equal(1, job.TreesFelled);
        Assert.Equal(2, sim.State.WoodStock);
        var gone = Bikepark.Sim.Crew.Forest.GoneFilter(sim.Network, sim.State);
        Assert.True(gone(job.Trees[0].X, job.Trees[0].Z));
        Assert.False(gone(job.Trees[1].X, job.Trees[1].Z));

        // The rest of the area is claimed: a second area on the same spot finds nothing.
        sim.Commands.Enqueue(new FellTreesCommand(Forest, 2_000));
        sim.Step();
        Assert.Equal("There are no standing trees here (or they are already marked).", sim.Events.Pending.OfType<CommandRejected>().Last().Reason);

        sim.RunDays(2);
        Assert.Empty(sim.State.Jobs);
        Assert.Equal(trees * 2, sim.State.WoodStock);
        Assert.Equal(trees, sim.State.FelledTrees.Count);
        Assert.Equal(trees, sim.State.CrewStats.TreesFelled);
    }

    [Fact]
    public void Felling_UsesTheWholeCrew_OneWorkerPerTree()
    {
        var sim = TestWorld();
        for (int i = 0; i < 6; i++)
            sim.Commands.Enqueue(new HireCrewCommand());
        sim.Commands.Enqueue(new FellTreesCommand(Forest, 2_000));
        sim.Step();
        var job = Assert.Single(sim.State.Jobs);
        Assert.True(job.Trees.Count > 12);

        sim.RunTicks(sim.State.CrewRules.WorkStartMinute + 1 - sim.State.Tick);
        Assert.Equal(6, sim.State.Crew.Count(m => m.JobId == job.Id)); // more than maxWorkersPerJob (3)
        Assert.Equal(WorkType.Felling, job.CurrentWorkType);
    }

    [Fact]
    public void WorkersBeyondTheLimit_LeaveAWayJobOnceItsTreesAreFelled()
    {
        var sim = ValleyWorld(instantTrails: false);
        var job = Jobs.ForWay(sim.State, ValleyRedRocket(sim))!;
        foreach (var other in sim.State.Jobs.Where(j => j != job).ToList())
            sim.Commands.Enqueue(new CancelJobCommand(other.Id));
        for (int i = 0; i < 6; i++)
            sim.Commands.Enqueue(new HireCrewCommand());
        sim.RunTicks(sim.State.CrewRules.WorkStartMinute + 2 - sim.State.Tick);
        Assert.True(job.IsFelling);
        Assert.Equal(sim.State.Crew.Count, sim.State.Crew.Count(m => m.JobId == job.Id));

        while (job.IsFelling)
            sim.Step();
        sim.Step();
        Assert.Equal(WorkType.Digging, job.CurrentWorkType);
        Assert.Equal(sim.State.CrewRules.MaxWorkersPerJob, sim.State.Crew.Count(m => m.JobId == job.Id));
    }

    [Fact]
    public void ClearingPlanner_ChecksSize_AndCountsOnlyStandingTrees()
    {
        var sim = TestWorld();
        string? Error(PointCm c, int r) => ClearingPlanner.Plan(sim.Terrain, sim.Network, sim.State, c, r).Issues.FirstOrDefault()?.Code;
        Assert.Null(Error(Forest, 2_000));
        Assert.Equal("tooSmall", Error(Forest, 100));
        Assert.Equal("tooLarge", Error(Forest, 10_000));
        Assert.Equal("outsideMap", Error(new PointCm(-500, 100), 2_000));

        var plan = ClearingPlanner.Plan(sim.Terrain, sim.Network, sim.State, Forest, 2_000);
        Assert.Equal(plan.Trees.Count * 2, plan.Estimate.WoodGained);
        Assert.All(plan.Trees, t => Assert.False(sim.Network.IsInCorridor(t.X, t.Z)));
        long d0 = Dist2(plan.Trees[0]), dLast = Dist2(plan.Trees[^1]);
        Assert.True(d0 <= dLast); // nearest to the centre first

        static long Dist2(PointCm p) => (long)(p.X - Forest.X) * (p.X - Forest.X) + (long)(p.Z - Forest.Z) * (p.Z - Forest.Z);
    }

    [Fact]
    public void PlannedTrailThroughForest_FellsItsCorridorFirst_ThenDigs()
    {
        var sim = ValleyWorld(instantTrails: false);
        var job = Jobs.ForWay(sim.State, ValleyRedRocket(sim))!;
        int trees = job.Trees.Count;
        Assert.True(trees > 10);
        Assert.Equal(WorkType.Felling, job.CurrentWorkType);
        var estimate = WorkCosts.Way(sim.State.CrewRules, sim.State.TrailRules, WayKind.Trail, sim.Network.Geometry(ValleyRedRocket(sim)), trees);
        Assert.Equal(estimate.TotalMinutes, WorkCosts.TotalMinutes(sim.State.CrewRules, job));

        foreach (var other in sim.State.Jobs.Where(j => j != job).ToList())
            sim.Commands.Enqueue(new CancelJobCommand(other.Id));
        sim.Commands.Enqueue(new HireCrewCommand());
        int wood = sim.State.WoodStock;
        sim.RunTicks(sim.State.CrewRules.WorkStartMinute + trees * 20 / 3 + 5 - sim.State.Tick); // three workers fell
        Assert.Equal(trees, job.TreesFelled);
        Assert.Equal(WorkType.Digging, job.CurrentWorkType);
        Assert.Equal(wood + trees * 2, sim.State.WoodStock);
        Assert.False(sim.Network.IsInCorridor(job.Trees[0].X, job.Trees[0].Z)); // not built yet
        Assert.Contains(job.Trees[0], sim.State.FelledTrees);

        sim.RunDays(3);
        Assert.True(sim.State.Ways.Single(w => w.Id == ValleyRedRocket(sim)).Built);
        Assert.True(sim.Network.IsInCorridor(job.Trees[0].X, job.Trees[0].Z));
        Assert.DoesNotContain(job.Trees[0], sim.State.FelledTrees); // the corridor covers it now
    }

    [Fact]
    public void JobWithoutTrees_StartsWithTheMainWork()
    {
        var job = new Job { Kind = JobKind.BuildWay, WorkMinutes = 100, MainWorkType = WorkType.Digging };
        Assert.False(job.IsFelling);
        Assert.Equal(WorkType.Digging, job.CurrentWorkType);
    }

    [Fact]
    public void Purchases_CostMoney_AndAreRejectedWithoutIt()
    {
        var sim = TestWorld();
        sim.Commands.Enqueue(new BuyToolCommand("mini_excavator")); // 18,000 € > 10,000 €
        sim.Commands.Enqueue(new BuyToolCommand("shovel_set"));
        sim.Commands.Enqueue(new BuyToolCommand("drill")); // unknown
        sim.Commands.Enqueue(new BuyWoodCommand(1000)); // 25,000 €
        sim.Commands.Enqueue(new BuyWoodCommand(0));
        sim.Step();
        var reasons = sim.Events.Pending.OfType<CommandRejected>().Select(r => r.Reason).ToList();
        Assert.Equal(["Not enough money.", "Unknown tool 'drill'.", "Not enough money.", "Buy between 1 and 1000 wood."], reasons);
        Assert.Equal(["shovel_set"], sim.State.OwnedToolIds);
        Assert.Equal(1_000_000 - 150_000, sim.State.Finance.MoneyCents);
        Assert.Equal(1250, WorkCosts.SpeedPermille(sim.State, WorkType.Digging));
        Assert.Equal(1000, WorkCosts.SpeedPermille(sim.State, WorkType.Carpentry));
    }

    [Fact]
    public void CrewIsLimited_AndDismissedWorkersLeaveTheirJob()
    {
        var sim = TestWorld();
        for (int i = 0; i < sim.State.CrewRules.MaxCrew + 1; i++)
            sim.Commands.Enqueue(new HireCrewCommand());
        sim.Step();
        Assert.Equal(sim.State.CrewRules.MaxCrew, sim.State.Crew.Count);
        Assert.Single(sim.Events.Pending.OfType<CommandRejected>());

        sim.Commands.Enqueue(new DismissCrewCommand(sim.State.Crew[0].Id));
        sim.Commands.Enqueue(new DismissCrewCommand(-1));
        sim.Commands.Enqueue(new HireCrewCommand());
        sim.Step();
        Assert.Equal("Worker 1", sim.State.Crew[^1].Name); // the free name is reused
    }

    // ---------------------------------------------------------------- cancel

    [Fact]
    public void DeletingAPlannedWay_CancelsItsJob_AndThoseOfItsFeatures()
    {
        var sim = ValleyWorld(instantTrails: false);
        sim.Commands.Enqueue(new BuyWoodCommand(30));
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(ValleyRedRocket(sim), "wall_ride", 5_000));
        sim.Step();
        Assert.Equal(3, sim.State.Jobs.Count);
        sim.State.Jobs.Single(j => j.Kind == JobKind.BuildFeature).WoodTaken = true; // as if work had started
        sim.State.WoodStock -= 30;

        sim.Commands.Enqueue(new DeleteWayCommand(ValleyRedRocket(sim)));
        sim.Step();
        Assert.Single(sim.State.Jobs); // Flow Country's
        Assert.Equal(50, sim.State.WoodStock); // 20 + 30, the wall-ride's wood is back
        Assert.Equal(2, sim.Events.Pending.OfType<JobCancelled>().Count());
    }

    [Fact]
    public void CancellingAJob_RemovesWhatItWouldHaveBuilt()
    {
        var sim = TestWorld();
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(RedRocket, "double", 30_000));
        sim.Step();
        sim.Commands.Enqueue(new CancelJobCommand(sim.State.Jobs[0].Id));
        sim.Step();
        Assert.Empty(sim.State.Jobs);
        Assert.Empty(sim.State.Ways.Single(w => w.Id == RedRocket).Features);

        sim = ValleyWorld(instantTrails: false);
        int flow = sim.State.Jobs[0].WayId;
        sim.Commands.Enqueue(new CancelJobCommand(sim.State.Jobs[0].Id));
        sim.Step();
        Assert.DoesNotContain(sim.State.Ways, w => w.Id == flow);
    }

    [Fact]
    public void CancellingFelling_KeepsTheTreesCutSoFar()
    {
        var sim = TestWorld();
        sim.Commands.Enqueue(new HireCrewCommand());
        sim.Commands.Enqueue(new FellTreesCommand(Forest, 2_000));
        sim.RunTicks(420 + 61);
        int felled = sim.State.Jobs[0].TreesFelled;
        Assert.Equal(3, felled);
        sim.Commands.Enqueue(new CancelJobCommand(sim.State.Jobs[0].Id));
        sim.Step();
        Assert.Empty(sim.State.Jobs);
        Assert.Equal(3, sim.State.FelledTrees.Count);
        Assert.Equal(6, sim.State.WoodStock);
    }

    // ---------------------------------------------------------------- money

    [Fact]
    public void Wages_ArePaidPerWorkerEveryDay()
    {
        var sim = TestWorld();
        sim.Commands.Enqueue(new HireCrewCommand());
        sim.Commands.Enqueue(new HireCrewCommand());
        DayReport? report = null;
        sim.Events.Subscribe<DayEnded>(e => report = e.Report);
        sim.RunDays(1);
        sim.Events.Dispatch();
        Assert.Equal(2 * 18_000, sim.State.Finance.TotalWagesCents);
        Assert.Equal(2 * 18_000, report!.WagesCents);
        Assert.Equal(0, sim.State.Finance.WagesTodayCents);
    }

    // ---------------------------------------------------------------- persistence

    [Fact]
    public void CrewAndJobs_SurviveSaveAndLoad_AndContinueIdentically()
    {
        var sim = TestWorlds.Run(7, 0, TestWorlds.Script());
        sim.RunTicks(1000);
        Assert.NotEmpty(sim.State.Jobs);
        Assert.NotEmpty(sim.State.FelledTrees);
        var loaded = new Simulation(SaveGame.Deserialize(SaveGame.Serialize(sim.State)));
        Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(loaded.State));
        sim.RunTicks(800);
        loaded.RunTicks(800);
        Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(loaded.State));
    }

    [Fact]
    public void Version2SaveWithoutCrew_LoadsWithEverythingBuilt()
    {
        var sim = ValleyWorld(instantTrails: true);
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(ValleyRedRocket(sim), "double", 20_000, Instant: true));
        sim.Step();
        var root = JsonNode.Parse(SaveGame.Serialize(sim.State))!.AsObject();
        var world = root["world"]!.AsObject();
        foreach (string name in new[] { "crewRules", "toolTypes", "ownedToolIds", "crew", "jobs", "woodStock", "felledTrees", "crewStats" })
            Assert.True(world.Remove(name), name);
        foreach (var way in world["ways"]!.AsArray())
        {
            way!.AsObject().Remove("built");
            foreach (var feature in way["features"]!.AsArray())
                feature!.AsObject().Remove("built");
        }

        var loaded = new Simulation(SaveGame.Deserialize(root.ToJsonString()));
        Assert.All(loaded.State.Ways, w => Assert.True(w.Built));
        Assert.True(loaded.State.Ways.Single(w => w.Id == ValleyRedRocket(sim)).Features.Single().Built);
        Assert.Equal(TrailRating.Black, loaded.Network.Geometry(ValleyRedRocket(sim)).Rating);
        Assert.Empty(loaded.State.Crew);
        loaded.RunTicks(60);
    }

    [Fact]
    public void Save_UsesStableCrewDiscriminators()
    {
        var commands = new (ICommand Command, string Type)[]
        {
            (new HireCrewCommand(), "hireCrew"),
            (new DismissCrewCommand(1), "dismissCrew"),
            (new BuyToolCommand("chainsaw"), "buyTool"),
            (new BuyWoodCommand(10), "buyWood"),
            (new FellTreesCommand(Forest, 2_000), "fellTrees"),
            (new PrioritizeJobCommand(1), "prioritizeJob"),
            (new CancelJobCommand(1), "cancelJob"),
        };
        foreach (var (command, type) in commands)
            Assert.Contains($"\"type\":\"{type}\"", JsonSerializer.Serialize(command, SimJson.Compact));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Test world (no crew, no wood, 10,000 €) with the demo network built, after tick 0.</summary>
    private static Simulation TestWorld()
    {
        var sim = new Simulation(TestWorlds.Create());
        foreach (var c in TestWorlds.DemoNetwork())
            sim.Commands.Enqueue(c.Command, 0);
        sim.Step();
        Assert.DoesNotContain(sim.Events.Pending, e => e is CommandRejected);
        Assert.All(sim.State.Ways, w => Assert.True(w.Built));
        sim.Events.Clear();
        return sim;
    }

    /// <summary>Starter Valley with the demo trails, planned (crew jobs) or built at once, after tick 0.</summary>
    private static Simulation ValleyWorld(bool instantTrails)
    {
        var sim = new Simulation(ScenarioLoader.CreateWorld(TestWorlds.LiftScenario()));
        foreach (var c in TestWorlds.DemoLiftNetwork())
            sim.Commands.Enqueue(c.Command is BuildWayCommand build ? build with { Instant = instantTrails } : c.Command, 0);
        sim.Step();
        Assert.DoesNotContain(sim.Events.Pending, e => e is CommandRejected);
        sim.Events.Clear();
        return sim;
    }
}
