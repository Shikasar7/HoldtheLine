using HoldTheLine.Rules.Geometry;
using Xunit;

namespace HoldTheLine.Rules.Tests;

public class GeometryTests
{
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(4, 3, true)]
    [InlineData(5, 0, false)]
    [InlineData(0, 4, false)]
    [InlineData(-1, 0, false)]
    public void IsInside_respects_5x4_bounds(int col, int row, bool expected) =>
        Assert.Equal(expected, BoardGeometry.IsInside(new Cell(col, row)));

    [Fact]
    public void Home_rows_are_opposite_edges()
    {
        Assert.Equal(0, BoardGeometry.HomeRow(0));
        Assert.Equal(3, BoardGeometry.HomeRow(1));
        Assert.Equal(3, BoardGeometry.EnemyHomeRow(0));
        Assert.Equal(0, BoardGeometry.EnemyHomeRow(1));
    }

    [Fact]
    public void Adjacency_is_orthogonal_only()
    {
        var center = new Cell(2, 1);
        Assert.True(BoardGeometry.AreAdjacent(center, new Cell(2, 2)));
        Assert.True(BoardGeometry.AreAdjacent(center, new Cell(1, 1)));
        Assert.False(BoardGeometry.AreAdjacent(center, new Cell(3, 2))); // diagonal
        Assert.False(BoardGeometry.AreAdjacent(center, new Cell(2, 3))); // distance 2
        Assert.False(BoardGeometry.AreAdjacent(center, center));
    }

    [Fact]
    public void AdjacentCells_clips_at_board_edge()
    {
        Assert.Equal(2, BoardGeometry.AdjacentCells(new Cell(0, 0)).Count());
        Assert.Equal(4, BoardGeometry.AdjacentCells(new Cell(2, 1)).Count());
    }

    [Fact]
    public void LineDistance_requires_alignment()
    {
        Assert.Equal(2, BoardGeometry.LineDistance(new Cell(2, 0), new Cell(2, 2)));
        Assert.Equal(3, BoardGeometry.LineDistance(new Cell(1, 2), new Cell(4, 2)));
        Assert.Equal(-1, BoardGeometry.LineDistance(new Cell(1, 1), new Cell(2, 2)));
    }

    [Fact]
    public void CellsBetween_is_exclusive_and_empty_when_adjacent_or_unaligned()
    {
        Assert.Equal([new Cell(2, 1)], BoardGeometry.CellsBetween(new Cell(2, 0), new Cell(2, 2)));
        Assert.Empty(BoardGeometry.CellsBetween(new Cell(2, 0), new Cell(2, 1)));
        Assert.Empty(BoardGeometry.CellsBetween(new Cell(0, 0), new Cell(1, 1)));
        Assert.Equal(2, BoardGeometry.CellsBetween(new Cell(4, 2), new Cell(1, 2)).Count);
    }
}
