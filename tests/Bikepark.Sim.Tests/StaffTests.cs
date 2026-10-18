using System.Text.Json.Nodes;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.Staff;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Tests;

/// <summary>Park staff roles (Starter Valley), crew grades and the applicant pool.</summary>
public class StaffTests
{
    private static Simulation Career(Action<ScenarioDefinition>? tweak = null)
    {
        var scenario = TestWorlds.CareerScenario();
        tweak?.Invoke(scenario);
        var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
        sim.Step();
        sim.Events.Clear();
        return sim;
    }

    private static StaffRole Role(WorldState state, StaffEffect effect) => state.StaffRoles.First(r => r.Effect == effect);

    private static void SetCount(WorldState state, StaffEffect effect, int count)
    {
        var role = Role(state, effect);
        if (state.Staff.FirstOrDefault(p => p.RoleId == role.Id) is { } position) position.Count = count;
        else state.Staff.Add(new StaffPosition { RoleId = role.Id, Count = count });
    }

    [Fact]
    public void Career_StartsWithItsStaff_AndPaysTheirWages()
    {
        var sim = Career();
        var state = sim.State;
        Assert.True(state.StaffRules.Enabled);
        Assert.Equal(5, state.StaffRoles.Count);
        Assert.Equal(1, StaffMath.Count(state, "manager"));
        long wages = StaffMath.WagesPerDay(state);
        Assert.Equal(state.StaffRoles.Sum(r => StaffMath.Count(state, r.Id) * r.WagePerDayCents), wages);
        Assert.NotEqual(state.Rules.DailyUpkeepCents, wages);

        sim.RunTicks(GameTime.MinutesPerDay - sim.State.Tick);
        Assert.Equal(wages, Math.Abs(Ledger.Get(state.DayHistory[^1].ByCategory, MoneyCategory.ParkStaff)));
    }

    [Fact]
    public void WithStaffOff_TheFlatBaseStaffIsPaid_AndEverythingIsCovered()
    {
        var sim = new Simulation(TestWorlds.Create());
        var state = sim.State;
        Assert.False(state.StaffRules.Enabled);
        Assert.Equal(state.Rules.DailyUpkeepCents, StaffMath.DailyCostCents(state));
        foreach (var effect in Enum.GetValues<StaffEffect>())
            Assert.Equal(1000, StaffMath.CoveragePermille(state, effect));
        Assert.Equal(1000, StaffMath.MarketingPermille(state));
        Assert.Equal(1000, StaffMath.RescueWaitPermille(state));
        Assert.Equal(0, StaffMath.TicketTurnAwayPermille(state));
    }

    [Fact]
    public void SetStaff_ValidatesRoleCountAndLevel()
    {
        var sim = Career();
        sim.Commands.Enqueue(new SetStaffCommand("cleaning", 5));
        sim.Commands.Enqueue(new SetStaffCommand("cleaning", 99)); // above maxCount
        sim.Commands.Enqueue(new SetStaffCommand("nobody", 1)); // unknown role
        sim.Commands.Enqueue(new SetStaffCommand("marketing", 1)); // needs level 2
        sim.Commands.Enqueue(new SetStaffCommand("first_aid", 0)); // letting people go is always fine
        sim.Step();
        Assert.Equal(3, sim.Events.Pending.OfType<CommandRejected>().Count());
        Assert.Equal(5, StaffMath.Count(sim.State, "cleaning"));
        Assert.Equal(0, StaffMath.Count(sim.State, "first_aid"));
        Assert.Equal(0, StaffMath.Count(sim.State, "marketing"));
    }

    [Fact]
    public void Coverage_FollowsTheNeed_AndManagersSuperviseEveryoneElse()
    {
        var sim = Career();
        var state = sim.State;
        var tickets = Role(state, StaffEffect.Tickets);
        state.Stats.VisitorsToday = tickets.CoversPer * 4;
        SetCount(state, StaffEffect.Tickets, 2);
        SetCount(state, StaffEffect.Supervision, 5);
        Assert.Equal(500, StaffMath.CoveragePermille(state, StaffEffect.Tickets));

        // Without managers (and more people than the free head count), everyone else works at the floor.
        SetCount(state, StaffEffect.Supervision, 0);
        for (int i = 0; i < 10; i++) sim.Commands.Enqueue(new HireCrewCommand());
        sim.Step();
        Assert.Equal(state.StaffRules.SupervisionFloorPermille, StaffMath.SupervisionPermille(state));
        Assert.Equal(500 * state.StaffRules.SupervisionFloorPermille / 1000, StaffMath.CoveragePermille(state, StaffEffect.Tickets));
        Assert.Equal(state.StaffRules.SupervisionFloorPermille, StaffMath.CrewSupervisionPermille(state));
        var member = state.Crew[0];
        Assert.Equal(state.StaffRules.SupervisionFloorPermille, WorkCosts.WorkerSpeedPermille(state, member, null) * 1000 / member.SpeedPermille);
    }

