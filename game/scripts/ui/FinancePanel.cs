using Bikepark.Sim.Banking;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.Sponsors;
using Bikepark.Sim.Staff;
using Bikepark.Sim.State;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// The park's budget, in the spirit of SimCity's: regular income and expenses per area as bars (yesterday's closed day,
/// or today so far), sponsors, loans, one-off transactions (land, lifts, buildings, tools, wood, loans) and the balance.
/// Below: the last days and the income by place (buildings, lifts: a click opens their window). Prices are not set here
/// but where they apply: the park window (entrance, parking, beer hours), a lift's window (lift pass), a building's.
/// </summary>
public partial class FinancePanel : HudPanel
{
    /// <summary>One line of the budget: what an area of the park costs and brings in.</summary>
    private sealed record BudgetLine(string Name, MoneyCategory[] Expenses, MoneyCategory[] Income, string Hint, bool Loans = false);

    private static readonly BudgetLine[] Lines =
    [
        new("Entrance", [], [MoneyCategory.EntryFees], "Entrance fees paid on arrival"),
        new("Lifts", [MoneyCategory.LiftFees, MoneyCategory.LiftUpkeep], [MoneyCategory.LiftPasses],
            "Lift day passes sold · bike access booked on company lifts · upkeep of your own lifts"),
        new("Food & drinks", [MoneyCategory.BarUpkeep, MoneyCategory.FoodGoods], [MoneyCategory.Food, MoneyCategory.Drinks],
            "Lunch, snacks, coffee and beer sold · the goods sold · upkeep of the bars"),
        new("Merch", [MoneyCategory.ShopUpkeep, MoneyCategory.MerchGoods], [MoneyCategory.Merch], "Merch sold · the goods sold · upkeep of the shops"),
        new("Parking & camping", [MoneyCategory.CampUpkeep], [MoneyCategory.ParkingFees, MoneyCategory.Camping],
            "Parking fees · nights at the campground · upkeep of the campgrounds"),
        new("Sponsors & media", [MoneyCategory.CameraUpkeep], [MoneyCategory.Sponsors, MoneyCategory.Clips],
            "Trail and park sponsors (once their sign stands) · clips sold at photo spots · camera upkeep"),
        new("Bikes", [MoneyCategory.BikeCosts], [MoneyCategory.Rentals, MoneyCategory.Repairs],
            "Bike rentals and repairs · parts and upkeep of rental stations and service shops (new fleet bikes are one-offs)"),
        new("Crew", [MoneyCategory.Wages], [], "Daily wages of your crew (Crew menu: hire and dismiss)"),
        new("Park staff", [MoneyCategory.ParkStaff], [], "Managers, ticket office, first aid, cleaning and marketing: every day, open or not (Crew menu → Park staff)"),
        new("Loans", [], [], "Weekly installments of your running loans, per day", Loans: true),
    ];

    private const int BarWidth = 200;

    private Label _advice = null!;
    private Button _yesterdayButton = null!, _todayButton = null!;
    private Label _budgetCaption = null!;
    private readonly List<(BudgetLine Line, Control Row, BudgetBar Expense, BudgetBar Income)> _rows = [];
    private Label _expenseTotal = null!, _incomeTotal = null!, _profit = null!, _withTransactions = null!;
    private PanelContainer _profitBox = null!;
    private bool _showToday;

    private VBoxContainer? _sponsors;
    private string _sponsorsSignature = "";
    private VBoxContainer? _loans;
    private string _loansSignature = "";
    private VBoxContainer _transactions = null!;
    private Label _transactionsSum = null!;
    private string _transactionsSignature = "";
    private Label _money = null!, _debtStatus = null!;

    private VBoxContainer _places = null!;
    private readonly Dictionary<string, (PanelContainer Row, Label Today, Label Total)> _placeRows = [];
    private string _placesSignature = "";
    private DayChart _chart = null!;

    public override string Title => "Finances";

