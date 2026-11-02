using Bikepark.Sim.Commands;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

/// <summary>A scenario's real bridges: level decks, the ground meeting them at the ends, other ways passing under.</summary>
public class DeckBridgeTests
{
    private static readonly PointCm A = new(20_000, 30_000), B = new(60_000, 50_000);

    private static Simulation World(params ICommand[] commands)
    {
        var sim = new Simulation(TestWorlds.Create());
        foreach (var command in commands) sim.Commands.Enqueue(command);
        sim.Step();
        return sim;
    }

    private static BuildWayCommand Deck(PointCm? from = null, PointCm? to = null, WayOrigin origin = WayOrigin.Scenario) =>
        new(WayKind.AccessPath, "", [from ?? A, to ?? B], origin, Surface: WaySurface.Tarmac, DeckWidthCm: 800);

    private static Way Bridge(Simulation sim) => sim.State.Ways.Single(w => w.DeckWidthCm > 0);

    [Fact]
    public void Deck_IsLevel_AtTheMiddleOfItsEnds()
    {
        var bare = World().Terrain;
        var sim = World(Deck());

        var g = sim.Network.Geometry(Bridge(sim).Id);

        Assert.True(g.IsDeck);
        Assert.Single(g.Ys.ToArray().Distinct());
        Assert.Equal((bare.HeightAt(A.X, A.Z) + bare.HeightAt(B.X, B.Z)) / 2, g.Ys[0]);
    }

    [Fact]
    public void Ground_MeetsTheDeck_AtBothEnds()
    {
        var sim = World(Deck());
        var g = sim.Network.Geometry(Bridge(sim).Id);

        Assert.Equal(g.Ys[0], sim.Terrain.HeightAt(g.Xs[0], g.Zs[0]));
        Assert.Equal(g.Ys[0], sim.Terrain.HeightAt(g.Xs[^1], g.Zs[^1]));
        Assert.Equal(2, sim.State.TerrainEdits.Count(e => e.OwnerId == Bridge(sim).Id));
    }

    [Fact]
    public void Ground_InTheMiddle_StaysAsItIs()
    {
        var bare = World().Terrain;
        var sim = World(Deck());
        var (x, z) = ((A.X + B.X) / 2, (A.Z + B.Z) / 2);

        Assert.Equal(bare.HeightAt(x, z), sim.Terrain.HeightAt(x, z));
    }

    [Fact]
    public void Way_UnderTheDeck_DoesNotCross()
    {
        var sim = World(Deck(), new BuildWayCommand(WayKind.AccessPath, "", [new(20_000, 50_000), new(60_000, 30_000)], WayOrigin.Scenario));

        Assert.Equal(2, sim.State.Ways.Count);
        Assert.Empty(sim.Network.CrossingsOn(Bridge(sim).Id));
    }

    [Fact]
    public void Way_EndingUnderTheDeck_DoesNotJoinIt()
    {
        var middle = new PointCm((A.X + B.X) / 2, (A.Z + B.Z) / 2);
        var sim = World(Deck(), new BuildWayCommand(WayKind.AccessPath, "", [new(20_000, 60_000), middle], WayOrigin.Scenario));

        var other = sim.State.Ways.Single(w => w.DeckWidthCm == 0);
        Assert.Null(other.StartJoin);
        Assert.Null(other.EndJoin);
    }

    [Fact]
    public void Way_EndingAtTheDecksEnd_JoinsIt()
    {
        var sim = World(Deck(), new BuildWayCommand(WayKind.AccessPath, "", [new(5_000, 30_000), new(A.X - 100, A.Z)], WayOrigin.Scenario));

        var other = sim.State.Ways.Single(w => w.DeckWidthCm == 0);
        Assert.Equal(Bridge(sim).Id, (other.StartJoin ?? other.EndJoin)?.WayId);
    }

    [Fact]
    public void Trees_UnderTheDeck_Stand()
    {
        var sim = World(Deck());
        var (x, z) = ((A.X + B.X) / 2, (A.Z + B.Z) / 2);

        Assert.False(sim.Network.IsInCorridor(x, z));
    }

    [Fact]
    public void Player_CannotBuildOne()
    {
        var sim = World(Deck(origin: WayOrigin.Player));

        Assert.Empty(sim.State.Ways);
    }

    [Fact]
    public void Bridge_CannotBeRenaturalized()
    {
        var sim = World(Deck());
        var bridge = Bridge(sim);

        Assert.NotNull(WayEditing.CannotRenaturalize(sim.State, sim.Network, bridge, 1_000, 5_000));
    }

    [Fact]
    public void Bridge_SurvivesASave()
    {
        var sim = World(Deck());

        var loaded = SaveGame.Deserialize(SaveGame.Serialize(sim.State));

        Assert.Equal(800, loaded.Ways.Single().DeckWidthCm);
    }
}
