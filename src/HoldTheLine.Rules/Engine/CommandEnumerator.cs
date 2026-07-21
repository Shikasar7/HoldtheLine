using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine;

/// <summary>
/// Enumerates legal commands for the active seat. Candidates are generated cheaply, then
/// confirmed by dry-running the resolver — legality has exactly one definition (the resolver),
/// so this can never drift from the actual rules. Powers the simulator now and the heuristic AI
/// in P4. Fine at prototype scale; optimize with a real validator split only if profiling says so.
/// </summary>
public static class CommandEnumerator
{
    /// <param name="forSeat">Only meaningful in the 起手重抽 phase — the seat whose mulligan options to
    /// enumerate (both seats may act). Ignored in normal play, which always enumerates the active seat.</param>
    public static List<Command> LegalCommands(GameState state, CardDatabase db, LeaderDatabase? leaders = null, int? forSeat = null)
    {
        // 起手重抽: the enumerator's job is "any single command here is legal" (bot fallback / timeout
        // auto-play / random bot) — it returns just the keep-all mulligan; heuristic swaps live in the AI layer.
        if (state.Mulligan is { } mull)
        {
            int mseat = forSeat ?? state.ActiveSeat;
            return mseat is 0 or 1 && !mull.Done[mseat]
                ? [new MulliganCommand { Seat = mseat, ReplacedEntityIds = [] }]
                : [];
        }

        leaders ??= LeaderDatabase.Empty;
        var resolver = new Resolver(db, leaders);
        var candidates = new List<Command>();
        int seat = state.ActiveSeat;
        var player = state.Player(seat);

        foreach (var card in player.Hand)
        {
            var def = db.Get(card.CardId);
            if (MinPlayableCost(state, seat, db, def) > player.Mana)
                continue;

            switch (def.Type)
            {
                case CardType.Unit:
                    int homeRow = BoardGeometry.HomeRow(seat);
                    bool needsUnit = def.Effects.Any(e => e.Trigger == "battlecry" && e.NeedsUnitTarget);
                    // Offer the bare (no-target) deploy unless a battlecry FORCES a target — for a no-target unit
                    // it IS the play; for a target-needing battlecry it is the "先上随从再判战吼" fizzle, legal only
                    // when the board has no valid target. Gating here (once per card, not per cell) skips the bare
                    // deploys the resolver would just prune, sparing a full state-clone dry-run per free cell.
                    bool offerBareDeploy = !needsUnit || !EffectEngine.BattlecryTargetMandatory(state, seat, def.Effects);
                    for (int col = 0; col < BoardGeometry.Cols; col++)
                    {
                        var cell = new Cell(col, homeRow);
                        if (state.UnitAt(cell) != null)
                            continue;
                        if (offerBareDeploy)
                            candidates.Add(new PlayCardCommand { Seat = seat, CardEntityId = card.EntityId, TargetCell = cell });
                        if (needsUnit)
                            candidates.AddRange(state.Units.Select(u => (Command)new PlayCardCommand
                                { Seat = seat, CardEntityId = card.EntityId, TargetCell = cell, TargetUnitId = u.EntityId }));
                    }
                    break;

                case CardType.Order:
                    candidates.AddRange(OrderTargets(state, seat, card.EntityId, def));
                    break;
            }
        }

        foreach (var unit in state.Units.Where(u => u.OwnerSeat == seat))
        {
            foreach (var to in MoveDestinations(unit))
                candidates.Add(new MoveUnitCommand { Seat = seat, UnitEntityId = unit.EntityId, To = to });

            foreach (var enemy in state.Units.Where(u => u.OwnerSeat != seat))
                candidates.Add(new AttackCommand
                {
                    Seat = seat,
                    AttackerEntityId = unit.EntityId,
                    TargetUnitId = enemy.EntityId,
                });

            candidates.Add(new AttackCommand { Seat = seat, AttackerEntityId = unit.EntityId, TargetLeader = true });
        }

        if (leaders.TryGet(player.LeaderId, out var leader) && !player.LeaderSkillUsedThisTurn && player.Mana >= leader.SkillCost)
            candidates.AddRange(LeaderSkillTargets(state, seat, leader));

        var legal = candidates.Where(c => resolver.Execute(state, c).Success).ToList();
        legal.Add(new EndTurnCommand { Seat = seat });
        return legal;
    }

