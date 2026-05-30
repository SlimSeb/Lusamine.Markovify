using Lusamine.Markovify;

namespace Lusamine.Markovify.Tests;

public class StateTests
{
    [Fact]
    public void EqualStates_AreEqual_AndShareHashCode()
    {
        var a = new State(["the", "quick"]);
        var b = new State(["the", "quick"]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void DifferentStates_AreNotEqual()
    {
        var a = new State(["the", "quick"]);
        var b = new State(["the", "slow"]);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void StatesOfDifferentLength_AreNotEqual()
    {
        Assert.NotEqual(new State(["a"]), new State(["a", "b"]));
    }

    [Fact]
    public void Shift_DropsOldestAndAppendsNewest()
    {
        var state = new State(["the", "quick", "brown"]);

        var shifted = state.Shift("fox");

        Assert.Equal(["quick", "brown", "fox"], shifted.Words);
        // Original is untouched (immutability).
        Assert.Equal(["the", "quick", "brown"], state.Words);
    }

    [Fact]
    public void State_WorksAsDictionaryKey()
    {
        var map = new Dictionary<State, int>
        {
            [new State(["a", "b"])] = 1,
        };

        Assert.True(map.ContainsKey(new State(["a", "b"])));
        Assert.False(map.ContainsKey(new State(["a", "c"])));
    }
}
