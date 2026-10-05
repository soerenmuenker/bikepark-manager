using Bikepark.Sim;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Reporting;
using Godot;

namespace Bikepark.Game;

/// <summary>
/// Placeholder debug HUD: shows KPIs, a short event log, and issues commands. Pure view; never writes to WorldState.
/// </summary>
public partial class Hud : CanvasLayer
{
    private const int FeeStepCents = 250;
    private const int MaxLogLines = 8;

    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";

    private readonly Queue<string> _log = new();
    private readonly List<IDisposable> _subscriptions = [];
    private SimHost _host = null!;
    private Label _stats = null!;
    private Label _logLabel = null!;

    public override void _Ready()
    {
        _host = GetNode<SimHost>(SimHostPath);
        BuildUi();
        _host.SimulationReplaced += Subscribe;
        Subscribe(_host.Sim);
    }

    public override void _ExitTree()
    {
        _host.SimulationReplaced -= Subscribe;
        Unsubscribe();
    }

    public override void _Process(double delta)
    {
        var state = _host.Sim.State;
        var kpi = KpiReport.From(state, includeHash: false);
        _stats.Text =
            $"{kpi.ParkName}   {GameTime.Format(state.Tick)}   speed {_host.Speed}x\n" +
            $"Money {Money(kpi.MoneyCents)}   Entry fee {Money(kpi.EntryFeeCents)}\n" +
            $"Guests in park {kpi.GuestsInPark}   happiness {kpi.AverageHappiness / 10}%   " +
            $"(exit avg {kpi.AverageExitHappiness / 10}%)\n" +
            $"Visitors {kpi.TotalVisitors}   turned away {kpi.TotalTurnedAway}   left unhappy {kpi.TotalLeftUnhappy}";
    }

    private void Subscribe(Simulation sim)
    {
        Unsubscribe();
        _subscriptions.Add(sim.Events.Subscribe<DayEnded>(e =>
            Log($"Day {e.Report.Day + 1} closed: {e.Report.Visitors} visitors, net {Money(e.Report.RevenueCents - e.Report.ExpensesCents)}")));
        _subscriptions.Add(sim.Events.Subscribe<ParkOpened>(e => Log($"{GameTime.Format(e.Tick)} park opened")));
        _subscriptions.Add(sim.Events.Subscribe<ParkClosed>(e => Log($"{GameTime.Format(e.Tick)} park closed")));
        _subscriptions.Add(sim.Events.Subscribe<CommandRejected>(e => Log($"Rejected: {e.Reason}")));
    }

    private void Unsubscribe()
    {
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
    }

    private void Log(string line)
    {
        GD.Print(line);
        _log.Enqueue(line);
        while (_log.Count > MaxLogLines)
            _log.Dequeue();
        _logLabel.Text = string.Join('\n', _log);
    }

    private void BuildUi()
    {
        var root = new VBoxContainer { Position = new Vector2(16, 16) };
        AddChild(root);

        _stats = new Label();
        root.AddChild(_stats);

        var speedRow = new HBoxContainer();
        root.AddChild(speedRow);
        for (int i = 0; i < SimHost.SpeedMultipliers.Length; i++)
        {
            int index = i;
            int speed = SimHost.SpeedMultipliers[i];
            AddButton(speedRow, speed == 0 ? "Pause" : $"{speed}x", () => _host.SetSpeedIndex(index));
        }

        var actionRow = new HBoxContainer();
        root.AddChild(actionRow);
        AddButton(actionRow, "Fee -", () => ChangeFee(-FeeStepCents));
        AddButton(actionRow, "Fee +", () => ChangeFee(FeeStepCents));
        AddButton(actionRow, "Save", _host.Save);
        AddButton(actionRow, "Load", () => Log(_host.Load() ? "Loaded save." : "No save found."));

        _logLabel = new Label();
        root.AddChild(_logLabel);
    }

    private void ChangeFee(long deltaCents) =>
        _host.Enqueue(new SetEntryFeeCommand(_host.Sim.State.Park.EntryFeeCents + deltaCents));

    private static void AddButton(Container parent, string text, Action onPressed)
    {
        var button = new Button { Text = text };
        button.Pressed += onPressed;
        parent.AddChild(button);
    }

    private static string Money(long cents) => $"{cents / 100.0:N2} €";
}
