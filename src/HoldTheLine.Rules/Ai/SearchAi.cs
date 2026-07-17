using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Ai;

/// <summary>
/// A lookahead AI (M3 plan §3.5, B3). Where <see cref="GreedyAi"/> scores each command by its immediate
/// value, SearchAi asks "what does the board look like AFTER this move, once I finish my turn and the
/// opponent answers?" — it plays each candidate first-move out to a shallow rollout (the rest of my turn
/// and the opponent's whole turn, both with GreedyAi) and keeps the move whose resulting <em>position</em>
/// scores best. That folds in leader-HP swing, material trades, board advance and tide safety instead of
/// one myopic step, so it stops walking into punishes and sequences its own turn (deploy → then attack).
///
/// <para>The rules engine is pure (<see cref="Resolver.Execute"/> resolves against a clone), so this needs
/// no special "undo" — every probe is just another Execute. Cost is bounded by pruning candidates to the
/// top-K and capping rollout length, keeping a step well under the plan's budget.</para>
/// </summary>
public sealed class SearchAi
{
    private const int TopK = 4;              // candidate first-moves to roll out (ranked by a cheap probe)
    private const int MaxRolloutCommands = 8; // safety cap per turn side inside a rollout — also bounds the step budget

    private readonly Resolver _resolver;
    private readonly CardDatabase _db;
    private readonly LeaderDatabase _leaders;

    public SearchAi(CardDatabase db, LeaderDatabase leaders)
    {
        _db = db;
        _leaders = leaders;
        _resolver = new Resolver(db, leaders);
    }

    public Command Pick(GameState state)
        => Pick(state, CommandEnumerator.LegalCommands(state, _db, _leaders));

    public Command Pick(GameState state, IReadOnlyList<Command> legal)
    {
        int seat = state.ActiveSeat;
        var candidates = legal.Where(c => c is not ConcedeCommand).ToList();
        if (candidates.Count == 0)
            return new EndTurnCommand { Seat = seat };
        if (candidates.Count == 1)
            return candidates[0];

        // Rank by the immediate resulting position, then roll out only the most promising few.
        var probed = candidates
            .Select(c => (Command: c, Child: TryExecute(state, c)))
            .Where(t => t.Child is not null)
            .Select(t => (t.Command, t.Child, Quick: Eval(t.Child!, seat)))
            .OrderByDescending(t => t.Quick)
            .Take(TopK)
            .ToList();

        Command best = candidates[^1];
        double bestVal = double.NegativeInfinity;
        foreach (var (command, child, _) in probed)
        {
            double val = Eval(Rollout(child!, seat), seat);
            if (val > bestVal) { bestVal = val; best = command; }
        }
        return best;
    }

    /// <summary>Finish the acting seat's turn, then play out the opponent's whole turn — both greedily —
    /// so the candidate is judged on the position it actually leads to.</summary>
    private GameState Rollout(GameState s, int seat)
    {
        int guard = 0;
        while (s.Result is null && s.ActiveSeat == seat && guard++ < MaxRolloutCommands)
            s = Step(s);
        guard = 0;
        while (s.Result is null && s.ActiveSeat != seat && guard++ < MaxRolloutCommands)
            s = Step(s);
        return s;
    }

    private GameState Step(GameState s)
    {
        var legal = CommandEnumerator.LegalCommands(s, _db, _leaders);
        var pick = GreedyAi.Pick(s, _db, _leaders, legal);
        var r = _resolver.Execute(s, pick);
        if (r.Success)
            return r.State!;
        // A greedy pick is always legal; if the engine ever disagrees, force the turn to end rather than loop.
        var end = _resolver.Execute(s, new EndTurnCommand { Seat = s.ActiveSeat });
        return end.Success ? end.State! : s; // guard counter bounds the loop even if both fail
    }

    private GameState? TryExecute(GameState s, Command c)
    {
        var r = _resolver.Execute(s, c);
        return r.Success ? r.State : null;
    }

    /// <summary>Static position value from <paramref name="seat"/>'s view: leader-HP swing dominates (it is
    /// the win condition), then material, board advance, tide safety and a touch of card advantage.</summary>
    private static double Eval(GameState s, int seat)
    {
        if (s.Result is { } res)
            return res.WinnerSeat == seat ? 1_000_000 : res.WinnerSeat < 0 ? 0 : -1_000_000;

        int opp = 1 - seat;
        double v = (s.Player(seat).LeaderHp - s.Player(opp).LeaderHp) * 3.0;

        foreach (var u in s.Units)
        {
            double body = u.Atk * 1.5 + u.CurrentHp;
            double advance = Math.Abs(u.Cell.Row - BoardGeometry.HomeRow(u.OwnerSeat)) * 0.3; // deeper = better for the owner
            double contrib = body + advance;
            v += u.OwnerSeat == seat ? contrib : -contrib;
        }

        int round = (s.TurnNumber + 1) / 2;
        if (round >= TurnFlow.PressureTideStartRound - 1
            && !s.Units.Any(u => u.OwnerSeat == seat && !BoardGeometry.InOwnHalf(seat, u.Cell)))
            v -= 6; // no body in the enemy half once the tide looms

        v += (s.Player(seat).Hand.Count - s.Player(opp).Hand.Count) * 0.5;
        return v;
    }
}
