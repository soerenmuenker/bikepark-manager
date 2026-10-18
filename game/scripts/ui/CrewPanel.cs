using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Staff;
using Bikepark.Sim.State;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// Crew menu, in three tabs. Workers: every worker with grade, speed, wage and what they are doing, and today's applicants
/// to hire. Park staff: managers, ticket office, first aid, cleaning and marketing – how many, what the park needs, what a
/// shortage does. Jobs: the queue in priority order (free workers take the first job they can work on), with how many
/// workers each may have.
/// </summary>
public partial class CrewPanel : HudPanel
{
    private const float ContentWidth = 700;
    private const int MaxListHeight = 320;

    private Label _workers = null!, _wages = null!, _staffWages = null!, _wood = null!, _jobs = null!, _ahead = null!;
    private Control _staffTile = null!;
    private TabStrip _tabs = null!;
    private VBoxContainer _workersPage = null!, _staffPage = null!, _jobsPage = null!;

    // Workers
    private Label _hours = null!, _applicantsNote = null!;
    private Button _hireStandard = null!;
    private VBoxContainer _crewList = null!;
    private HFlowContainer _applicants = null!;
    private Control _applicantsHeading = null!;
    private string? _crewSignature, _applicantSignature;
    private readonly List<(int CrewId, Label Status, Label Worked)> _crewRows = [];

    // Park staff
    private VBoxContainer _staffList = null!;
    private Label _staffIntro = null!, _staffTotal = null!;
    private string? _staffSignature;
    private readonly List<(StaffRole Role, Label Count, Label Need, ProgressBar Bar, Label Effect, Button Plus)> _staffRows = [];

    // Jobs
    private VBoxContainer _jobList = null!;
    private Button _buy10 = null!, _buy50 = null!;
    private string? _jobSignature;
    private readonly List<(int JobId, Label Title, Label State, ProgressBar Bar, Label Workers)> _jobRows = [];
    private readonly Dictionary<int, PanelContainer> _jobCards = [];

    public override string Title => "Crew & staff";

    protected override void Build()
    {
        var tiles = Row(24);
        tiles.AddChild(UiTheme.StatTile("Workers", out _workers, 24));
        tiles.AddChild(UiTheme.StatTile("Crew wages / day", out _wages));
        _staffTile = UiTheme.StatTile("Staff wages / day", out _staffWages);
        tiles.AddChild(_staffTile);
        tiles.AddChild(UiTheme.StatTile("Wood", out _wood, 24));
        tiles.AddChild(UiTheme.StatTile("Jobs", out _jobs));
        tiles.AddChild(UiTheme.StatTile("Work ahead", out _ahead));
        Body.AddChild(tiles);

        _tabs = new TabStrip();
        _tabs.Add("Workers", "Your crew: who does what, and today's applicants");
        _tabs.Add("Park staff", "Managers, ticket office, first aid, cleaning and marketing");
        _tabs.Add("Jobs", "The job queue, wood and how many workers each job gets");
        _tabs.Selected += ShowTab;
        Body.AddChild(_tabs);

        _workersPage = Page();
        _staffPage = Page();
        _jobsPage = Page();
        BuildWorkers();
        BuildStaff();
        BuildJobs();
        ShowTab(0);
    }

    private VBoxContainer Page()
    {
        var page = new VBoxContainer { CustomMinimumSize = new Vector2(ContentWidth, 0) };
        page.AddThemeConstantOverride("separation", 8);
        Body.AddChild(page);
        return page;
    }

    private void ShowTab(int index)
    {
        _workersPage.Visible = index == 0;
        _staffPage.Visible = index == 1;
        _jobsPage.Visible = index == 2;
        if (Ctx is not null) Refresh();
    }

