using Bikepark.Sim.Core;

namespace Bikepark.Sim.Tests;

public class SimRandomTests
{
    [Fact]
    public void MatchesPcg32ReferenceSequence()
    {
        // Expected values from the PCG reference implementation (pcg32-demo, seed 42, stream 54).
        var rng = SimRandom.FromSeed(42, 54);
        uint[] expected = [0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e];

        foreach (uint value in expected)
            Assert.Equal(value, rng.NextUInt());
    }

    [Fact]
    public void RestoringState_ContinuesSequence()
    {
        var rng = SimRandom.FromSeed(123);
        for (int i = 0; i < 10; i++) rng.NextUInt();

        var copy = new SimRandom { State = rng.State, Increment = rng.Increment };

        for (int i = 0; i < 100; i++)
            Assert.Equal(rng.NextUInt(), copy.NextUInt());
    }

    [Fact]
    public void Range_StaysInBounds()
    {
        var rng = SimRandom.FromSeed(7);
        for (int i = 0; i < 10_000; i++)
        {
            int v = rng.Range(-3, 4);
            Assert.InRange(v, -3, 3);
        }
    }

    [Fact]
    public void ChancePermille_HandlesEdges()
    {
        var rng = SimRandom.FromSeed(7);
        Assert.False(rng.ChancePermille(0));
        Assert.True(rng.ChancePermille(1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(0));
    }
}