    [Fact]
    public void NoTicketOffice_TurnsArrivalsAway()
    {
        var sim = Career();
        SetCount(sim.State, StaffEffect.Tickets, 0);
        sim.State.Stats.VisitorsToday = 1; // there is a need
        Assert.Equal(sim.State.StaffRules.TicketTurnAwayPermille, StaffMath.TicketTurnAwayPermille(sim.State));
        sim.RunDays(1);
        Assert.True(sim.State.Stats.TotalTurnedAwayTicketQueue > 0);
    }

    [Fact]
    public void FirstAid_ShortensTheRescue_AndCleaningKeepsTheMood()
    {
        var sim = Career();
        var state = sim.State;
        SetCount(state, StaffEffect.FirstAid, 0);
        if (StaffMath.Need(state, StaffEffect.FirstAid) == 0)
            state.Ways.First(w => w.Kind == Trails.WayKind.Trail).Built = true;
        Assert.Equal(2000, StaffMath.RescueWaitPermille(state));
        Assert.Equal(1000 + state.StaffRules.MinorCrashExtraMoodPermille, StaffMath.MinorCrashMoodPermille(state));
        SetCount(state, StaffEffect.FirstAid, 10);
        SetCount(state, StaffEffect.Supervision, 5); // enough managers for everyone
        Assert.Equal(1000, StaffMath.RescueWaitPermille(state));

        state.Stats.VisitorsToday = 10;
        SetCount(state, StaffEffect.Cleaning, 0);
        int lost = 0;
        for (int minute = 0; minute < 10; minute++) lost += StaffMath.CleaningMoodLoss(state, 600 + minute);
        Assert.Equal(10 * state.StaffRules.CleaningMoodPerMinute, lost);
        SetCount(state, StaffEffect.Cleaning, 5);
        Assert.Equal(0, StaffMath.CleaningMoodLoss(state, 600));
    }

    [Fact]
    public void Marketing_BringsDemand_WithDiminishingReturns()
    {
        var sim = Career();
        var state = sim.State;
        var role = Role(state, StaffEffect.Marketing);
        Assert.Equal(1000, StaffMath.MarketingPermille(state));
        SetCount(state, StaffEffect.Marketing, 1);
        int one = StaffMath.MarketingPermille(state);
        Assert.Equal(1000 + role.CoversPer * StaffMath.SupervisionPermille(state) / 1000, one);
        SetCount(state, StaffEffect.Marketing, 2);
        int two = StaffMath.MarketingPermille(state);
        Assert.True(two > one && two - 1000 < 2 * (one - 1000));
    }

    [Fact]
    public void Applicants_AreRolledEachOpening_Deterministically_AndHiredWithTheirGrade()
    {
        var a = Career();
        var b = Career();
        var rules = a.State.CrewRules;
        Assert.Equal(rules.ApplicantCount, a.State.Applicants.Count);
        Assert.Equal(a.State.Applicants.Select(x => x.Name), b.State.Applicants.Select(x => x.Name));
        Assert.All(a.State.Applicants, x => Assert.Contains(rules.Grades, g => g.Id == x.GradeId));

        var applicant = a.State.Applicants[0];
        a.Commands.Enqueue(new HireCrewCommand(ApplicantId: applicant.Id));
        a.Commands.Enqueue(new HireCrewCommand(ApplicantId: applicant.Id)); // rejected: hired already
        a.Commands.Enqueue(new HireCrewCommand()); // a standard worker
        a.Step();
        Assert.Single(a.Events.Pending.OfType<CommandRejected>());
        var hired = a.State.Crew.Single(m => m.Name == applicant.Name);
        Assert.Equal(applicant.SpeedPermille, hired.SpeedPermille);
        Assert.Equal(applicant.WagePerDayCents, hired.WagePerDayCents);
        Assert.Equal(applicant.GradeId, hired.GradeId);
        Assert.Equal(rules.ApplicantCount - 1, a.State.Applicants.Count);
        var standard = a.State.Crew[^1];
        Assert.Equal(1000, standard.SpeedPermille);
        Assert.Equal(rules.WagePerDayCents, standard.WagePerDayCents);

        // The tick-0 list is day 0's; the next opening brings new people.
        var before = a.State.Applicants.Select(x => x.Id).ToList();
        a.RunTicks(GameTime.MinutesPerDay + a.State.Rules.OpenMinute + 1 - a.State.Tick);
        Assert.Equal(rules.ApplicantCount, a.State.Applicants.Count);
        Assert.DoesNotContain(a.State.Applicants, x => before.Contains(x.Id));
    }