    /// <summary>Key: a job id (Jobs tab, highlighted), or a tab: "workers", "staff", "jobs".</summary>
    public override void Focus(string key)
    {
        int tab = key.ToLowerInvariant() switch { "workers" => 0, "staff" => 1, "jobs" => 2, _ => -1 };
        if (tab >= 0)
        {
            _tabs.Select(tab);
            return;
        }
        _tabs.Select(2);
        Refresh();
        if (int.TryParse(key, out int id) && _jobCards.TryGetValue(id, out var row)) Highlight(row);
    }

    public override void Refresh()
    {
        var state = Ctx.Sim.State;
        var rules = state.CrewRules;
        _workers.Text = $"{state.Crew.Count}";
        _wages.Text = UiTheme.Money(state.Crew.Sum(m => m.WagePerDayCents));
        _staffTile.Visible = state.StaffRules.Enabled;
        _staffWages.Text = UiTheme.Money(StaffMath.WagesPerDay(state));
        _wood.Text = $"{state.WoodStock}";
        _jobs.Text = $"{state.Jobs.Count}";
        long remaining = state.Jobs.Sum(j => WorkCosts.RemainingMinutes(rules, j));
        long perDay = state.Crew.Sum(m => (long)m.SpeedPermille) * StaffMath.CrewSupervisionPermille(state) / 1_000_000
                      * (state.Rules.CloseMinute - state.Rules.OpenMinute);
        _ahead.Text = remaining == 0 ? "–" : perDay == 0 ? HudContext.CrewTime(remaining) : $"{HudContext.CrewTime(remaining)} (~{(remaining + perDay - 1) / perDay} d)";

        if (_workersPage.Visible) RefreshWorkers(state);
        if (_staffPage.Visible) RefreshStaff(state);
        if (_jobsPage.Visible) RefreshJobs(state);
    }

    // ---------------------------------------------------------------- workers

    private void BuildWorkers()
    {
        var actions = Row(8);
        _hireStandard = UiTheme.Button("Hire a worker", () => Ctx.Host.Enqueue(new HireCrewCommand()), "Paid every evening; works during opening hours");
        actions.AddChild(_hireStandard);
        _hours = UiTheme.Label("", 12, UiTheme.TextDim);
        _hours.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        actions.AddChild(_hours);
        _workersPage.AddChild(actions);

        _workersPage.AddChild(UiTheme.Label("CREW — in hiring order (the first hired pick their job first)", 11, UiTheme.TextDim, bold: true));
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _crewList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _crewList.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_crewList);
        _workersPage.AddChild(scroll);