    protected override void Build()
    {
        var advisor = new PanelContainer();
        advisor.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 8, 14, 8, new Color(UiTheme.Accent, 0.35f), 1));
        _advice = UiTheme.Label("", 13);
        _advice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _advice.CustomMinimumSize = new Vector2(900, 0);
        advisor.AddChild(_advice);
        Body.AddChild(advisor);

        var columns = Row(18);
        var left = new VBoxContainer();
        left.AddThemeConstantOverride("separation", 6);
        left.CustomMinimumSize = new Vector2(560, 0);
        columns.AddChild(left);
        var right = new VBoxContainer();
        right.AddThemeConstantOverride("separation", 10);
        right.CustomMinimumSize = new Vector2(340, 0);
        right.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        columns.AddChild(right);
        Body.AddChild(columns);

        BuildBudget(left);
        BuildSponsors(right);
        BuildLoans(right);
        BuildTransactions(right);
        BuildBalance(right);

        left.AddChild(UiTheme.Separator());
        left.AddChild(UiTheme.Label("LAST DAYS — revenue (green) and expenses (red), one-offs included", 11, UiTheme.TextDim, bold: true));
        _chart = new DayChart { CustomMinimumSize = new Vector2(560, 90) };
        left.AddChild(_chart);
        left.AddChild(UiTheme.Separator());
        left.AddChild(UiTheme.Label("INCOME BY PLACE — click one for its window (prices, upgrades, lift pass)", 11, UiTheme.TextDim, bold: true));
        _places = new VBoxContainer();
        _places.AddThemeConstantOverride("separation", 2);
        left.AddChild(_places);
        var hint = UiTheme.Label("Prices are set where they apply: entrance fee, lift pass and parking fee in the Park menu [H], " +
                                 "food, drinks and shop prices in the building's window.", 11, UiTheme.TextDim);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        hint.CustomMinimumSize = new Vector2(560, 0);
        left.AddChild(hint);
    }

    // ---------------------------------------------------------------- building

    private void BuildBudget(VBoxContainer left)
    {
        var header = Row(8);
        _budgetCaption = UiTheme.Label("BUDGET", 11, UiTheme.TextDim, bold: true);
        _budgetCaption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(_budgetCaption);
        var group = new ButtonGroup();
        _yesterdayButton = Toggle("Yesterday", group, false, "The last closed day: what the guests brought and cost");
        _todayButton = Toggle("Today so far", group, true, "Today until now (the day closes at midnight)");
        header.AddChild(_yesterdayButton);
        header.AddChild(_todayButton);
        left.AddChild(header);

        var heads = Row(8);
        heads.AddChild(Cell("", 120));
        heads.AddChild(Cell("EXPENSES", BarWidth, UiTheme.TextDim, 11, bold: true, align: HorizontalAlignment.Right));
        heads.AddChild(Cell("INCOME", BarWidth, UiTheme.TextDim, 11, bold: true));
        left.AddChild(heads);

        var state = Ctx.Sim.State;
        foreach (var line in Lines)
        {
            if (!Shown(state, line)) continue;
            var row = Row(8);
            row.MouseFilter = MouseFilterEnum.Pass;
            row.AddChild(Cell(line.Name, 120, UiTheme.Text, 13, align: HorizontalAlignment.Right));
            var expense = new BudgetBar { Color = UiTheme.Bad, RightToLeft = true, CustomMinimumSize = new Vector2(BarWidth, 22), Empty = line.Expenses.Length == 0 && !line.Loans };
            var income = new BudgetBar { Color = UiTheme.Good, CustomMinimumSize = new Vector2(BarWidth, 22), Empty = line.Income.Length == 0 };
            row.AddChild(expense);
            row.AddChild(income);
            // Where the money of a line is managed: a click takes you there.
            (Hud.Menu Menu, string Focus)? manage = line.Name switch
            {
                "Crew" => (Hud.Menu.Crew, "workers"),
                "Park staff" => (Hud.Menu.Crew, "staff"),
                "Entrance" or "Parking & camping" => (Hud.Menu.Park, "arrivals"),
                _ => null,
            };
            if (manage is { } target)
            {
                row.MouseFilter = MouseFilterEnum.Stop;
                row.MouseDefaultCursorShape = CursorShape.PointingHand;
                row.GuiInput += e =>
                {
                    if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Ctx.ShowMenu(target.Menu, target.Focus);
                };
            }
            left.AddChild(row);
            _rows.Add((line, row, expense, income));
        }

        left.AddChild(UiTheme.Separator());
        var totals = Row(8);
        totals.AddChild(Cell("Subtotal", 120, UiTheme.Text, 13, bold: true, align: HorizontalAlignment.Right));
        _expenseTotal = Cell("", BarWidth, UiTheme.Bad, 15, bold: true, align: HorizontalAlignment.Right);
        _incomeTotal = Cell("", BarWidth, UiTheme.Good, 15, bold: true);
        totals.AddChild(_expenseTotal);
        totals.AddChild(_incomeTotal);
        left.AddChild(totals);

        var profit = Row(8);
        profit.AddChild(Cell("Profit / loss", 120, UiTheme.Text, 13, bold: true, align: HorizontalAlignment.Right));
        _profitBox = new PanelContainer { CustomMinimumSize = new Vector2(BarWidth * 2 + 8, 0) };
        _profit = UiTheme.Label("", 18, bold: true);
        _profit.HorizontalAlignment = HorizontalAlignment.Center;
        _profitBox.AddChild(_profit);
        profit.AddChild(_profitBox);
        left.AddChild(profit);

        var with = Row(8);
        with.AddChild(Cell("+ transactions", 120, UiTheme.TextDim, 12, align: HorizontalAlignment.Right));
        _withTransactions = Cell("", BarWidth * 2 + 8, UiTheme.TextDim, 13, align: HorizontalAlignment.Center);
        with.AddChild(_withTransactions);
        left.AddChild(with);
    }

    private void BuildSponsors(VBoxContainer right)
    {
        if (Ctx.Sim.State.SponsorTypes.Count == 0) return;
        var card = Card(right, "SPONSORS — the park and its trails, with benefits");
        _sponsors = new VBoxContainer();
        _sponsors.AddThemeConstantOverride("separation", 6);
        card.AddChild(_sponsors);
    }

    /// <summary>
    /// The park sponsor and the trail deals (offers to accept or turn down, applications, running deals with what they
    /// pay and give), then every brand with what it would sponsor now: apply for the park or its best trail.
    /// </summary>
    private void RefreshSponsors(WorldState state)
    {
        if (_sponsors is null) return;
        int level = Ctx.Kpi.Level;
        long day = GameTime.Day(state.Tick);
        string signature = $"{level}|{day}|{state.Ways.Count}|{state.WaysRevision}|{string.Join(",", state.Sponsorships.Select(d => $"{d.Id}:{d.Status}:{d.SignBuilt}:{d.PaidCents / 10_000}"))}";
        if (signature == _sponsorsSignature) return;
        _sponsorsSignature = signature;
        foreach (var child in _sponsors.GetChildren()) child.QueueFree();

        string Target(Sponsorship d) => d.IsPark ? "the park" : state.Ways.FirstOrDefault(w => w.Id == d.WayId)?.Name ?? "a trail";
        var open = state.Sponsorships.Where(d => d.Status != SponsorshipStatus.Ended).OrderBy(d => d.IsPark ? 0 : 1).ThenBy(d => d.Id).ToList();
        if (open.Count == 0)
            _sponsors.AddChild(UiTheme.Label("No sponsors yet: brands come by for very good trails, or apply below.", 12, UiTheme.TextDim));
        foreach (var deal in open)
        {
            if (SponsorMath.Find(state, deal.SponsorId) is not { } brand) continue;
            var block = new VBoxContainer();
            block.AddThemeConstantOverride("separation", 0);
            var head = Row(8);
            var name = UiTheme.Label($"{brand.Name} · {Target(deal)}", 13, Color.FromHtml(brand.Color).Lightened(0.35f), bold: true);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            head.AddChild(name);
            int id = deal.Id;
            int scale = deal.IsPark ? 1000 : state.SponsorRules.TrailBenefitPermille;
            long pays = deal.IsPark ? brand.ParkDailyCents : brand.DailyCents;
            string extra = deal.IsPark ? brand.ParkPerVisitorCents > 0 ? $" + {UiTheme.Money(brand.ParkPerVisitorCents)}/visitor" : ""
                : brand.PerRunCents > 0 ? $" + {UiTheme.Money(brand.PerRunCents)}/run" : "";
            string detail;
            switch (deal.Status)
            {
                case SponsorshipStatus.Offered:
                    head.AddChild(UiTheme.Button("Accept", () => Ctx.Host.Enqueue(new AcceptSponsorCommand(id)),
                        "The crew puts up the brand's sign at the entrance; it pays from the day the sign stands"));
                    head.AddChild(UiTheme.Button("No", () => Ctx.Host.Enqueue(new DeclineSponsorCommand(id)), "Turn the offer down"));
                    detail = $"Offers {UiTheme.Money(pays)}/day{extra} for {(deal.IsPark ? brand.ParkDays : brand.Days)} days · until day {GameTime.Day(deal.ExpiresTick) + 1}";
                    break;
                case SponsorshipStatus.Applied:
                    detail = $"You applied: answer on day {GameTime.Day(deal.AnswerTick) + 1}";
                    break;
                default:
                    head.AddChild(UiTheme.Button("End", () => Ctx.Host.Enqueue(new EndSponsorshipCommand(id)), "End the deal now (the money stops)"));
                    detail = (deal.SignBuilt ? $"{UiTheme.Money(pays)}/day{extra} · paid {UiTheme.Money(deal.PaidCents)}" : "The crew is putting up the sign (Crew menu)")
                             + $" · {Math.Max(0, deal.EndDay - day)} days left";
                    break;
            }
            block.AddChild(head);
            block.AddChild(UiTheme.Label(detail, 11, UiTheme.TextDim));
            block.AddChild(UiTheme.Label(SponsorMath.Describe(brand, scale), 11, UiTheme.Good));
            _sponsors.AddChild(block);
        }

        _sponsors.AddChild(UiTheme.Label("BRANDS — apply, they answer in a few days", 11, UiTheme.TextDim, bold: true));
        var network = Ctx.Sim.Network;
        foreach (var brand in state.SponsorTypes.OrderBy(b => b.RequiredLevel))
        {
            var row = Row(6);
            var label = UiTheme.Label(brand.Name, 12, bold: true);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            label.TooltipText = $"{brand.Name}: {SponsorMath.Describe(brand)} (a park deal in full, a trail deal {state.SponsorRules.TrailBenefitPermille / 10} %)";
            row.AddChild(label);
            bool busy = SponsorMath.Open(state, brand.Id) is not null;
            string id = brand.Id;
            if (brand.SponsorsParks)
            {
                string? misfit = SponsorMath.ParkMisfit(state, brand, level);
                var park = UiTheme.Button("Park", () => Ctx.Host.Enqueue(new ApplySponsorCommand(id, 0)),
                    misfit ?? $"Ask {brand.Name} to sponsor the whole park: {UiTheme.Money(brand.ParkDailyCents)}/day, {SponsorMath.Describe(brand)}");
                park.Disabled = busy || misfit is not null || state.Sponsorships.Any(d => d.IsPark && d.Status != SponsorshipStatus.Ended);
                row.AddChild(park);
            }
            var best = SponsorMath.BestTrail(state, network, brand, level);
            string? why = best is null
                ? network.Trails.Select(t => SponsorMath.TrailMisfit(state, network, brand, t, level)).FirstOrDefault(m => m is not null) ?? "No trail fits."
                : null;
            int wayId = best?.Id ?? 0;
            var trail = UiTheme.Button(best is null ? "Trail" : best.Name, () => Ctx.Host.Enqueue(new ApplySponsorCommand(id, wayId)),
                why ?? $"Ask {brand.Name} to sponsor {best!.Name}: {UiTheme.Money(brand.DailyCents)}/day, {SponsorMath.Describe(brand, state.SponsorRules.TrailBenefitPermille)}");
            trail.Disabled = busy || best is null;
            row.AddChild(trail);
            _sponsors.AddChild(row);
        }
    }

    private void BuildLoans(VBoxContainer right)
    {
        if (Ctx.Sim.State.LoanRules.Offers.Count == 0) return;
        var card = Card(right, "LOANS — paid back weekly, flat interest");
        _loans = new VBoxContainer();
        _loans.AddThemeConstantOverride("separation", 6);
        card.AddChild(_loans);
    }

    private void BuildTransactions(VBoxContainer right)
    {
        var card = Card(right, "RECENT TRANSACTIONS — one-offs");
        _transactions = new VBoxContainer();
        _transactions.AddThemeConstantOverride("separation", 1);
        card.AddChild(_transactions);
        _transactionsSum = UiTheme.Label("", 12, bold: true);
        card.AddChild(_transactionsSum);
    }

    private void BuildBalance(VBoxContainer right)
    {
        var card = Card(right, "BALANCE");
        _money = UiTheme.Label("", 26, bold: true);
        card.AddChild(_money);
        _debtStatus = UiTheme.Label("", 12, UiTheme.Bad);
        _debtStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        card.AddChild(_debtStatus);
    }

    // ---------------------------------------------------------------- refreshing

    public override void Refresh()
    {
        var state = Ctx.Sim.State;
        var f = state.Finance;
        var yesterday = Ctx.Days.LastOrDefault();
        bool today = _showToday || yesterday?.ByCategory is null;
        _todayButton.SetPressedNoSignal(today);
        _yesterdayButton.SetPressedNoSignal(!today);
        _yesterdayButton.Disabled = yesterday?.ByCategory is null;
        IReadOnlyList<long>? money = today ? f.TodayCents : yesterday!.ByCategory;
        _budgetCaption.Text = today
            ? $"BUDGET — TODAY SO FAR ({GameTime.Format(state.Tick).ToUpperInvariant()})"
            : $"BUDGET — YESTERDAY (DAY {yesterday!.Day + 1}, {yesterday.Visitors} GUESTS)";

        long loansPerDay = LoansPerDay(state);
        var values = _rows.Select(r => (
            Expense: r.Line.Loans ? loansPerDay : r.Line.Expenses.Sum(c => Ledger.Get(money, c)),
            Income: r.Line.Income.Sum(c => Ledger.Get(money, c)))).ToList();
        long max = Math.Max(1, values.Max(v => Math.Max(v.Expense, v.Income)));
        for (int i = 0; i < _rows.Count; i++)
        {
            var (line, row, expense, income) = _rows[i];
            expense.Set(values[i].Expense, max, line.Loans ? "/day" : "");
            income.Set(values[i].Income, max, "");
            row.TooltipText = Tooltip(state, line, money) + line.Name switch
            {
                "Crew" or "Park staff" => "\nClick to manage them (Crew menu)",
                "Entrance" or "Parking & camping" => "\nClick to set the fees (Park menu)",
                _ => "",
            };
        }
        long expenses = values.Sum(v => v.Expense), incomes = values.Sum(v => v.Income);
        _expenseTotal.Text = $"−{UiTheme.Money(expenses)}";
        _incomeTotal.Text = $"+{UiTheme.Money(incomes)}";
        long profit = incomes - expenses;
        _profit.Text = Signed(profit);
        _profitBox.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(profit >= 0 ? UiTheme.Good : UiTheme.Bad, 0.55f), 6, 8, 3));
        long oneOffs = Enum.GetValues<MoneyCategory>().Where(c => !Ledger.IsRegular(c))
            .Sum(c => Ledger.IsIncome(c) ? Ledger.Get(money, c) : -Ledger.Get(money, c));
        _withTransactions.Text = oneOffs == 0 ? "no one-off transactions" : $"{Signed(oneOffs)} one-offs  →  {Signed(profit + oneOffs)}";

        _advice.Text = Advice(state, yesterday, loansPerDay);
        RefreshSponsors(state);
        RefreshLoans();
        RefreshTransactions(state);
        _money.Text = UiTheme.MoneyExact(f.MoneyCents);
        _money.AddThemeColorOverride("font_color", f.MoneyCents < 0 ? UiTheme.Bad : UiTheme.Text);
        int limit = state.Rules.BankruptcyDays;
        _debtStatus.Text = state.Park.Bankrupt ? "The park is bankrupt."
            : f.DaysInDebt > 0 && limit > 0 ? $"In the red for {f.DaysInDebt} day(s): bankrupt after {limit} days in a row."
            : LoanMath.DebtCents(state) > 0 ? $"Still owed on loans: {UiTheme.Money(LoanMath.DebtCents(state))}" : "";
        _debtStatus.AddThemeColorOverride("font_color", f.DaysInDebt > 0 || state.Park.Bankrupt ? UiTheme.Bad : UiTheme.TextDim);
        _debtStatus.Visible = _debtStatus.Text.Length > 0;

        RefreshPlaces(state);
        _chart.Days = Ctx.Days;
        _chart.QueueRedraw();
    }

    /// <summary>Key: a bar's or shop's id (opens its window).</summary>
    public override void Focus(string key)
    {
        Refresh();
        if (int.TryParse(key, out int id)) Ctx.Inspect(new InspectTarget(InspectKind.Facility, id));
    }

    /// <summary>
    /// What each building and lift brought in (today and in total), biggest first; a click opens its window. Lifts only
    /// count with a lift pass (the money of the passes bought there).
    /// </summary>
    private void RefreshPlaces(WorldState state)
    {
        var places = new List<(string Key, string Name, UiIcon Icon, long Today, long Total, InspectTarget Target)>();
        foreach (var facility in state.Facilities)
        {
            var kind = FacilityMath.FindType(state, facility.TypeId)?.Kind;
            places.Add(($"f{facility.Id}", facility.Name, kind switch
                {
                    FacilityKind.Bar => UiIcon.Bar, FacilityKind.Campground => UiIcon.Tent, FacilityKind.Rental => UiIcon.Riders,
                    FacilityKind.Service => UiIcon.Tool, _ => UiIcon.Shop,
                }, facility.RevenueTodayCents, facility.TotalRevenueCents, new InspectTarget(InspectKind.Facility, facility.Id)));
        }
        foreach (var lift in state.Lifts.Where(l => l.Stats.PassRevenueCents > 0 || state.Park.LiftTicketCents > 0))
            places.Add(($"l{lift.Id}", $"{lift.Name} (lift passes)", UiIcon.Lift, lift.Stats.PassRevenueTodayCents, lift.Stats.PassRevenueCents,
                new InspectTarget(InspectKind.Lift, lift.Id)));
        places = places.OrderByDescending(p => p.Total).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();

        string signature = string.Join(",", places.Select(p => p.Key));
        if (signature != _placesSignature)
        {
            _placesSignature = signature;
            foreach (var child in _places.GetChildren()) child.QueueFree();
            _placeRows.Clear();
            if (places.Count == 0)
                _places.AddChild(UiTheme.Label("No bars, shops or paid lifts yet: without a bar guests bring a packed lunch and buy no snacks.", 12, UiTheme.TextDim));
            foreach (var place in places)
            {
                var row = new PanelContainer { MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = $"{place.Name}: click for its window" };
                var style = UiTheme.Box(UiTheme.Card, 6, 8, 4);
                style.ShadowSize = 0;
                row.AddThemeStyleboxOverride("panel", style);
                var target = place.Target;
                row.GuiInput += e =>
                {
                    if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Ctx.Inspect(target);
                };
                var line = Row(10);
                line.MouseFilter = MouseFilterEnum.Ignore;
                line.AddChild(new IconView(place.Icon, 18, UiTheme.Accent) { MouseFilter = MouseFilterEnum.Ignore });
                var name = Cell(place.Name, 0, UiTheme.Text, 12, bold: true);
                name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                name.MouseFilter = MouseFilterEnum.Ignore;
                line.AddChild(name);
                var today = Cell("", 120, UiTheme.TextDim, 12, align: HorizontalAlignment.Right);
                var total = Cell("", 120, UiTheme.Text, 12, bold: true, align: HorizontalAlignment.Right);
                today.MouseFilter = total.MouseFilter = MouseFilterEnum.Ignore;
                line.AddChild(today);
                line.AddChild(total);
                row.AddChild(line);
                _places.AddChild(row);
                _placeRows[place.Key] = (row, today, total);
            }
        }
        foreach (var place in places)
        {
            if (!_placeRows.TryGetValue(place.Key, out var row)) continue;
            row.Today.Text = $"today {UiTheme.Money(place.Today)}";
            row.Total.Text = $"total {UiTheme.Money(place.Total)}";
        }
    }

    /// <summary>A short status, like a SimCity advisor: how yesterday went, the biggest cost, debt.</summary>
    private string Advice(WorldState state, DayReport? yesterday, long loansPerDay)
    {
        var f = state.Finance;
        if (state.Park.Bankrupt) return "The park is bankrupt: the bank has closed it.";
        string debt = f.DaysInDebt > 0 && state.Rules.BankruptcyDays > 0
            ? $" You are in the red ({f.DaysInDebt} of {state.Rules.BankruptcyDays} days until bankruptcy): take a loan, raise prices or cut costs."
            : f.MoneyCents < 0 ? " You are in the red: take a loan, raise prices or cut costs." : "";
        if (yesterday?.ByCategory is not { } money) return "The first day isn't over yet: the budget fills as the guests come." + debt;

        var costs = _rows.Select(r => (r.Line.Name, Cents: r.Line.Loans ? loansPerDay : r.Line.Expenses.Sum(c => Ledger.Get(money, c))))
            .OrderByDescending(c => c.Cents).ToList();
        long income = _rows.Sum(r => r.Line.Income.Sum(c => Ledger.Get(money, c)));
        long profit = income - costs.Sum(c => c.Cents);
        string biggest = costs.Count > 0 && costs[0].Cents > 0 ? $" Biggest cost: {costs[0].Name.ToLowerInvariant()} ({UiTheme.Money(costs[0].Cents)})." : "";
        string verdict = profit >= 0
            ? $"Yesterday the park made {UiTheme.Money(profit)} from its regular business ({yesterday.Visitors} guests)."
            : $"Yesterday the regular business lost {UiTheme.Money(-profit)} ({yesterday.Visitors} guests).";
        return verdict + biggest + debt;
    }

    /// <summary>The newest one-offs first, and what they added up to over the last 7 days.</summary>
    private void RefreshTransactions(WorldState state)
    {
        var list = state.Finance.Transactions;
        string signature = $"{list.Count}|{list.LastOrDefault()?.Tick}|{list.LastOrDefault()?.Label}";
        if (signature == _transactionsSignature) return;
        _transactionsSignature = signature;
        foreach (var child in _transactions.GetChildren()) child.QueueFree();
        if (list.Count == 0)
            _transactions.AddChild(UiTheme.Label("None yet: land, lifts, buildings, tools, wood and loans show up here.", 12, UiTheme.TextDim));
        foreach (var t in list.AsEnumerable().Reverse().Take(8))
        {
            var row = Row(8);
            row.AddChild(Cell($"Day {GameTime.Day(t.Tick) + 1}", 52, UiTheme.TextDim, 12));
            var label = Cell(t.Label, 0, UiTheme.Text, 12);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            label.TooltipText = $"{t.Label} · {GameTime.Format(t.Tick)}";
            row.AddChild(label);
            row.AddChild(Cell(Signed(t.Cents), 96, t.Cents >= 0 ? UiTheme.Good : UiTheme.Bad, 12, bold: true, align: HorizontalAlignment.Right));
            _transactions.AddChild(row);
        }
        long since = GameTime.Day(state.Tick) - 6;
        long week = list.Where(t => GameTime.Day(t.Tick) >= since).Sum(t => t.Cents);
        _transactionsSum.Text = $"Last 7 days: {Signed(week)}";
        _transactionsSum.AddThemeColorOverride("font_color", week >= 0 ? UiTheme.Good : UiTheme.Bad);
    }

    /// <summary>One block per offer: take it, or the running loan (what is left, next installment, repay).</summary>
    private void RefreshLoans()
    {
        if (_loans is null) return;
        var state = Ctx.Sim.State;
        int level = Ctx.Kpi.Level;
        long day = GameTime.Day(state.Tick);
        string signature = $"{level}|{state.Finance.MoneyCents / 10_000}|{string.Join(",", state.Loans.Select(l => $"{l.Id}:{l.RemainingCents}:{l.NextDueDay}"))}";
        if (signature == _loansSignature) return;
        _loansSignature = signature;
        foreach (var child in _loans.GetChildren()) child.QueueFree();

        foreach (var offer in state.LoanRules.Offers)
        {
            var block = new VBoxContainer();
            block.AddThemeConstantOverride("separation", 0);
            var head = Row(8);
            var loan = state.Loans.FirstOrDefault(l => l.OfferId == offer.Id);
            var name = UiTheme.Label(offer.Name, 13, bold: true);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            head.AddChild(name);
            string detail;
            Color color = UiTheme.TextDim;
            if (loan is not null)
            {
                long days = Math.Max(0, loan.NextDueDay - day);
                detail = $"{UiTheme.Money(loan.RemainingCents)} still owed · next {UiTheme.Money(loan.NextPaymentCents)} " +
                         (days == 0 ? "tonight" : $"in {days + 1} days") + (loan.MissedPayments > 0 ? $" · {loan.MissedPayments} missed" : "");
                if (loan.MissedPayments > 0) color = UiTheme.Warn;
                int id = loan.Id;
                var repay = UiTheme.Button($"Repay {UiTheme.Money(loan.RemainingCents)}", () => Ctx.Host.Enqueue(new RepayLoanCommand(id)),
                    "Pay back everything still owed now (no discount on the interest)");
                repay.Disabled = state.Finance.MoneyCents < loan.RemainingCents;
                head.AddChild(repay);
            }
            else
            {
                string? locked = LoanMath.CannotTake(state, level, offer);
                detail = $"{UiTheme.Money(offer.PrincipalCents)} · {offer.Weeks} × {UiTheme.Money(offer.InstallmentCents)} weekly · " +
                         $"{offer.InterestPermille / 10.0:0.#} % = {UiTheme.Money(offer.TotalRepayCents)}";
                string id = offer.Id;
                var take = UiTheme.Button(state.Rules.UnlockLevel(level) < offer.RequiredLevel ? $"Level {offer.RequiredLevel}" : "Take",
                    () => Ctx.Host.Enqueue(new TakeLoanCommand(id)), locked ?? $"Borrow {UiTheme.Money(offer.PrincipalCents)} now");
                take.Disabled = locked is not null;
                head.AddChild(take);
            }
            block.AddChild(head);
            block.AddChild(UiTheme.Label(detail, 11, color));
            _loans.AddChild(block);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Lines that can never move in this scenario are left out (no shops, no loans, no base staff).</summary>
    private static bool Shown(WorldState state, BudgetLine line) => line.Name switch
    {
        "Merch" => state.FacilityTypes.Any(t => t.Kind == FacilityKind.Shop),
        "Parking & camping" => state.ParkingLots.Count > 0 || state.FacilityTypes.Any(t => t.Kind == FacilityKind.Campground),
        "Bikes" => state.FacilityTypes.Any(t => t.Kind is FacilityKind.Rental or FacilityKind.Service),
        "Sponsors & media" => state.SponsorTypes.Count > 0,
        "Park staff" => state.StaffRules.Enabled || state.Rules.DailyUpkeepCents > 0,
        "Loans" => state.LoanRules.Offers.Count > 0 || state.Loans.Count > 0,
        _ => true,
    };

    /// <summary>The weekly installments of the running loans, per day (the last one may be smaller).</summary>
    private static long LoansPerDay(WorldState state) =>
        state.Loans.Sum(l => Math.Min(l.InstallmentCents, l.RemainingCents)) / LoanMath.DaysPerInstallment;

    private static string Tooltip(WorldState state, BudgetLine line, IReadOnlyList<long>? money)
    {
        var parts = line.Income.Concat(line.Expenses).Select(c => $"{CategoryName(c)} {UiTheme.Money(Ledger.Get(money, c))}");
        if (line.Loans)
            parts = state.Loans.Select(l => $"{l.Name}: {UiTheme.Money(l.InstallmentCents)} a week, {UiTheme.Money(l.RemainingCents)} still owed");
        if (line.Name == "Park staff" && state.StaffRules.Enabled)
            parts = parts.Concat(state.StaffRoles.Where(r => StaffMath.Count(state, r.Id) > 0)
                .Select(r => $"{r.Name} ×{StaffMath.Count(state, r.Id)}: {UiTheme.Money(StaffMath.Count(state, r.Id) * r.WagePerDayCents)}"));
        string body = string.Join(" · ", parts);
        return line.Hint + (body.Length > 0 ? "\n" + body : "");
    }

    private static string CategoryName(MoneyCategory c) => c switch
    {
        MoneyCategory.EntryFees => "entrance fees",
        MoneyCategory.LiftPasses => "lift passes",
        MoneyCategory.Food => "lunch & snacks",
        MoneyCategory.Drinks => "coffee & drinks",
        MoneyCategory.FoodGoods => "food goods",
        MoneyCategory.ParkingFees => "parking fees",
        MoneyCategory.Camping => "camping nights",
        MoneyCategory.CampUpkeep => "campground upkeep",
        MoneyCategory.Rentals => "bike rentals",
        MoneyCategory.Repairs => "repairs",
        MoneyCategory.BikeCosts => "bike parts & upkeep",
        MoneyCategory.Sponsors => "sponsors",
        MoneyCategory.Clips => "video clips",
        MoneyCategory.CameraUpkeep => "camera upkeep",
        MoneyCategory.Merch => "merch",
        MoneyCategory.LiftFees => "bike access fees",
        MoneyCategory.LiftUpkeep => "own lift upkeep",
        MoneyCategory.BarUpkeep => "bar upkeep",
        MoneyCategory.ShopUpkeep => "shop upkeep",
        MoneyCategory.MerchGoods => "goods",
        MoneyCategory.Wages => "wages",
        MoneyCategory.ParkStaff => "park staff",
        _ => c.ToString(),
    };

    private static string Signed(long cents) => cents >= 0 ? $"+{UiTheme.Money(cents)}" : $"−{UiTheme.Money(-cents)}";

    private Button Toggle(string text, ButtonGroup group, bool today, string tooltip)
    {
        var button = new TipButton { Text = text, ToggleMode = true, ButtonGroup = group, FocusMode = FocusModeEnum.None, TooltipText = tooltip };
        button.Pressed += () =>
        {
            _showToday = today;
            Refresh();
        };
        return button;
    }

    private static Label Cell(string text, float width, Color? color = null, int size = 13, bool bold = false,
        HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = UiTheme.Label(text, size, color, bold);
        label.CustomMinimumSize = new Vector2(width, 0);
        label.HorizontalAlignment = align;
        label.VerticalAlignment = VerticalAlignment.Center;
        return label;
    }

    private static VBoxContainer Card(VBoxContainer parent, string caption)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 8, 12, 8));
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        box.AddChild(UiTheme.Label(caption, 11, UiTheme.TextDim, bold: true));
        panel.AddChild(box);
        parent.AddChild(panel);
        return box;
    }
}

