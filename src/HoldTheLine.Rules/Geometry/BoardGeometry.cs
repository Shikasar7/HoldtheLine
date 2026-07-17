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

    /// <summary>A seat's own half of the board (its home + front rows). Seat 0 = rows 0–1, seat 1 = rows 2–3.</summary>
    public static bool InOwnHalf(int seat, Cell c) => seat == 0 ? c.Row < Rows / 2 : c.Row >= Rows / 2;

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

    /// <summary>Orthogonal-step (Manhattan) distance: |Δcol| + |Δrow|. The metric for 射程 N — a
    /// range-N unit hits any cell within N steps, diagonals included (diagonal-1 = distance 2).</summary>
    public static int StepDistance(Cell a, Cell b) =>
        Math.Abs(a.Col - b.Col) + Math.Abs(a.Row - b.Row);

    /// <summary>Whether two distinct cells share a row or column (the only case where a shot has a
    /// well-defined "straight line" and thus a cell directly behind the target — used by 贯穿).</summary>
    public static bool AreAligned(Cell a, Cell b) => a.Col == b.Col || a.Row == b.Row;

    /// <summary>
    /// The cell one orthogonal step beyond <paramref name="through"/>, continuing the straight line
    /// from <paramref name="from"/> (the attacker) through the target. Null when the two are not
    /// aligned (no straight line). The result may lie outside the board — callers check <see cref="IsInside"/>.
    /// </summary>
    public static Cell? StepBeyond(Cell from, Cell through)
    {
        if (from == through || !AreAligned(from, through))
            return null;
        int dc = Math.Sign(through.Col - from.Col);
        int dr = Math.Sign(through.Row - from.Row);
        return new Cell(through.Col + dc, through.Row + dr);
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