        var heading = new VBoxContainer();
        heading.AddChild(UiTheme.Separator());
        heading.AddChild(UiTheme.Label("APPLICANTS — looking for work today; new ones come at every opening", 11, UiTheme.TextDim, bold: true));
        _applicantsHeading = heading;
        _workersPage.AddChild(heading);
        _applicants = new HFlowContainer();
        _applicants.AddThemeConstantOverride("h_separation", 8);
        _applicants.AddThemeConstantOverride("v_separation", 8);
        _workersPage.AddChild(_applicants);
        _applicantsNote = UiTheme.Label("", 12, UiTheme.TextDim);
        _workersPage.AddChild(_applicantsNote);
    }

    private void RefreshWorkers(WorldState state)
    {
        var rules = state.CrewRules;
        bool pool = rules.Grades.Count > 0;
        _hireStandard.Visible = !pool;
        _hireStandard.Text = $"Hire a worker ({UiTheme.Money(rules.WagePerDayCents)} / day)";
        _hours.Text = $"Work hours = opening hours {Clock(state.Rules.OpenMinute)}–{Clock(state.Rules.CloseMinute)}" +
                      (StaffMath.CrewSupervisionPermille(state) < 1000 ? $" · too few managers: everyone works at {StaffMath.CrewSupervisionPermille(state) / 10} %" : "");

        string crewSignature = string.Join(',', state.Crew.Select(m => m.Id));
        if (crewSignature != _crewSignature) RebuildCrew(state);
        var uses = WorkCosts.ToolAssignments(state);
        foreach (var (crewId, status, worked) in _crewRows)
        {
            if (state.Crew.FirstOrDefault(m => m.Id == crewId) is not { } member) continue;
            status.Text = WorkerStatus(state, member, uses);
            worked.Text = $"{Math.Max(1, (state.Tick - member.HiredTick) / GameTime.MinutesPerDay + 1)} d · {member.MinutesWorked / 60} h worked";
        }
        var scroll = (ScrollContainer)_crewList.GetParent();
        scroll.CustomMinimumSize = new Vector2(ContentWidth, Math.Min(MaxListHeight, Math.Max(24, _crewList.GetCombinedMinimumSize().Y)));

        _applicantsHeading.Visible = _applicants.Visible = _applicantsNote.Visible = pool;
        string applicantSignature = string.Join(',', state.Applicants.Select(a => a.Id));
        if (pool && applicantSignature != _applicantSignature) RebuildApplicants(state);
        _applicantsNote.Text = state.Applicants.Count == 0 ? "Nobody else today. New applicants come at the next opening." : "";
    }

    private void RebuildCrew(WorldState state)
    {
        _crewSignature = string.Join(',', state.Crew.Select(m => m.Id));
        foreach (var child in _crewList.GetChildren()) child.QueueFree();
        _crewRows.Clear();
        if (state.Crew.Count == 0)
        {
            _crewList.AddChild(UiTheme.Label(state.CrewRules.Grades.Count > 0
                ? "No crew yet. Hire one of the applicants below: they build your trails, paths and features."
                : "No crew yet. Hire workers: they build your trails, paths and features.", 13, UiTheme.TextDim));
            return;
        }
        foreach (var member in state.Crew)
        {
            int crewId = member.Id;
            var row = new PanelContainer();
            var box = UiTheme.Box(UiTheme.Card, 8, 10, 5);
            box.ShadowSize = 0;
            row.AddThemeStyleboxOverride("panel", box);
            var h = Row(10);
            row.AddChild(h);
            h.AddChild(new IconView(UiIcon.Crew, 26, GradeColor(member.GradeId)) { SizeFlagsVertical = SizeFlags.ShrinkCenter });
            var texts = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            texts.AddThemeConstantOverride("separation", 0);
            var top = Row(10);
            top.AddChild(UiTheme.Label(member.Name, 14, bold: true));
            top.AddChild(UiTheme.Label($"{GradeName(state, member.GradeId)} · speed {member.SpeedPermille / 10} % · {UiTheme.Money(member.WagePerDayCents)} / day",
                12, UiTheme.TextDim));
            texts.AddChild(top);
            var status = UiTheme.Label("", 12);
            texts.AddChild(status);
            h.AddChild(texts);
            var worked = UiTheme.Label("", 11, UiTheme.TextDim);
            worked.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            h.AddChild(worked);
            h.AddChild(new RoundButton(UiIcon.Close, 24, "", $"Dismiss {member.Name} (no more wages from tonight)",
                () => Ctx.Host.Enqueue(new DismissCrewCommand(crewId))) { SizeFlagsVertical = SizeFlags.ShrinkCenter });
            _crewList.AddChild(row);
            _crewRows.Add((crewId, status, worked));
        }
    }

    private void RebuildApplicants(WorldState state)
    {
        _applicantSignature = string.Join(',', state.Applicants.Select(a => a.Id));
        foreach (var child in _applicants.GetChildren()) child.QueueFree();
        foreach (var applicant in state.Applicants)
        {
            int id = applicant.Id;
            var card = new PanelContainer { CustomMinimumSize = new Vector2(160, 0) };
            var box = UiTheme.Box(UiTheme.Card, 8, 10, 8);
            box.ShadowSize = 0;
            card.AddThemeStyleboxOverride("panel", box);
            var v = new VBoxContainer();
            v.AddThemeConstantOverride("separation", 2);
            card.AddChild(v);
            var top = Row(6);
            top.AddChild(new IconView(UiIcon.Crew, 18, GradeColor(applicant.GradeId)));
            top.AddChild(UiTheme.Label(applicant.Name, 13, bold: true));
            v.AddChild(top);
            v.AddChild(UiTheme.Label($"{GradeName(state, applicant.GradeId)} · speed {applicant.SpeedPermille / 10} %", 12, GradeColor(applicant.GradeId)));
            v.AddChild(UiTheme.Label($"{UiTheme.Money(applicant.WagePerDayCents)} / day", 12, UiTheme.TextDim));
            v.AddChild(UiTheme.Button("Hire", () => Ctx.Host.Enqueue(new HireCrewCommand(ApplicantId: id)),
                $"Hire {applicant.Name}: works {applicant.SpeedPermille / 10} % as fast as a standard worker, paid {UiTheme.MoneyExact(applicant.WagePerDayCents)} every evening"));
            _applicants.AddChild(card);
        }
    }

    private static string WorkerStatus(WorldState state, CrewMember member, List<ToolUse> uses)
    {
        if (!ParkSchedule.IsOpen(state, state.Tick)) return "Off duty (works during opening hours)";
        if (Jobs.Find(state, member.JobId) is not { } job)
            return state.Jobs.Count == 0 ? "Idle: no jobs (plan something in Build)" : "Idle: no job they can work on now";
        string title = Jobs.Title(state, job);
        if (Jobs.IsWaitingForRiders(state, job)) return $"{title} · waiting for the last riders to leave";
        string verb = job.IsFelling ? "Felling trees" : job.CurrentWorkType == WorkType.Carpentry ? "Carpentry" : "Digging";
        var use = uses.FirstOrDefault(u => u.Member == member);
        string tool = use.Type is { } type ? $"with the {type.Name.ToLowerInvariant()}" : "by hand";
        return $"{verb} — {title}, {tool}";
    }

    internal static string GradeName(WorldState state, string gradeId) =>
        state.CrewRules.Grades.FirstOrDefault(g => g.Id == gradeId)?.Name ?? "Standard";

    private static Color GradeColor(string gradeId) => gradeId switch
    {
        "apprentice" => UiTheme.TextDim,
        "expert" => UiTheme.Warn,
        _ => UiTheme.Accent,
    };

    private static string Clock(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    // ---------------------------------------------------------------- park staff

    private void BuildStaff()
    {
        _staffIntro = UiTheme.Label("", 12, UiTheme.TextDim);
        _staffIntro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _staffIntro.CustomMinimumSize = new Vector2(ContentWidth, 0);
        _staffPage.AddChild(_staffIntro);
        _staffList = new VBoxContainer();
        _staffList.AddThemeConstantOverride("separation", 6);
        _staffPage.AddChild(_staffList);
        _staffTotal = UiTheme.Label("", 13, bold: true);
        _staffPage.AddChild(_staffTotal);
    }

    private void RefreshStaff(WorldState state)
    {
        if (!state.StaffRules.Enabled)
        {
            _staffIntro.Text = $"This park pays a fixed base staff (ticket office, first aid, cleaning): {UiTheme.Money(state.Rules.DailyUpkeepCents)} every day. " +
                               "There are no staff roles to manage here.";
            _staffTotal.Text = "";
            if (_staffRows.Count > 0 || _staffSignature is not null)
            {
                foreach (var child in _staffList.GetChildren()) child.QueueFree();
                _staffRows.Clear();
                _staffSignature = null;
            }
            return;
        }
        _staffIntro.Text = "Everyone who keeps the park running besides the crew. Each role covers part of what the park needs; " +
                           "too few of a role costs guests, mood or safety. Wages are paid every evening, open or not.";
        int level = Ctx.Kpi.Level;
        string signature = $"{state.StaffRoles.Count}|{state.Rules.UnlockLevel(level)}";
        if (signature != _staffSignature) RebuildStaff(state);
        foreach (var (role, count, need, bar, effect, plus) in _staffRows)
        {
            int n = StaffMath.Count(state, role.Id);
            count.Text = $"{n}";
            int coverage = StaffMath.CoveragePermille(state, role.Effect);
            bar.Value = coverage;
            bar.Visible = role.Effect != StaffEffect.Marketing;
            bar.Modulate = coverage >= 1000 ? UiTheme.Good : coverage >= 700 ? UiTheme.Warn : UiTheme.Bad;
            need.Text = NeedText(state, role);
            effect.Text = EffectText(state, role.Effect);
            effect.Modulate = role.Effect != StaffEffect.Marketing && coverage < 1000 ? UiTheme.Warn : Colors.White;
            plus.Disabled = n >= role.MaxCount || (state.Rules.UnlockLevel(level) < role.RequiredLevel);
        }
        _staffTotal.Text = $"Staff: {StaffMath.Headcount(state)} people · {UiTheme.Money(StaffMath.WagesPerDay(state))} per day";
    }

    private void RebuildStaff(WorldState state)
    {
        int level = Ctx.Kpi.Level;
        _staffSignature = $"{state.StaffRoles.Count}|{state.Rules.UnlockLevel(level)}";
        foreach (var child in _staffList.GetChildren()) child.QueueFree();
        _staffRows.Clear();
        foreach (var role in state.StaffRoles)
        {
            string id = role.Id;
            bool locked = state.Rules.UnlockLevel(level) < role.RequiredLevel;
            var row = new PanelContainer { TooltipText = role.Description };
            var box = UiTheme.Box(UiTheme.Card, 8, 12, 8);
            box.ShadowSize = 0;
            row.AddThemeStyleboxOverride("panel", box);
            var h = Row(12);
            row.AddChild(h);
            var texts = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            texts.AddThemeConstantOverride("separation", 2);
            var top = Row(10);
            top.AddChild(UiTheme.Label(role.Name, 14, bold: true));
            top.AddChild(UiTheme.Label(locked ? $"needs park level {role.RequiredLevel}" : $"{UiTheme.Money(role.WagePerDayCents)} each / day", 12,
                locked ? UiTheme.Warn : UiTheme.TextDim));
            texts.AddChild(top);
            var need = UiTheme.Label("", 12, UiTheme.TextDim);
            texts.AddChild(need);
            var bar = new ProgressBar { MinValue = 0, MaxValue = 1000, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 6) };
            texts.AddChild(bar);
            var effect = UiTheme.Label("", 12);
            effect.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            effect.CustomMinimumSize = new Vector2(440, 0);
            texts.AddChild(effect);
            h.AddChild(texts);

            var stepper = Row(6);
            stepper.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            stepper.AddChild(UiTheme.Button("−", () => Ctx.Host.Enqueue(new SetStaffCommand(id, Math.Max(0, StaffMath.Count(Ctx.Sim.State, id) - 1))),
                $"One {role.Name.ToLowerInvariant()} less"));
            var count = UiTheme.Label("", 18, bold: true);
            count.CustomMinimumSize = new Vector2(34, 0);
            count.HorizontalAlignment = HorizontalAlignment.Center;
            stepper.AddChild(count);
            var plus = UiTheme.Button("+", () => Ctx.Host.Enqueue(new SetStaffCommand(id, StaffMath.Count(Ctx.Sim.State, id) + 1)),
                locked ? $"Needs park level {role.RequiredLevel}" : $"One more ({UiTheme.Money(role.WagePerDayCents)} a day, up to {role.MaxCount})");
            stepper.AddChild(plus);
            h.AddChild(stepper);
            _staffList.AddChild(row);
            _staffRows.Add((role, count, need, bar, effect, plus));
        }
    }

    private static string NeedText(WorldState state, StaffRole role)
    {
        long covers = StaffMath.Covers(state, role.Effect);
        long need = StaffMath.Need(state, role.Effect);
        return role.Effect switch
        {
            StaffEffect.Supervision => $"Supervise {covers} of {need} people (staff and crew beyond the first {state.StaffRules.FreeHeadcount})",
            StaffEffect.Tickets => $"Serve {covers} of {need} visitors a day (yesterday's or today's, whichever is more)",
            StaffEffect.FirstAid => $"Look after {covers} of {need} trails and lifts",
            StaffEffect.Cleaning => $"Clean up after {covers} of {need} visitors (each building counts as {state.StaffRules.FacilityCleaningVisitors})",
            _ => $"Each brings guests: the first {role.CoversPer / 10} % more demand, less for every extra one",
        };
    }

    /// <summary>What the role's staffing does right now.</summary>
    internal static string EffectText(WorldState state, StaffEffect effect)
    {
        switch (effect)
        {
            case StaffEffect.Supervision:
                int sup = StaffMath.SupervisionPermille(state);
                return sup >= 1000 ? "Everyone is supervised." : $"Too few managers: staff and crew work at {sup / 10} %.";
            case StaffEffect.Tickets:
                int away = StaffMath.TicketTurnAwayPermille(state);
                return away == 0 ? "No queue at the entrance." : $"Queue at the entrance: {away / 10.0:0.#} % of arrivals turn back, the rest arrive {StaffMath.TicketQueueMood(state)} mood worse.";
            case StaffEffect.FirstAid:
                int wait = StaffMath.RescueWaitPermille(state);
                return wait <= 1000 ? "The injured are looked after until the helicopter comes." : $"The injured wait {wait / 10 - 100} % longer for the helicopter; minor crashes hurt more.";
            case StaffEffect.Cleaning:
                int shortage = StaffMath.ShortagePermille(state, StaffEffect.Cleaning);
                return shortage == 0 ? "The park is clean." : $"Litter and dirty toilets: guests lose mood {shortage / 100} of every 10 minutes.";
            default:
                int marketing = StaffMath.MarketingPermille(state);
                return marketing <= 1000 ? "No marketing: guests come from reviews and word of mouth." : $"+{(marketing - 1000) / 10.0:0.#} % more guests.";
        }
    }

    // ---------------------------------------------------------------- jobs

    private void BuildJobs()
    {
        var wood = Row(8);
        wood.AddChild(UiTheme.Label("WOOD", 11, UiTheme.TextDim, bold: true));
        _buy10 = UiTheme.Button("", () => Ctx.Host.Enqueue(new BuyWoodCommand(10)), "Buying is expensive; felling trees is cheaper");
        _buy50 = UiTheme.Button("", () => Ctx.Host.Enqueue(new BuyWoodCommand(50)), "Buying is expensive; felling trees is cheaper");
        wood.AddChild(_buy10);
        wood.AddChild(_buy50);
        wood.AddChild(UiTheme.Button("Fell trees…", () => Ctx.Clearing.SetActive(true), "Mark an area of forest for the crew to cut (each tree gives wood)"));
        _jobsPage.AddChild(wood);
        _jobsPage.AddChild(UiTheme.Separator());

        _jobsPage.AddChild(UiTheme.Label("JOBS — in priority order · a trail is dug in sections at once, a feature by a few workers at one spot", 11, UiTheme.TextDim, bold: true));
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _jobList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _jobList.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_jobList);
        _jobsPage.AddChild(scroll);
    }

    private void RefreshJobs(WorldState state)
    {
        var rules = state.CrewRules;
        _buy10.Text = $"Buy 10 ({UiTheme.Money(10 * rules.WoodPriceCents)})";
        _buy50.Text = $"Buy 50 ({UiTheme.Money(50 * rules.WoodPriceCents)})";
        string jobSignature = string.Join(',', state.Jobs.Select(j => j.Id));
        if (jobSignature != _jobSignature) RebuildJobs(state);
        foreach (var (jobId, title, stateLabel, bar, workers) in _jobRows)
        {
            if (Jobs.Find(state, jobId) is not { } job) continue;
            title.Text = Jobs.Title(state, job);
            stateLabel.Text = JobState(state, job);
            bar.Value = WorkCosts.ProgressPermille(rules, job);
            int on = state.Crew.Count(m => m.JobId == job.Id);
            int auto = JobSystem.AutoLimit(state, job);
            workers.Text = job.Workers == 0 ? $"{on} · auto (≤{auto})" : $"{on} · max {Math.Min(job.Workers, auto)}";
        }
        var scroll = (ScrollContainer)_jobList.GetParent();
        scroll.CustomMinimumSize = new Vector2(ContentWidth, Math.Min(MaxListHeight, Math.Max(30, _jobList.GetCombinedMinimumSize().Y)));
    }

    private static string JobState(WorldState state, Job job)
    {
        int workers = state.Crew.Count(m => m.JobId == job.Id);
        var rules = state.CrewRules;
        string who = workers == 0 ? "" : $" · {workers} worker{(workers == 1 ? "" : "s")}";
        if (!JobSystem.IsWorkable(state, job))
        {
            if (job.Kind == JobKind.BuildFeature && state.Ways.FirstOrDefault(w => w.Id == job.WayId)?.Built != true)
                return "Waiting for the trail to be built";
            if (job.Wood > 0 && !job.WoodTaken)
                return $"Waiting for wood: needs {job.Wood}, {state.WoodStock} in stock (fell trees or buy)";
        }
        if (workers > 0 && Jobs.IsWaitingForRiders(state, job))
            return $"Trail closed · waiting for the last riders to leave{who}";
        string left = HudContext.CrewTime(WorkCosts.RemainingMinutes(rules, job)) + " left";
        if (job.IsFelling)
            return $"Felling {job.TreesFelled}/{job.Trees.Count} trees · {left}{who}";
        string verb = job.CurrentWorkType == WorkType.Carpentry ? "Carpentry" : "Digging";
        int sections = job.Kind == JobKind.BuildWay ? Math.Min(WorkCosts.SectionsLeft(state, job), Math.Max(1, (workers + rules.WorkersPerSection - 1) / rules.WorkersPerSection)) : 0;
        string parallel = sections > 1 ? $" · {sections} sections at once" : "";
        return workers == 0 ? $"Queued · {left}" : $"{verb} {WorkCosts.ProgressPermille(rules, job) / 10} %{parallel} · {left}{who}";
    }

    private void RebuildJobs(WorldState state)
    {
        _jobSignature = string.Join(',', state.Jobs.Select(j => j.Id));
        foreach (var child in _jobList.GetChildren()) child.QueueFree();
        _jobRows.Clear();
        _jobCards.Clear();
        if (state.Jobs.Count == 0)
        {
            _jobList.AddChild(UiTheme.Label("No jobs. Plan a trail, a feature or a felling area in Build.", 13, UiTheme.TextDim));
            return;
        }
        for (int i = 0; i < state.Jobs.Count; i++)
        {
            var job = state.Jobs[i];
            int jobId = job.Id;
            var row = new PanelContainer();
            _jobCards[jobId] = row;
            var box = UiTheme.Box(UiTheme.Card, 8, 10, 6);
            box.ShadowSize = 0;
            row.AddThemeStyleboxOverride("panel", box);
            var h = Row(10);
            row.AddChild(h);
            var icon = job.Kind switch
            {
                JobKind.FellTrees => UiIcon.Felling,
                JobKind.BuildWay => state.Ways.FirstOrDefault(w => w.Id == job.WayId)?.Kind == WayKind.AccessPath ? UiIcon.Path : UiIcon.Trail,
                JobKind.RepairFeature => UiIcon.Tool,
                JobKind.BuildSign => UiIcon.Star,
                _ => FeatureIconFor(state, job),
            };
            h.AddChild(new IconView(icon, 30, UiTheme.Accent) { SizeFlagsVertical = SizeFlags.ShrinkCenter });
            var texts = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            texts.AddThemeConstantOverride("separation", 1);
            var title = UiTheme.Label("", 14, bold: true);
            var stateLabel = UiTheme.Label("", 12, UiTheme.TextDim);
            var bar = new ProgressBar { MinValue = 0, MaxValue = 1000, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 6) };
            texts.AddChild(title);
            texts.AddChild(stateLabel);
            texts.AddChild(bar);
            h.AddChild(texts);

            // Workers on the job: fewer leaves the rest for the next jobs; "auto" = as many as the work allows.
            var stepper = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
            stepper.AddThemeConstantOverride("separation", 0);
            stepper.AddChild(UiTheme.Label("WORKERS", 9, UiTheme.TextDim, bold: true));
            var buttons = Row(4);
            buttons.AddChild(Small(UiTheme.Button("−", () => StepWorkers(jobId, -1), "Fewer workers on this job (the others take the next jobs)")));
            var workers = UiTheme.Label("", 12, bold: true);
            workers.CustomMinimumSize = new Vector2(92, 0);
            workers.HorizontalAlignment = HorizontalAlignment.Center;
            workers.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            buttons.AddChild(workers);
            buttons.AddChild(Small(UiTheme.Button("+", () => StepWorkers(jobId, 1), "More workers on this job (up to what the work allows: then auto)")));
            stepper.AddChild(buttons);
            h.AddChild(stepper);

            if (i > 0)
                h.AddChild(new RoundButton(UiIcon.Up, 26, "", "Move to the top of the queue", () => Ctx.Host.Enqueue(new PrioritizeJobCommand(jobId))) { SizeFlagsVertical = SizeFlags.ShrinkCenter });
            h.AddChild(new RoundButton(UiIcon.Close, 26, "", job.Kind switch
                {
                    JobKind.FellTrees => "Cancel (trees cut so far stay cut)",
                    JobKind.RepairFeature => "Cancel the repair (the trail opens again)",
                    JobKind.BuildSign => "Cancel the sign: the sponsor deal ends",
                    _ => "Cancel: removes the planned way or feature",
                }, () => Ctx.Host.Enqueue(new CancelJobCommand(jobId))) { SizeFlagsVertical = SizeFlags.ShrinkCenter });
            _jobList.AddChild(row);
            _jobRows.Add((jobId, title, stateLabel, bar, workers));
        }
    }

    /// <summary>− from auto starts at the workers on it now; + up to the job's limit, past it back to auto.</summary>
    private void StepWorkers(int jobId, int step)
    {
        var state = Ctx.Sim.State;
        if (Jobs.Find(state, jobId) is not { } job) return;
        int auto = JobSystem.AutoLimit(state, job);
        int on = state.Crew.Count(m => m.JobId == jobId);
        int current = job.Workers > 0 ? Math.Min(job.Workers, auto) : on > 0 ? Math.Min(on, auto) : auto;
        int next = step < 0 ? Math.Max(1, current - 1)
            : job.Workers == 0 || current + 1 >= auto ? 0 : current + 1;
        if (next != job.Workers) Ctx.Host.Enqueue(new SetJobWorkersCommand(jobId, next));
    }

    private static Button Small(Button button)
    {
        button.CustomMinimumSize = new Vector2(26, 24);
        return button;
    }

    private static UiIcon FeatureIconFor(WorldState state, Job job)
    {
        var feature = state.Ways.FirstOrDefault(w => w.Id == job.WayId)?.Features.FirstOrDefault(f => f.Id == job.FeatureId);
        var type = feature is null ? null : TrailFeatures.FindType(state.TrailFeatureTypes, feature.TypeId);
        return type is null ? UiIcon.Build : BuildPanel.FeatureIcon(type.Kind);
    }
}