    /// <summary>Lower bound on what a card can cost this turn: a 薪炎 order can be cheapened by an on-board
    /// 晚祷领唱-style 引导者 (floor 1), so gating on full cost would wrongly hide an affordable channel play.
    /// The resolver still charges the exact per-channeler cost (docs/21 §1.2).</summary>
    private static int MinPlayableCost(GameState state, int seat, CardDatabase db, CardDefinition def)
    {
        if (def.Type != CardType.Order || !EffectEngine.IsKindleDamageOrder(def))
            return def.Cost;
        int maxDiscount = state.Units
            .Where(u => u.OwnerSeat == seat)
            .Select(u => EffectEngine.ChannelEffectAmount(db, u, "discount"))
            .DefaultIfEmpty(0)
            .Max();
        return maxDiscount > 0 ? Math.Max(1, def.Cost - maxDiscount) : def.Cost;
    }

    private static IEnumerable<Command> OrderTargets(GameState state, int seat, int cardEntityId, CardDefinition def)
    {
        bool needsUnit = def.Effects.Any(e => e.Trigger == "play" && e.NeedsUnitTarget);
        bool needsCell = def.Effects.Any(e => e.Trigger == "play" && e.NeedsCellTarget);
        bool isChannel = def.Effects.Any(e => e.Trigger == "play" && e.IsChannel);

        // 引导 (docs/21 §1.2): enumerate a (channeler × target) grid — the resolver prunes out-of-range
        // pairs. A channel order with no friendly minion on board is unplayable (no channeler → no candidate).
        // Non-channel orders keep a single null channeler, so their enumeration is byte-identical to before.
        var channelers = isChannel
            ? state.Units.Where(u => u.OwnerSeat == seat).Select(u => (int?)u.EntityId).ToList()
            : [null];

        var result = new List<Command>();
        foreach (var ch in channelers)
        {
            if (needsUnit)
                result.AddRange(state.Units.Select(u => (Command)new PlayCardCommand
                    { Seat = seat, CardEntityId = cardEntityId, TargetUnitId = u.EntityId, ChannelerUnitId = ch }));
            else if (needsCell)
                result.AddRange(AllBoardCells().Select(cell => (Command)new PlayCardCommand
                    { Seat = seat, CardEntityId = cardEntityId, TargetCell = cell, ChannelerUnitId = ch }));
            else
                result.Add(new PlayCardCommand { Seat = seat, CardEntityId = cardEntityId, ChannelerUnitId = ch });
        }
        return result;
    }

    private static IEnumerable<Command> LeaderSkillTargets(GameState state, int seat, LeaderDefinition leader)
    {
        if (leader.SkillNeedsUnitTarget)
            return state.Units.Select(u => (Command)new UseLeaderSkillCommand { Seat = seat, TargetUnitId = u.EntityId });
        if (leader.SkillNeedsCellTarget)
            return AllBoardCells().Select(cell => (Command)new UseLeaderSkillCommand { Seat = seat, TargetCell = cell });
        return [new UseLeaderSkillCommand { Seat = seat }];
    }

    /// <summary>Every cell on the board. Cell-target orders/skills (cell_cross_all, row_enemies, …) may point
    /// anywhere; the resolver is the final arbiter of legality, so over-generating candidates is safe.</summary>
    private static IEnumerable<Cell> AllBoardCells()
    {
        for (int row = 0; row < BoardGeometry.Rows; row++)
            for (int col = 0; col < BoardGeometry.Cols; col++)
                yield return new Cell(col, row);
    }

    private static IEnumerable<Cell> MoveDestinations(UnitInstance unit)
    {
        if (unit.HasKeyword(Keyword.Emplacement) && !unit.HasKeyword(Keyword.Mobilized))
            yield break; // 架设: pinned to its cell — never enumerate a move (unless 重新部署 lifted the block).
        if (unit.HasKeyword(Keyword.Rooted))
            yield break; // 定身: rooted this turn — no legal moves.

        foreach (var c in BoardGeometry.AdjacentCells(unit.Cell))
            yield return c;

        if (unit.HasKeyword(Keyword.Leap))
        {
            Cell[] jumps =
            [
                new(unit.Cell.Col + 2, unit.Cell.Row), new(unit.Cell.Col - 2, unit.Cell.Row),
                new(unit.Cell.Col, unit.Cell.Row + 2), new(unit.Cell.Col, unit.Cell.Row - 2),
            ];
            foreach (var c in jumps)
                if (BoardGeometry.IsInside(c))
                    yield return c;
        }
    }
}