/// <summary>One budget bar: a fill scaled to the table's largest value, with the amount written on it.</summary>
public partial class BudgetBar : Control
{
    public Color Color { get; set; } = UiTheme.Good;

    /// <summary>Expenses grow from the right edge towards the labels in the middle, like SimCity's.</summary>
    public bool RightToLeft { get; set; }

    /// <summary>Nothing can ever be booked here (a line without income, say): drawn blank.</summary>
    public bool Empty { get; set; }

    private long _cents, _max = 1;
    private string _suffix = "";

    public BudgetBar() => MouseFilter = MouseFilterEnum.Pass;

    public void Set(long cents, long max, string suffix)
    {
        if (cents == _cents && max == _max && suffix == _suffix) return;
        (_cents, _max, _suffix) = (cents, Math.Max(1, max), suffix);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), UiTheme.Line);
        if (Empty) return;
        float w = (float)Math.Min(1.0, (double)_cents / _max) * size.X;
        if (_cents > 0) w = Math.Max(w, 2);
        var fill = RightToLeft ? new Rect2(size.X - w, 0, w, size.Y) : new Rect2(0, 0, w, size.Y);
        DrawRect(fill, new Color(Color, 0.75f));
        string text = UiTheme.Money(_cents) + _suffix;
        var font = UiTheme.Bold;
        const int fontSize = 12;
        float textWidth = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X;
        float baseline = size.Y / 2 + fontSize * 0.36f;
        // Inside the bar when it fits, else next to it.
        bool inside = w >= textWidth + 10;
        float x = RightToLeft
            ? inside ? size.X - w + 5 : size.X - w - textWidth - 5
            : inside ? w - textWidth - 5 : w + 5;
        x = Math.Clamp(x, 2, size.X - textWidth - 2);
        DrawString(font, new Vector2(x, baseline), text, HorizontalAlignment.Left, -1, fontSize,
            _cents == 0 ? UiTheme.TextDim : UiTheme.Text);
    }
}
