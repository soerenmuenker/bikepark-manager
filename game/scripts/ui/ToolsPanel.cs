using Bikepark.Sim.Commands;
using Bikepark.Sim.Crew;
using Bikepark.Sim.State;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// Tools menu: the tools for sale (each worker on a job uses one tool of the job's kind of work, the best first, and is
/// faster with it), and every tool the park owns on its own row: condition, work days left and who is using it now
/// (<see cref="WorkCosts.ToolAssignments"/>, the same hand-out the crew works with).
/// </summary>
public partial class ToolsPanel : HudPanel
{
    private const float ContentWidth = 660;
    private const int MaxListHeight = 300;

    private HFlowContainer _shop = null!;
    private Label _use = null!;
    private VBoxContainer _owned = null!;
    private string? _shopSignature, _ownedSignature;
    private readonly List<(int ToolId, ProgressBar Bar, Label Condition, Label Status)> _rows = [];

    public override string Title => "Tools";

    protected override void Build()
    {
        var intro = UiTheme.Label("Each worker on a job uses one tool of the job's kind of work (the best free one first) and works faster with it. " +
                                  "Tools wear with every minute worked and are gone when worn out.", 12, UiTheme.TextDim);
        intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        intro.CustomMinimumSize = new Vector2(ContentWidth, 0);
        Body.AddChild(intro);

        Body.AddChild(UiTheme.Label("BUY", 11, UiTheme.TextDim, bold: true));
        _shop = new HFlowContainer { CustomMinimumSize = new Vector2(ContentWidth, 0) };
        _shop.AddThemeConstantOverride("h_separation", 8);
        _shop.AddThemeConstantOverride("v_separation", 8);
        Body.AddChild(_shop);
        Body.AddChild(UiTheme.Separator());

        Body.AddChild(UiTheme.Label("YOUR TOOLS", 11, UiTheme.TextDim, bold: true));
        _use = UiTheme.Label("", 12, UiTheme.TextDim);
        Body.AddChild(_use);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _owned = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _owned.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_owned);
        Body.AddChild(scroll);
    }

    public override void Refresh()
    {
        var state = Ctx.Sim.State;
        string shopSignature = $"{Ctx.Kpi.Level}|{state.Finance.MoneyCents / 50_000}|{string.Join(',', state.Tools.Select(t => t.TypeId))}";
        if (shopSignature != _shopSignature) RebuildShop(state, shopSignature);

        var uses = WorkCosts.ToolAssignments(state);
        _use.Text = string.Join("  ·  ", new[] { WorkType.Digging, WorkType.Carpentry, WorkType.Felling }.Select(type =>
        {
            int busy = uses.Count(u => u.Job.CurrentWorkType == type);
            int tools = WorkCosts.ToolsFor(state, type).Count;
            int byHand = uses.Count(u => u.Job.CurrentWorkType == type && u.Tool is null);
            return $"{WorkWord(type)}: {busy} working, {tools} tool{(tools == 1 ? "" : "s")}" + (byHand > 0 ? $" ({byHand} by hand)" : "");
        }));

        string ownedSignature = string.Join(',', state.Tools.Select(t => t.Id));
        if (ownedSignature != _ownedSignature) RebuildOwned(state);
        int workDay = Math.Max(1, state.Rules.CloseMinute - state.Rules.OpenMinute);
        foreach (var (toolId, bar, condition, status) in _rows)
        {
            if (state.Tools.FirstOrDefault(t => t.Id == toolId) is not { } tool
                || state.ToolTypes.FirstOrDefault(t => t.Id == tool.TypeId) is not { } type) continue;
            int permille = WorkCosts.ConditionPermille(type, tool);
            bar.Value = permille;
            bar.Modulate = permille >= 500 ? UiTheme.Good : permille >= 200 ? UiTheme.Warn : UiTheme.Bad;
            condition.Text = type.LifeMinutes <= 0 ? "lasts forever"
                : $"{permille / 10} % · ~{Math.Max(0, type.LifeMinutes - tool.MinutesUsed) / workDay} work days left";
            var use = uses.FirstOrDefault(u => u.Tool == tool);
            status.Text = use.Tool is not null ? $"In use by {use.Member.Name} — {Jobs.Title(state, use.Job)}" : "Idle";
            status.Modulate = use.Tool is not null ? UiTheme.Good : UiTheme.TextDim;
        }
        var scroll = (ScrollContainer)_owned.GetParent();
        scroll.CustomMinimumSize = new Vector2(ContentWidth, Math.Min(MaxListHeight, Math.Max(24, _owned.GetCombinedMinimumSize().Y)));
    }

    private void RebuildShop(WorldState state, string signature)
    {
        _shopSignature = signature;
        foreach (var child in _shop.GetChildren()) child.QueueFree();
        if (state.ToolTypes.Count == 0)
        {
            _shop.AddChild(UiTheme.Label("No tools for sale in this scenario.", 12, UiTheme.TextDim));
            return;
        }
        int level = Ctx.Kpi.Level;
        int workDay = Math.Max(1, state.Rules.CloseMinute - state.Rules.OpenMinute);
        foreach (var tool in state.ToolTypes.OrderBy(t => t.WorkType).ThenBy(t => t.RequiredLevel).ThenBy(t => t.PriceCents))
        {
            string id = tool.Id;
            int owned = state.Tools.Count(t => t.TypeId == id);
            bool locked = state.Rules.UnlockLevel(level) < tool.RequiredLevel;
            var button = new TipButton
            {
                Text = $"{tool.Name}{(owned > 0 ? $"  ×{owned}" : "")}\n{WorkWord(tool.WorkType)} +{tool.SpeedBonusPermille / 10} % · " +
                       (locked ? $"level {tool.RequiredLevel}" : UiTheme.Money(tool.PriceCents)),
                Disabled = locked || state.Finance.MoneyCents < tool.PriceCents,
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(150, 46),
                TooltipText = locked ? $"Needs park level {tool.RequiredLevel}"
                    : $"Buy one more for {UiTheme.MoneyExact(tool.PriceCents)}" +
                      (tool.LifeMinutes > 0 ? $"; lasts about {tool.LifeMinutes / workDay} full work days" : "") +
                      (owned > 0 ? $". You have {owned}" : ""),
            };
            button.AddThemeFontSizeOverride("font_size", 12);
            button.Pressed += () => Ctx.Host.Enqueue(new BuyToolCommand(id));
            _shop.AddChild(button);
        }
    }

    private void RebuildOwned(WorldState state)
    {
        _ownedSignature = string.Join(',', state.Tools.Select(t => t.Id));
        foreach (var child in _owned.GetChildren()) child.QueueFree();
        _rows.Clear();
        if (state.Tools.Count == 0)
        {
            _owned.AddChild(UiTheme.Label("No tools yet: the crew works by hand. Buy some above.", 13, UiTheme.TextDim));
            return;
        }
        // Best first per kind of work, as they are handed out.
        foreach (var (tool, type) in new[] { WorkType.Digging, WorkType.Carpentry, WorkType.Felling }.SelectMany(t => WorkCosts.ToolsFor(state, t)))
        {
            var row = new PanelContainer();
            var box = UiTheme.Box(UiTheme.Card, 8, 10, 5);
            box.ShadowSize = 0;
            row.AddThemeStyleboxOverride("panel", box);
            var h = Row(10);
            row.AddChild(h);
            h.AddChild(new IconView(type.WorkType == WorkType.Felling ? UiIcon.Felling : type.WorkType == WorkType.Carpentry ? UiIcon.Wood : UiIcon.Tool,
                24, UiTheme.Accent) { SizeFlagsVertical = SizeFlags.ShrinkCenter });
            var texts = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            texts.AddThemeConstantOverride("separation", 1);
            var top = Row(10);
            top.AddChild(UiTheme.Label($"{type.Name} #{tool.Id}", 13, bold: true));
            top.AddChild(UiTheme.Label($"{WorkWord(type.WorkType)} +{type.SpeedBonusPermille / 10} %", 12, UiTheme.TextDim));
            texts.AddChild(top);
            var status = UiTheme.Label("", 12);
            texts.AddChild(status);
            h.AddChild(texts);
            var right = new VBoxContainer { CustomMinimumSize = new Vector2(190, 0), SizeFlagsVertical = SizeFlags.ShrinkCenter };
            var condition = UiTheme.Label("", 11, UiTheme.TextDim);
            right.AddChild(condition);
            var bar = new ProgressBar { MinValue = 0, MaxValue = 1000, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 6) };
            right.AddChild(bar);
            h.AddChild(right);
            _owned.AddChild(row);
            _rows.Add((tool.Id, bar, condition, status));
        }
    }

    private static string WorkWord(WorkType type) => type switch
    {
        WorkType.Digging => "Digging",
        WorkType.Carpentry => "Carpentry",
        _ => "Felling",
    };
}
