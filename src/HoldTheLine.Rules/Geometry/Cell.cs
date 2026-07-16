namespace HoldTheLine.Rules.Geometry;

/// <summary>A board coordinate. Col 0..4 (west→east), Row 0..3 (seat 0's home row is Row 0, seat 1's is Row 3).</summary>
public readonly record struct Cell(int Col, int Row)
{
    public override string ToString() => $"({Col},{Row})";
}
