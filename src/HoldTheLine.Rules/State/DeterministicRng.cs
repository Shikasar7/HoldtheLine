namespace HoldTheLine.Rules.State;

/// <summary>
/// SplitMix64. The entire RNG is one serializable ulong, which is what makes replays and
/// server/client agreement possible — never use System.Random inside the rules engine.
/// </summary>
public sealed class DeterministicRng
{
    public ulong State { get; set; }

    public DeterministicRng() { }

    public DeterministicRng(ulong seed) => State = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    public ulong NextUInt64()
    {
        State += 0x9E3779B97F4A7C15UL;
        ulong z = State;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public int NextInt(int maxExclusive) =>
        maxExclusive <= 0
            ? throw new ArgumentOutOfRangeException(nameof(maxExclusive))
            : (int)(NextUInt64() % (ulong)maxExclusive);

    public void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = NextInt(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