    [Fact]
    public void FasterWorkers_BuildFaster_AndEachIsPaidTheirOwnWage()
    {
        var sim = new Simulation(TestWorlds.Create());
        foreach (var c in TestWorlds.DemoNetwork()) sim.Commands.Enqueue(c.Command, 0);
        sim.Step();
        sim.Commands.Enqueue(new HireCrewCommand());
        sim.Commands.Enqueue(new HireCrewCommand());
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(3, "double", 30_000));
        sim.Step();
        sim.State.Crew[0].SpeedPermille = 1500;
        sim.State.Crew[0].WagePerDayCents = 30_000;
        sim.State.Jobs[0].WorkMinutes = 100_000;
        sim.RunTicks(sim.State.Rules.OpenMinute + 10 - sim.State.Tick);
        Assert.Equal(10 * (1500 + 1000), sim.State.Jobs[0].Progress);
        Assert.All(sim.State.Crew, m => Assert.Equal(10, m.MinutesWorked));

        sim.RunTicks(GameTime.MinutesPerDay - sim.State.Tick);
        Assert.Equal(30_000 + sim.State.CrewRules.WagePerDayCents, Math.Abs(Ledger.Get(sim.State.DayHistory[^1].ByCategory, MoneyCategory.Wages)));
    }

    [Fact]
    public void ToolAssignments_AreWhatTheCrewWorksWith()
    {
        var sim = new Simulation(TestWorlds.Create());
        foreach (var c in TestWorlds.DemoNetwork()) sim.Commands.Enqueue(c.Command, 0);
        sim.Step();
        for (int i = 0; i < 2; i++) sim.Commands.Enqueue(new HireCrewCommand());
        sim.Commands.Enqueue(new BuyToolCommand("shovel_set"));
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(3, "double", 30_000));
        sim.Step();
        sim.State.Jobs[0].WorkMinutes = 100_000;
        sim.RunTicks(sim.State.Rules.OpenMinute + 1 - sim.State.Tick);

        var uses = WorkCosts.ToolAssignments(sim.State);
        Assert.Equal(2, uses.Count);
        Assert.Equal(sim.State.Crew[0], uses[0].Member);
        Assert.Equal(sim.State.Tools[0], uses[0].Tool);
        Assert.Null(uses[1].Tool);
        long used = sim.State.Tools[0].MinutesUsed;
        sim.Step();
        Assert.Equal(used + 1, sim.State.Tools[0].MinutesUsed);
    }

    [Fact]
    public void Staff_SurvivesSaveAndLoad()
    {
        var sim = Career();
        sim.Commands.Enqueue(new SetStaffCommand("cleaning", 4));
        sim.Step();
        var loaded = SaveGame.Deserialize(SaveGame.Serialize(sim.State));
        Assert.Equal(4, StaffMath.Count(loaded, "cleaning"));
        Assert.Equal(sim.State.Applicants.Count, loaded.Applicants.Count);
        Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(loaded));
    }

    [Fact]
    public void Load_MigratesVersion10_WorkersBecomeStandardWorkers_AndTheCrewLimitsGo()
    {
        var sim = new Simulation(TestWorlds.Create());
        foreach (var c in TestWorlds.DemoNetwork()) sim.Commands.Enqueue(c.Command, 0);
        sim.Commands.Enqueue(new HireCrewCommand(), 1);
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(3, "double", 30_000), 1);
        sim.RunTicks(2);
        var root = JsonNode.Parse(SaveGame.Serialize(sim.State))!.AsObject();
        root["version"] = 10;
        var world = root["world"]!.AsObject();
        var crew = world["crewRules"]!.AsObject();
        foreach (string key in new[] { "sectionMeters", "workersPerSection", "grades", "applicantCount", "applicantSpreadPermille" }) crew.Remove(key);
        crew["wagePerDayCents"] = 20_000;
        crew["maxCrew"] = 12;
        crew["maxWorkersPerJob"] = 3;
        foreach (var member in world["crew"]!.AsArray())
            foreach (string key in new[] { "gradeId", "speedPermille", "wagePerDayCents", "hiredTick", "minutesWorked" }) member!.AsObject().Remove(key);
        world["jobs"]!.AsArray()[0]!.AsObject()["workers"] = 3;
        foreach (string key in new[] { "applicants", "applicantsDay", "staffRoles", "staffRules", "staff" }) world.Remove(key);
        world["stats"]!.AsObject().Remove("totalTurnedAwayTicketQueue");

        var loaded = SaveGame.Deserialize(root.ToJsonString());
        var worker = Assert.Single(loaded.Crew);
        Assert.Equal(1000, worker.SpeedPermille);
        Assert.Equal(20_000, worker.WagePerDayCents);
        Assert.Equal(2, loaded.Jobs[0].Workers);
        Assert.False(loaded.StaffRules.Enabled);
        Assert.Equal(loaded.Rules.DailyUpkeepCents, StaffMath.DailyCostCents(loaded));
    }
}
