using Bikepark.Sim.Commands;
using Bikepark.Sim.Events;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

/// <summary>How a Starter Valley career begins: the unfinished blue Easy Peasy and the welcome.</summary>
public class CareerStartTests
{
    private const int EasyPeasy = 17;

    private static Simulation Started(IEnumerable<TimedCommand>? commands = null)
    {
        var sim = new Simulation(ScenarioLoader.CreateWorld(TestWorlds.CareerScenario()));
        foreach (var c in commands ?? [])
            sim.Commands.Enqueue(c.Command, c.Tick);
        sim.Step();
        return sim;
    }

    [Fact]
    public void EasyPeasy_IsABlueTrail_ThreeQuartersBuilt_EndingLoose_AndClosed()
    {
        var sim = Started();
        Assert.DoesNotContain(sim.Events.Pending, e => e is CommandRejected);
        var way = sim.State.Ways.Single(w => w.Name == "Easy Peasy");
        Assert.Equal(EasyPeasy, way.Id);
        Assert.True(way.Built);
        Assert.Equal(WayKind.Trail, way.Kind);
        Assert.Equal(TrailRating.Blue, sim.Network.Geometry(way.Id).Rating);

        // It starts at the T-bar's top station and ends loose, three quarters down the hill: nobody can ride it.
        Assert.NotEqual(0, way.StartHubId);
        Assert.Equal(0, way.EndHubId);
        Assert.Null(way.EndJoin);
        Assert.False(sim.Network.IsConnected(way));
        Assert.False(sim.Network.IsRideable(way));
        var renaturalized = sim.Events.Pending.OfType<TrailRenaturalized>().Single();
        long full = renaturalized.ToCm;
        long left = sim.Network.Geometry(way.Id).LengthCm;
        Assert.InRange(left * 1000 / full, 700, 800);
    }

    [Fact]
    public void FinishingEasyPeasy_FromItsLooseEnd_MakesItOneRideableTrail()
    {
        // The career opening's last stretch, built at once.
        var finish = TestWorlds.CareerOpening().Select(c => c.Command).OfType<BuildWayCommand>().First(b => b.Name == "");
        var sim = Started();
        long before = sim.Network.Geometry(EasyPeasy).LengthCm;
        sim.Commands.Enqueue(finish with { Instant = true });
        sim.Step();

        Assert.DoesNotContain(sim.Events.Pending, e => e is CommandRejected);
        var way = Assert.Single(sim.State.Ways, w => w.Kind == WayKind.Trail);
        Assert.Equal("Easy Peasy", way.Name);
        Assert.True(sim.Network.IsRideable(way));
        Assert.True(sim.Network.Geometry(way.Id).LengthCm > before);
        Assert.Equal(TrailRating.Blue, sim.Network.Geometry(way.Id).Rating);
    }

    [Fact]
    public void Welcome_IsCopiedIntoTheWorld_AndNamesTheTrailToFinish()
    {
        var sim = Started();
        var welcome = sim.State.Welcome;
        Assert.NotNull(welcome);
        Assert.Equal($"Welcome to {sim.State.Park.Name}!", WelcomeNote.Fill(welcome.Title, sim.State.Park.Name));
        Assert.Contains(welcome.Paragraphs, p => p.Contains("Easy Peasy"));
        Assert.Contains(sim.State.Ways, w => w.Name == welcome.FocusTrail);

        // Saves keep it; the Demo has none.
        Assert.Equal(welcome.Title, SaveGame.Deserialize(SaveGame.Serialize(sim.State)).Welcome?.Title);
        Assert.Null(ScenarioLoader.CreateWorld(TestWorlds.LiftScenario()).Welcome);
    }

    [Fact]
    public void Welcome_MustHaveATitleAndText()
    {
        var scenario = TestWorlds.CareerScenario();
        scenario.Welcome = new WelcomeNote { Title = "Hi", Paragraphs = [] };
        var error = Assert.Throws<InvalidDataException>(() => ScenarioLoader.CreateWorld(scenario));
        Assert.Contains("welcome: paragraphs", error.Message);
    }
}
