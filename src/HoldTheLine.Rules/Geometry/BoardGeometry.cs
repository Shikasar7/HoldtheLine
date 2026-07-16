namespace HoldTheLine.Rules.Geometry;

/// <summary>Static 5x4 board geometry (GDD §2.1). Orthogonal adjacency only — diagonals never count.</summary>
public static class BoardGeometry
{
    public const int Cols = 5;
    public const int Rows = 4;

    public static bool IsInside(Cell c) => c.Col >= 0 && c.Col < Cols && c.Row >= 0 && c.Row < Rows;

    /// <summary>The row a seat deploys on; reaching the opponent's home row enables leader attacks.</summary>
    public static int HomeRow(int seat) => seat == 0 ? 0 : Rows - 1;

    public static int EnemyHomeRow(int seat) => HomeRow(1 - seat);

    public static bool AreAdjacent(Cell a, Cell b) =>
        Math.Abs(a.Col - b.Col) + Math.Abs(a.Row - b.Row) == 1;

    public static IEnumerable<Cell> AdjacentCells(Cell c)
    {
        Cell[] candidates =
        [
            new(c.Col + 1, c.Row), new(c.Col - 1, c.Row),
            new(c.Col, c.Row + 1), new(c.Col, c.Row - 1),
        ];
        foreach (var cand in candidates)
            if (IsInside(cand))
                yield return cand;
    }

    /// <summary>Straight-line distance when two cells share a row or column; -1 when not aligned.</summary>
    public static int LineDistance(Cell a, Cell b)
    {
        if (a.Col == b.Col) return Math.Abs(a.Row - b.Row);
        if (a.Row == b.Row) return Math.Abs(a.Col - b.Col);
        return -1;
    }

    /// <summary>Cells strictly between two aligned cells (exclusive of both ends). Empty when adjacent or not aligned.</summary>
    public static IReadOnlyList<Cell> CellsBetween(Cell a, Cell b)
    {
        var result = new List<Cell>();
        if (a.Col == b.Col)
        {
            int step = Math.Sign(b.Row - a.Row);
            for (int r = a.Row + step; r != b.Row; r += step)
                result.Add(new Cell(a.Col, r));
        }
        else if (a.Row == b.Row)
        {
            int step = Math.Sign(b.Col - a.Col);
            for (int c = a.Col + step; c != b.Col; c += step)
                result.Add(new Cell(c, a.Row));
        }
        return result;
    }
}
