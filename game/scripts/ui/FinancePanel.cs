using Bikepark.Sim.Banking;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.Sponsors;
using Bikepark.Sim.State;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// The park's budget, in the spirit of SimCity's: regular income and expenses per area as bars (yesterday's closed day,
/// or today so far), the levers the player can pull (entrance fee, lift pass, product prices, beer hours), loans, one-off
/// transactions (land, lifts, buildings, tools, wood, loans) and the balance. Below: the last days and the bars and shops
/// (with their upgrades).
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
        new("Park staff", [MoneyCategory.ParkStaff], [], "Ticket office, first aid and cleaning: every day, open or not"),
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

    private Label _fee = null!, _feeDetail = null!, _liftTicket = null!, _liftTicketDetail = null!;
    private Label? _parking, _parkingDetail;
    private VBoxContainer? _sponsors;
    private string _sponsorsSignature = "";
    private VBoxContainer? _loans;
    private string _loansSignature = "";
    private VBoxContainer _transactions = null!;
    private Label _transactionsSum = null!;
    private string _transactionsSignature = "";
    private Label _money = null!, _debtStatus = null!;

    private readonly List<(ProductType Product, Label Price, Label Detail)> _prices = [];
    private Button? _beerHours;
    private Label? _beerHoursDetail;

    private VBoxContainer? _buildings;
    private readonly Dictionary<int, (PanelContainer Row, Label Text, Button Upgrade, Button Bikes)> _buildingRows = [];

    /// <summary>Bikes bought for a rental fleet with one click.</summary>
    private const int BikeBatch = 5;
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
        BuildLevers(right);
        BuildPrices(right);
        BuildSponsors(right);
        BuildLoans(right);
        BuildTransactions(right);
        BuildBalance(right);

        left.AddChild(UiTheme.Separator());
        left.AddChild(UiTheme.Label("LAST DAYS — revenue (green) and expenses (red), one-offs included", 11, UiTheme.TextDim, bold: true));
        _chart = new DayChart { CustomMinimumSize = new Vector2(560, 90) };
        left.AddChild(_chart);
        if (Ctx.Sim.State.FacilityTypes.Count > 0)
        {
            left.AddChild(UiTheme.Label("BUILDINGS — built in the Build menu; upgrades and rental bikes here", 11, UiTheme.TextDim, bold: true));
            _buildings = new VBoxContainer();
            _buildings.AddThemeConstantOverride("separation", 2);
            left.AddChild(_buildings);
        }
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

    private void BuildLevers(VBoxContainer right)
    {
        var card = Card(right, "LEVERS — your prices");
        var fee = Row(8);
        fee.AddChild(Cell("Entrance", 80, UiTheme.Text, 13, bold: true));
        fee.AddChild(UiTheme.Button("−", () => Ctx.ChangeFee(-HudContext.FeeStepCents), "Lower the entrance fee (more guests come)"));
        _fee = Cell("", 84, UiTheme.Text, 17, bold: true, align: HorizontalAlignment.Center);
        fee.AddChild(_fee);
        fee.AddChild(UiTheme.Button("+", () => Ctx.ChangeFee(HudContext.FeeStepCents), "Raise the entrance fee (fewer guests come)"));
        card.AddChild(fee);
        _feeDetail = UiTheme.Label("", 11, UiTheme.TextDim);
        card.AddChild(_feeDetail);

        var ticket = Row(8);
        ticket.AddChild(Cell("Lift pass", 80, UiTheme.Text, 13, bold: true));
        ticket.AddChild(UiTheme.Button("−", () => Ctx.ChangeLiftTicket(-HudContext.FeeStepCents), "Lower the lift day pass"));
        _liftTicket = Cell("", 84, UiTheme.Text, 17, bold: true, align: HorizontalAlignment.Center);
        ticket.AddChild(_liftTicket);
        ticket.AddChild(UiTheme.Button("+", () => Ctx.ChangeLiftTicket(HudContext.FeeStepCents),
            "Raise the lift day pass (paid once per visit by guests who use a lift; too dear and they pedal instead)"));
        card.AddChild(ticket);
        _liftTicketDetail = UiTheme.Label("", 11, UiTheme.TextDim);
        card.AddChild(_liftTicketDetail);

        if (Ctx.Sim.State.Rules.MaxParkingFeeCents <= 0) return;
        var parking = Row(8);
        parking.AddChild(Cell("Parking", 80, UiTheme.Text, 13, bold: true));
        parking.AddChild(UiTheme.Button("−", () => Ctx.ChangeParkingFee(-HudContext.FeeStepCents), "Lower the parking fee"));
        _parking = Cell("", 84, UiTheme.Text, 17, bold: true, align: HorizontalAlignment.Center);
        parking.AddChild(_parking);
        parking.AddChild(UiTheme.Button("+", () => Ctx.ChangeParkingFee(HudContext.FeeStepCents),
            "Raise the parking fee (per car, shared by its guests and counted with the entrance fee: dearer = fewer guests; campers park free)"));
        card.AddChild(parking);
        _parkingDetail = UiTheme.Label("", 11, UiTheme.TextDim);
        card.AddChild(_parkingDetail);
    }

    /// <summary>One price per product (all bars and shops sell at it) and when riders may drink.</summary>
    private void BuildPrices(VBoxContainer right)
    {
        var state = Ctx.Sim.State;
        if (state.ProductTypes.Count == 0) return;
        var card = Card(right, "PRICES — in all bars and shops");
        foreach (var product in state.ProductTypes)
        {
            var row = Row(6);
            row.AddChild(Cell(product.Name, 80, UiTheme.Text, 13, bold: true));
            var p = product;
            row.AddChild(UiTheme.Button("−", () => ChangePrice(p, -PriceStep(p)), $"Cheaper {p.Name.ToLowerInvariant()}: more guests buy it"));
            var price = Cell("", 64, UiTheme.Text, 14, bold: true, align: HorizontalAlignment.Center);
            row.AddChild(price);
            row.AddChild(UiTheme.Button("+", () => ChangePrice(p, PriceStep(p)),
                $"Dearer {p.Name.ToLowerInvariant()}: fewer buy it, and above the fair price guests feel ripped off"));
            var detail = Cell("", 0, UiTheme.TextDim, 11);
            detail.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(detail);
            row.TooltipText = ProductTooltip(p);
            card.AddChild(row);
            _prices.Add((p, price, detail));
        }
        if (!state.ProductTypes.Any(p => p.Alcohol > 0)) return;
        var beer = Row(6);
        beer.AddChild(Cell("Beer for riders", 110, UiTheme.Text, 13, bold: true));
        _beerHours = UiTheme.Button("", CycleBeerHours,
            "When bars serve alcohol to riders (tipsy riders pick harder trails and crash more). After their last lap guests may always " +
            "stay for après-bike.");
        beer.AddChild(_beerHours);
        card.AddChild(beer);
        _beerHoursDetail = UiTheme.Label("", 11, UiTheme.TextDim);
        card.AddChild(_beerHoursDetail);
    }

    private static long PriceStep(ProductType product) => product.ReferencePriceCents < 1000 ? 50 : 100;

    private void ChangePrice(ProductType product, long delta)
    {
        long price = Math.Clamp(FacilityMath.PriceOf(Ctx.Sim.State, product) + delta, 0, product.MaxPriceCents);
        Ctx.Host.Enqueue(new SetProductPriceCommand(product.Id, price));
    }

    /// <summary>All day → from 15:00 → after closing → never → all day.</summary>
    private void CycleBeerHours()
    {
        var state = Ctx.Sim.State;
        int[] options = [0, 15 * GameTime.MinutesPerHour, state.Rules.CloseMinute, GameTime.MinutesPerDay];
        int index = Array.IndexOf(options, state.Park.BeerFromMinute);
        Ctx.Host.Enqueue(new SetBeerHoursCommand(options[(index + 1) % options.Length]));
    }

    private static string ProductTooltip(ProductType p)
    {
        var effects = new List<string>();
        if (p.Lunch) effects.Add($"lunch: a seat for {p.MinMinutes}–{p.MaxMinutes} min");
        if (p.Merch) effects.Add("merch: guests going home buy it at a shop");
        if (p.Energy > 0) effects.Add($"+{p.Energy / 10} % energy");
        if (p.Happiness > 0) effects.Add($"+{p.Happiness / 10} % mood");
        if (p.CaffeineMinutes > 0) effects.Add($"climbing costs half the energy for {p.CaffeineMinutes} min");
        if (p.Alcohol > 0) effects.Add("alcohol: bolder trail choices, more crashes");
        return $"{p.Name}: fair price {UiTheme.MoneyExact(p.ReferencePriceCents)}, at most {UiTheme.MoneyExact(p.MaxPriceCents)} · " +
               $"goods cost {p.GoodsCostPermille / 10} % of the price\n{string.Join(" · ", effects)}";
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
            row.TooltipText = Tooltip(state, line, money);
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
        RefreshLevers(state, yesterday);
        RefreshPrices(state);
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

        RefreshBuildings();
        _chart.Days = Ctx.Days;
        _chart.QueueRedraw();
    }

    /// <summary>Key: a bar's or shop's id.</summary>
    public override void Focus(string key)
    {
        Refresh();
        if (int.TryParse(key, out int id) && _buildingRows.TryGetValue(id, out var row)) Highlight(row.Row);
    }

    /// <summary>A short status, like a SimCity advisor: how yesterday went, the biggest cost, debt.</summary>
    private string Advice(WorldState state, DayReport? yesterday, long loansPerDay)
    {
        var f = state.Finance;
        if (state.Park.Bankrupt) return "The park is bankrupt: the bank has closed it.";
        string debt = f.DaysInDebt > 0 && state.Rules.BankruptcyDays > 0
            ? $" You are in the red ({f.DaysInDebt} of {state.Rules.BankruptcyDays} days until bankruptcy): take a loan, pull the levers or cut costs."
            : f.MoneyCents < 0 ? " You are in the red: take a loan, pull the levers or cut costs." : "";
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

    private void RefreshLevers(WorldState state, DayReport? yesterday)
    {
        long fee = state.Park.EntryFeeCents, pass = state.Park.LiftTicketCents;
        _fee.Text = UiTheme.MoneyExact(fee);
        _liftTicket.Text = pass == 0 ? "free" : UiTheme.MoneyExact(pass);
        if (yesterday?.ByCategory is not { } money)
        {
            _feeDetail.Text = "A dearer entrance means fewer guests.";
            _liftTicketDetail.Text = "Bought once per visit, when a guest first takes a lift.";
            RefreshParking(state, null);
            return;
        }
        long entry = Ledger.Get(money, MoneyCategory.EntryFees), passes = Ledger.Get(money, MoneyCategory.LiftPasses);
        _feeDetail.Text = $"Yesterday: {yesterday.Visitors} guests, {UiTheme.Money(entry)}" +
                          (yesterday.TurnedAway > 0 ? $" · {yesterday.TurnedAway} turned away (full)" : "") + " · dearer = fewer guests";
        _liftTicketDetail.Text = pass == 0
            ? $"Free lifts: {yesterday.LiftRides} lift rides yesterday"
            : $"Yesterday: {UiTheme.Money(passes)} (≈ {passes / pass} passes) · too dear and guests pedal up";
        RefreshParking(state, Ledger.Get(money, MoneyCategory.ParkingFees));
    }

    private void RefreshParking(WorldState state, long? yesterdayCents)
    {
        if (_parking is null || _parkingDetail is null) return;
        long fee = state.Park.ParkingFeeCents;
        _parking.Text = fee == 0 ? "free" : UiTheme.MoneyExact(fee);
        _parkingDetail.Text = state.ParkingLots.Count == 0 ? "No parking lots: nobody pays it."
            : yesterdayCents is { } cents ? $"Yesterday: {UiTheme.Money(cents)} · per car, counted with the entrance fee"
            : "Per car, shared by its guests and counted with the entrance fee.";
    }

    private void RefreshPrices(WorldState state)
    {
        foreach (var (product, price, detail) in _prices)
        {
            long cents = FacilityMath.PriceOf(state, product);
            price.Text = UiTheme.MoneyExact(cents);
            int factor = FacilityMath.BuyFactorPermille(product, cents);
            var sales = state.Finance.ProductSales.FirstOrDefault(s => s.ProductId == product.Id);
            string demand = cents > product.ReferencePriceCents ? $"{(1000 - factor) / 10} % fewer buy · rip-off"
                : cents < product.ReferencePriceCents ? $"+{(factor - 1000) / 10} % buy" : "fair";
            detail.Text = $"{demand} · {sales?.SoldToday ?? 0} sold today";
            detail.AddThemeColorOverride("font_color", cents > product.ReferencePriceCents ? UiTheme.Warn : UiTheme.TextDim);
        }
        if (_beerHours is null || _beerHoursDetail is null) return;
        int from = state.Park.BeerFromMinute;
        _beerHours.Text = from <= 0 ? "all day" : from >= GameTime.MinutesPerDay ? "never"
            : from >= state.Rules.CloseMinute ? "après-bike only" : $"from {from / 60:00}:{from % 60:00}";
        _beerHoursDetail.Text = $"Crashes after a drink: {state.Safety.CrashesUnderInfluence} · après-bike guests: {state.Stats.TotalApresGuests}";
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

    /// <summary>One line per bar and shop: what it sells and earned, and its next upgrade.</summary>
    private void RefreshBuildings()
    {
        if (_buildings is null) return;
        var state = Ctx.Sim.State;
        if (_buildingRows.Count != state.Facilities.Count || state.Facilities.Any(f => !_buildingRows.ContainsKey(f.Id))
            || _buildings.GetChildCount() == 0)
        {
            foreach (var child in _buildings.GetChildren()) child.QueueFree();
            _buildingRows.Clear();
            foreach (var facility in state.Facilities)
            {
                var row = new PanelContainer();
                row.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 6, 8, 4));
                var line = Row(8);
                var text = UiTheme.Label("", 12);
                text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                line.AddChild(text);
                int id = facility.Id;
                var bikes = UiTheme.Button("", () =>
                {
                    var station = Ctx.Sim.State.Facilities.FirstOrDefault(f => f.Id == id);
                    if (station is null || FacilityMath.FindType(Ctx.Sim.State, station.TypeId) is not { } type) return;
                    int count = Math.Clamp(FacilityMath.MaxFleet(type, station) - station.Fleet.Count, 1, BikeBatch);
                    Ctx.Host.Enqueue(new BuyRentalBikesCommand(id, count));
                });
                bikes.Visible = false;
                line.AddChild(bikes);
                var upgrade = UiTheme.Button("", () => Ctx.Host.Enqueue(new UpgradeFacilityCommand(id)));
                line.AddChild(upgrade);
                row.AddChild(line);
                _buildings.AddChild(row);
                _buildingRows[facility.Id] = (row, text, upgrade, bikes);
            }
            if (state.Facilities.Count == 0)
                _buildings.AddChild(UiTheme.Label("None yet: without a bar guests bring a packed lunch and buy no snacks.", 12, UiTheme.Warn));
        }
        int level = Ctx.Kpi.Level;
        foreach (var facility in state.Facilities)
        {
            var (_, text, button, bikesButton) = _buildingRows[facility.Id];
            if (FacilityMath.FindType(state, facility.TypeId) is not { } type) continue;
            string sells = string.Join(", ", FacilityMath.ProductsOf(state, type, facility).Select(p => p.Name.ToLowerInvariant()));
            string busy = type.Kind switch
            {
                FacilityKind.Bar => $"{FacilityMath.SeatsTaken(state, facility)}/{FacilityMath.Seats(type, facility)} seats",
                FacilityKind.Campground => $"{FacilityMath.PitchesTaken(state, facility)}/{FacilityMath.Pitches(type, facility)} pitches booked",
                FacilityKind.Rental => facility.Fleet.Count == 0 ? "no bikes yet: buy some"
                    : $"{facility.Fleet.Count(b => b.RentedBy == 0)}/{facility.Fleet.Count} bikes free (rack {FacilityMath.MaxFleet(type, facility)}) · " +
                      $"condition {facility.Fleet.Average(b => b.ConditionPermille) / 10:0} %" + (FacilityMath.EBikes(type, facility) ? " · e-bikes" : ""),
                FacilityKind.Service => $"{FacilityMath.SeatsTaken(state, facility)}/{FacilityMath.Seats(type, facility)} workbenches busy",
                _ => $"{FacilityMath.BuyChancePermille(type, facility) / 10} % of happy guests buy",
            };
            string upgrades = facility.Level == 0 ? "" : $" ({string.Join(", ", FacilityMath.Bought(type, facility).Select(u => u.Name.ToLowerInvariant()))})";
            text.Text = $"{facility.Name}{upgrades} · {sells} · {busy} · {facility.Customers} sold · today {UiTheme.Money(facility.RevenueTodayCents)} · " +
                        $"total {UiTheme.Money(facility.TotalRevenueCents)} · upkeep {UiTheme.Money(FacilityMath.Upkeep(type, facility))}/day";
            if (type.Kind == FacilityKind.Rental)
            {
                int room = FacilityMath.MaxFleet(type, facility) - facility.Fleet.Count;
                int count = Math.Clamp(room, 1, BikeBatch);
                string? cannot = FacilityMath.CannotBuyBikes(state, facility, count);
                bikesButton.Visible = true;
                bikesButton.Text = room <= 0 ? "Rack full" : $"+{count} bikes {UiTheme.Money(count * type.BikeCostCents)}";
                bikesButton.Disabled = cannot is not null;
                bikesButton.TooltipText = cannot ?? $"Buy {count} rental bikes ({UiTheme.Money(type.BikeCostCents)} each): worn bikes are retired at 0 %";
            }
            if (FacilityMath.NextUpgrade(type, facility) is not { } next)
            {
                button.Visible = false;
                continue;
            }
            string? locked = FacilityMath.CannotUpgrade(state, level, type, facility);
            button.Visible = true;
            button.Text = state.Rules.UnlockLevel(level) < next.RequiredLevel ? $"{next.Name}: level {next.RequiredLevel}"
                : $"{next.Name} {UiTheme.Money(next.CostCents)}";
            button.Disabled = locked is not null;
            button.TooltipText = (locked is null ? "" : locked + "\n") +
                                 $"{next.Name}: {next.Description} · +{UiTheme.Money(next.UpkeepPerDayCents)}/day upkeep";
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
        "Park staff" => state.Rules.DailyUpkeepCents > 0,
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
        MoneyCategory.ParkStaff => "base staff",
        _ => c.ToString(),
    };

    private static string Signed(long cents) => cents >= 0 ? $"+{UiTheme.Money(cents)}" : $"−{UiTheme.Money(-cents)}";

    private Button Toggle(string text, ButtonGroup group, bool today, string tooltip)
    {
        var button = new Button { Text = text, ToggleMode = true, ButtonGroup = group, FocusMode = FocusModeEnum.None, TooltipText = tooltip };
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
