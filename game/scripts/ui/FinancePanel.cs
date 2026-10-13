using Bikepark.Sim.Banking;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.State;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// The park's budget, in the spirit of SimCity's: regular income and expenses per area as bars (yesterday's closed day,
/// or today so far), the levers the player can pull (entrance fee, lift pass), loans, one-off transactions (land, lifts,
/// buildings, tools, wood, loans) and the balance. Below: the last days and the bars and shops.
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
        new("Food & drinks", [MoneyCategory.BarUpkeep], [MoneyCategory.Food], "Lunch and snacks sold · upkeep of the bars"),
        new("Merch", [MoneyCategory.ShopUpkeep, MoneyCategory.MerchGoods], [MoneyCategory.Merch], "Merch sold · the goods sold · upkeep of the shops"),
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
    private VBoxContainer? _loans;
    private string _loansSignature = "";
    private VBoxContainer _transactions = null!;
    private Label _transactionsSum = null!;
    private string _transactionsSignature = "";
    private Label _money = null!, _debtStatus = null!;

    private VBoxContainer? _buildings;
    private readonly Dictionary<int, (PanelContainer Row, Label Text)> _buildingRows = [];
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
        BuildLoans(right);
        BuildTransactions(right);
        BuildBalance(right);

        left.AddChild(UiTheme.Separator());
        left.AddChild(UiTheme.Label("LAST DAYS — revenue (green) and expenses (red), one-offs included", 11, UiTheme.TextDim, bold: true));
        _chart = new DayChart { CustomMinimumSize = new Vector2(560, 90) };
        left.AddChild(_chart);
        if (Ctx.Sim.State.FacilityTypes.Count > 0)
        {
            left.AddChild(UiTheme.Label("BARS & SHOPS — built in the Build menu", 11, UiTheme.TextDim, bold: true));
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
            return;
        }
        long entry = Ledger.Get(money, MoneyCategory.EntryFees), passes = Ledger.Get(money, MoneyCategory.LiftPasses);
        _feeDetail.Text = $"Yesterday: {yesterday.Visitors} guests, {UiTheme.Money(entry)}" +
                          (yesterday.TurnedAway > 0 ? $" · {yesterday.TurnedAway} turned away (full)" : "") + " · dearer = fewer guests";
        _liftTicketDetail.Text = pass == 0
            ? $"Free lifts: {yesterday.LiftRides} lift rides yesterday"
            : $"Yesterday: {UiTheme.Money(passes)} (≈ {passes / pass} passes) · too dear and guests pedal up";
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

    /// <summary>One line per bar and shop: what it served and earned.</summary>
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
                var text = UiTheme.Label("", 12);
                row.AddChild(text);
                _buildings.AddChild(row);
                _buildingRows[facility.Id] = (row, text);
            }
            if (state.Facilities.Count == 0)
                _buildings.AddChild(UiTheme.Label("None yet: without a bar guests bring a packed lunch and buy no snacks.", 12, UiTheme.Warn));
        }
        foreach (var facility in state.Facilities)
        {
            var type = FacilityMath.FindType(state, facility.TypeId);
            string lift = state.Lifts.FirstOrDefault(l => l.Id == facility.LiftId)?.Name ?? "?";
            string busy = type?.Kind == FacilityKind.Bar
                ? $"{FacilityMath.SeatsTaken(state, facility)}/{type.Seats} seats · {facility.Customers} lunches"
                : $"{facility.Customers} sold";
            _buildingRows[facility.Id].Text.Text = $"{facility.Name} (at {lift}) · {busy} · today {UiTheme.Money(facility.RevenueTodayCents)} · " +
                                                    $"total {UiTheme.Money(facility.TotalRevenueCents)} · upkeep {UiTheme.Money(type?.UpkeepPerDayCents ?? 0)}/day";
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Lines that can never move in this scenario are left out (no shops, no loans, no base staff).</summary>
    private static bool Shown(WorldState state, BudgetLine line) => line.Name switch
    {
        "Merch" => state.FacilityTypes.Any(t => t.Kind == FacilityKind.Shop),
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
