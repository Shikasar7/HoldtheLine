using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.Serialization;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine;

/// <summary>
/// The single entry point for advancing a match: validate a command, resolve it against a CLONE
/// of the given state, return the new state plus the event batch. Pure and deterministic — same
/// state + same command always yields the same result. This file owns movement and combat
/// semantics (GDD §2.3–2.5); shared mutation helpers live in ResolutionContext; data-driven card
/// effects live in EffectEngine.
/// </summary>
public sealed class Resolver
{
    private readonly CardDatabase _db;
    private readonly LeaderDatabase _leaders;

    public Resolver(CardDatabase db) : this(db, LeaderDatabase.Empty) { }

    public Resolver(CardDatabase db, LeaderDatabase leaders)
    {
        _db = db;
        _leaders = leaders;
    }

    public ExecutionResult Execute(GameState state, Command command)
    {
        if (state.Result != null)
            return ExecutionResult.Fail(RuleErrorCode.GameOver, "The game has ended.");
        if (command.Seat is not (0 or 1))
            return ExecutionResult.Fail(RuleErrorCode.InvalidCommand, $"Invalid seat {command.Seat}.");
        if (command is not ConcedeCommand && command.Seat != state.ActiveSeat)
            return ExecutionResult.Fail(RuleErrorCode.NotYourTurn, "It is not your turn.");

        var working = RulesJson.Clone(state);
        var ctx = new ResolutionContext(working, _db);

        var error = command switch
        {
            PlayCardCommand play => ResolvePlayCard(ctx, play),
            MoveUnitCommand move => ResolveMove(ctx, move),
            AttackCommand attack => ResolveAttack(ctx, attack),
            UseLeaderSkillCommand skill => ResolveLeaderSkill(ctx, skill),
            EndTurnCommand => ResolveEndTurn(ctx),
            ConcedeCommand concede => ResolveConcede(ctx, concede),
            _ => new RuleError(RuleErrorCode.InvalidCommand, $"Unknown command type {command.GetType().Name}."),
        };

        return error != null ? ExecutionResult.Fail(error.Code, error.Message) : ExecutionResult.Ok(working, ctx.Events);
    }

    // ---- play card ----

    private RuleError? ResolvePlayCard(ResolutionContext ctx, PlayCardCommand cmd)
    {
        var player = ctx.State.Player(cmd.Seat);
        var card = player.Hand.FirstOrDefault(c => c.EntityId == cmd.CardEntityId);
        if (card is null)
            return new RuleError(RuleErrorCode.UnknownEntity, $"Card {cmd.CardEntityId} is not in your hand.");

        var def = _db.Get(card.CardId);
        if (player.Mana < def.Cost)
            return new RuleError(RuleErrorCode.NotEnoughMana, $"'{def.Name}' costs {def.Cost}, you have {player.Mana}.");

        return def.Type switch
        {
            CardType.Unit => ResolveDeployUnit(ctx, cmd, player, card, def),
            CardType.Order => ResolveOrder(ctx, cmd, player, card, def),
            _ => new RuleError(RuleErrorCode.NotImplemented, $"Card type {def.Type} is not implemented."),
        };
    }

    private RuleError? ResolveDeployUnit(ResolutionContext ctx, PlayCardCommand cmd, PlayerState player, CardInstance card, CardDefinition def)
    {
        if (cmd.TargetCell is not { } cell)
            return new RuleError(RuleErrorCode.InvalidTarget, "Deploying a unit requires a target cell.");
        if (!BoardGeometry.IsInside(cell))
            return new RuleError(RuleErrorCode.CellOutsideBoard, $"{cell} is outside the board.");
        if (cell.Row != BoardGeometry.HomeRow(cmd.Seat))
            return new RuleError(RuleErrorCode.NotHomeRow, "Units deploy on your home row (GDD §2.3).");
        if (ctx.State.UnitAt(cell) != null)
            return new RuleError(RuleErrorCode.CellOccupied, $"{cell} is occupied.");
        if (EffectEngine.ValidateTargets(ctx, cmd.Seat, def.Effects, "battlecry", cmd.TargetUnitId, cmd.TargetCell) is { } targetError)
            return targetError;

        player.Mana -= def.Cost;
        player.Hand.Remove(card);

        var unit = new UnitInstance
        {
            EntityId = card.EntityId,
            CardId = def.Id,
            OwnerSeat = cmd.Seat,
            Cell = cell,
            Atk = def.Atk,
            MaxHp = def.Hp,
            CurrentHp = def.Hp,
            DeployedOnTurn = ctx.State.TurnNumber,
            ShieldActive = def.HasKeyword(Keyword.Shield),
            Keywords = def.Keywords.ToList(),
        };
        ctx.State.Units.Add(unit);

        ctx.Emit(new CardPlayedEvent { Seat = cmd.Seat, CardEntityId = card.EntityId, CardId = def.Id, ManaSpent = def.Cost });
        ctx.Emit(new UnitDeployedEvent
        {
            Seat = cmd.Seat, UnitEntityId = unit.EntityId, CardId = def.Id,
            Cell = cell, Atk = unit.Atk, Hp = unit.CurrentHp,
        });
        ctx.RecomputeGarrison(unit); // deploys on the home row → gains 驻防 immediately

        EffectEngine.RunTrigger(ctx, unit, cmd.Seat, def.Effects, "battlecry", cmd.TargetUnitId, cmd.TargetCell);
        ctx.CheckGameEnd();
        return null;
    }

    private RuleError? ResolveOrder(ResolutionContext ctx, PlayCardCommand cmd, PlayerState player, CardInstance card, CardDefinition def)
    {
        if (EffectEngine.ValidateTargets(ctx, cmd.Seat, def.Effects, "play", cmd.TargetUnitId, cmd.TargetCell) is { } targetError)
            return targetError;

        player.Mana -= def.Cost;
        player.Hand.Remove(card);
        player.Graveyard.Add(def.Id);

        ctx.Emit(new CardPlayedEvent { Seat = cmd.Seat, CardEntityId = card.EntityId, CardId = def.Id, ManaSpent = def.Cost });
        EffectEngine.RunTrigger(ctx, source: null, cmd.Seat, def.Effects, "play", cmd.TargetUnitId, cmd.TargetCell);
        ctx.FireAllyOrderPlayed(cmd.Seat); // 教团: each of your units reacts once the order has fully resolved.
        ctx.CheckGameEnd();
        return null;
    }

    // ---- movement (GDD §2.4) ----

    private static RuleError? ResolveMove(ResolutionContext ctx, MoveUnitCommand cmd)
    {
        var unit = ctx.State.FindUnit(cmd.UnitEntityId);
        if (unit is null)
            return new RuleError(RuleErrorCode.UnknownEntity, $"Unit {cmd.UnitEntityId} does not exist.");
        if (unit.OwnerSeat != cmd.Seat)
            return new RuleError(RuleErrorCode.NotYourUnit, "That unit is not yours.");
        if (unit.HasKeyword(Keyword.Emplacement))
            return new RuleError(RuleErrorCode.Emplaced, "架设单位不能移动。"); // Leap / move_bonus can't help — there is no movement to spend.
        if (IsSummoningSick(ctx.State, unit) && !unit.HasKeyword(Keyword.Charge))
            return new RuleError(RuleErrorCode.SummoningSickness, "This unit is still mustering (集结中).");
        if (unit.MovementUsed >= unit.MovementPerTurn)
            return new RuleError(RuleErrorCode.NoMovementLeft, "No movement left this turn.");
        if (!BoardGeometry.IsInside(cmd.To))
            return new RuleError(RuleErrorCode.CellOutsideBoard, $"{cmd.To} is outside the board.");

        bool adjacent = BoardGeometry.AreAdjacent(unit.Cell, cmd.To);
        // 跃障 (Leap): one straight-line jump of 2, crossing whatever sits between (GDD keyword).
        bool leap = !adjacent
            && unit.HasKeyword(Keyword.Leap)
            && BoardGeometry.LineDistance(unit.Cell, cmd.To) == 2;
        if (!adjacent && !leap)
            return new RuleError(RuleErrorCode.NotAdjacent, "Movement is one orthogonal step (跃障 excepted).");
        if (ctx.State.UnitAt(cmd.To) != null)
            return new RuleError(RuleErrorCode.CellOccupied, "Units never share cells — friend or foe (GDD §2.4).");

        var from = unit.Cell;
        unit.Cell = cmd.To;
        unit.MovementUsed++;
        unit.MovedThisRound = true;
        ctx.Emit(new UnitMovedEvent { UnitEntityId = unit.EntityId, From = from, To = cmd.To });
        ctx.RecomputeGarrison(unit); // leaving/entering the home row toggles 驻防
        ctx.ProcessDeaths();         // losing borrowed 驻防 HP can be lethal
        return null;
    }

    // ---- combat (GDD §2.5) ----

    private static RuleError? ResolveAttack(ResolutionContext ctx, AttackCommand cmd)
    {
        var attacker = ctx.State.FindUnit(cmd.AttackerEntityId);
        if (attacker is null)
            return new RuleError(RuleErrorCode.UnknownEntity, $"Unit {cmd.AttackerEntityId} does not exist.");
        if (attacker.OwnerSeat != cmd.Seat)
            return new RuleError(RuleErrorCode.NotYourUnit, "That unit is not yours.");
        if (IsSummoningSick(ctx.State, attacker) && !attacker.HasKeyword(Keyword.Charge) && !attacker.HasKeyword(Keyword.Assault))
            return new RuleError(RuleErrorCode.SummoningSickness, "This unit is still mustering (集结中).");
        if (attacker.AttacksUsed >= 1)
            return new RuleError(RuleErrorCode.NoAttacksLeft, "This unit has already attacked.");

        return cmd.TargetLeader
            ? ResolveLeaderAttack(ctx, cmd, attacker)
            : ResolveUnitAttack(ctx, cmd, attacker);
    }

    private static RuleError? ResolveLeaderAttack(ResolutionContext ctx, AttackCommand cmd, UnitInstance attacker)
    {
        if (attacker.Cell.Row != BoardGeometry.EnemyHomeRow(cmd.Seat))
            return new RuleError(RuleErrorCode.NotOnEnemyHomeRow, "Leaders can only be attacked from their home row (GDD §2.5).");
        if (AdjacentEnemyGuards(ctx.State, attacker).Count > 0)
            return new RuleError(RuleErrorCode.GuardEnforced, "An adjacent enemy with 守护 must be attacked first.");

        attacker.AttacksUsed++;
        int defendingSeat = 1 - cmd.Seat;
        ctx.Emit(new AttackedEvent { AttackerEntityId = attacker.EntityId, TargetLeaderSeat = defendingSeat });
        ctx.DamageLeader(defendingSeat, attacker.Atk); // leader attacks are never retaliated
        return null;
    }

    private static RuleError? ResolveUnitAttack(ResolutionContext ctx, AttackCommand cmd, UnitInstance attacker)
    {
        if (cmd.TargetUnitId is null)
            return new RuleError(RuleErrorCode.InvalidTarget, "Attack requires a target unit or TargetLeader.");
        var target = ctx.State.FindUnit(cmd.TargetUnitId.Value);
        if (target is null)
            return new RuleError(RuleErrorCode.UnknownEntity, $"Unit {cmd.TargetUnitId.Value} does not exist.");
        if (target.OwnerSeat == cmd.Seat)
            return new RuleError(RuleErrorCode.InvalidTarget, "Cannot attack a friendly unit.");

        int range = attacker.HasKeyword(Keyword.Range) ? attacker.KeywordValue(Keyword.Range) : 0;

        if (range == 0)
        {
            if (!BoardGeometry.AreAdjacent(attacker.Cell, target.Cell))
                return new RuleError(RuleErrorCode.NotAdjacent, "Melee units attack adjacent enemies only.");
        }
        else
        {
            // 射程 N is measured in orthogonal steps (Manhattan): any cell within N steps is a legal
            // target — diagonals included — and shots pass over every body, friend or foe (no line
            // blocking). See GDD §2.5 (2026-07-17 revision).
            int distance = BoardGeometry.StepDistance(attacker.Cell, target.Cell);
            if (distance > range)
                return new RuleError(RuleErrorCode.OutOfRange, $"Target is {distance} steps away; range is {range}.");
        }

        // 守护: an attacker adjacent to any enemy Guard must pick one of those Guards.
        var guards = AdjacentEnemyGuards(ctx.State, attacker);
        if (guards.Count > 0 && !guards.Contains(target.EntityId))
            return new RuleError(RuleErrorCode.GuardEnforced, "An adjacent enemy with 守护 must be attacked first.");

        attacker.AttacksUsed++;
        ctx.Emit(new AttackedEvent { AttackerEntityId = attacker.EntityId, TargetUnitId = target.EntityId });

        // Simultaneous strike (GDD §2.5): compute both sides before applying either. Retaliation lands
        // whenever the target can strike back at the attacker's cell — always true for melee (the attacker
        // is adjacent), and true for a ranged shot only when the attacker is inside the target's own reach
        // (射程/adjacency). A shot from safe distance is unanswered; 偷袭 (CheapShot) is never retaliated.
        bool retaliates = target.Atk > 0
            && !attacker.HasKeyword(Keyword.CheapShot)
            && ReachesCell(target, attacker.Cell);
        // 围猎 (PackTactics): melee attacks on flanked prey — another friendly unit adjacent to the
        // target — deal +1 damage. Speed buys the surround; the surround buys the kill.
        bool packFlank = range == 0
            && attacker.HasKeyword(Keyword.PackTactics)
            && BoardGeometry.AdjacentCells(target.Cell)
                .Select(ctx.State.UnitAt)
                .Any(u => u != null && u.OwnerSeat == cmd.Seat && u.EntityId != attacker.EntityId);
        ctx.DamageUnit(target, attacker.Atk + (packFlank ? 1 : 0));
        if (retaliates)
            ctx.DamageUnit(attacker, target.Atk);

        // 贯穿 (Pierce): a ranged shot along a straight line (attacker and target share a row/column)
        // also strikes the cell one step directly behind the target — friend or foe, equal damage, no
        // retaliation, one cell only. Diagonal shots have no defined "behind" cell, so they don't pierce.
        if (range > 0 && attacker.HasKeyword(Keyword.Pierce)
            && BoardGeometry.StepBeyond(attacker.Cell, target.Cell) is { } behindCell
            && BoardGeometry.IsInside(behindCell)
            && ctx.State.UnitAt(behindCell) is { } behindUnit)
        {
            ctx.DamageUnit(behindUnit, attacker.Atk);
        }

        bool targetDied = target.CurrentHp <= 0;
        var vacated = target.Cell;
        ctx.ProcessDeaths();

        // 践踏: melee kill + attacker survived + opted in + the cell is still free.
        if (targetDied
            && range == 0
            && cmd.OccupyCellOnKill
            && attacker.HasKeyword(Keyword.Trample)
            && attacker.CurrentHp > 0
            && ctx.State.FindUnit(attacker.EntityId) != null
            && ctx.State.UnitAt(vacated) is null)
        {
            var from = attacker.Cell;
            attacker.Cell = vacated;
            attacker.MovedThisRound = true; // occupying counts as movement for 坚守
            ctx.Emit(new UnitMovedEvent { UnitEntityId = attacker.EntityId, From = from, To = vacated });
            ctx.RecomputeGarrison(attacker); // trampling forward leaves the home row
            ctx.ProcessDeaths();
        }

        ctx.CheckGameEnd();
        return null;
    }

    // ---- leader skill (GDD §2.2) ----

    private RuleError? ResolveLeaderSkill(ResolutionContext ctx, UseLeaderSkillCommand cmd)
    {
        var player = ctx.State.Player(cmd.Seat);
        if (!_leaders.TryGet(player.LeaderId, out var leader))
            return new RuleError(RuleErrorCode.NotImplemented, $"No leader skill for '{player.LeaderId}'.");
        if (player.LeaderSkillUsedThisTurn)
            return new RuleError(RuleErrorCode.InvalidCommand, "Leader skill already used this turn.");
        if (player.Mana < leader.SkillCost)
            return new RuleError(RuleErrorCode.NotEnoughMana, $"Leader skill costs {leader.SkillCost}, you have {player.Mana}.");
        if (EffectEngine.ValidateTargets(ctx, cmd.Seat, leader.SkillEffects, "leader_skill", cmd.TargetUnitId, cmd.TargetCell) is { } targetError)
            return targetError;

        player.Mana -= leader.SkillCost;
        player.LeaderSkillUsedThisTurn = true;
        ctx.Emit(new LeaderSkillUsedEvent { Seat = cmd.Seat, LeaderId = leader.Id, TargetUnitId = cmd.TargetUnitId });
        EffectEngine.RunTrigger(ctx, source: null, cmd.Seat, leader.SkillEffects, "leader_skill", cmd.TargetUnitId, cmd.TargetCell);
        ctx.CheckGameEnd();
        return null;
    }

    // ---- turn / concede ----

    private static RuleError? ResolveEndTurn(ResolutionContext ctx)
    {
        ctx.ExpireEndOfTurnGrants(); // pounce, etc. lapse before control passes
        ctx.Emit(new TurnEndedEvent { Seat = ctx.State.ActiveSeat, TurnNumber = ctx.State.TurnNumber });
        TurnFlow.StartTurn(ctx, 1 - ctx.State.ActiveSeat);
        return null;
    }

    private static RuleError? ResolveConcede(ResolutionContext ctx, ConcedeCommand cmd)
    {
        ctx.State.Result = new GameResult { WinnerSeat = 1 - cmd.Seat, Reason = "concede" };
        ctx.Emit(new GameEndedEvent { WinnerSeat = 1 - cmd.Seat, Reason = "concede" });
        return null;
    }

    // ---- shared checks ----

    private static bool IsSummoningSick(GameState state, UnitInstance unit) =>
        unit.DeployedOnTurn == state.TurnNumber && unit.OwnerSeat == state.ActiveSeat;

    /// <summary>Whether <paramref name="unit"/> could attack the given cell from where it stands — adjacent
    /// for a melee unit, within 射程 steps (Manhattan) for a ranged one. Used to decide retaliation.</summary>
    private static bool ReachesCell(UnitInstance unit, Cell cell)
    {
        int range = unit.HasKeyword(Keyword.Range) ? unit.KeywordValue(Keyword.Range) : 0;
        return range == 0
            ? BoardGeometry.AreAdjacent(unit.Cell, cell)
            : BoardGeometry.StepDistance(unit.Cell, cell) <= range;
    }

    private static HashSet<int> AdjacentEnemyGuards(GameState state, UnitInstance attacker) =>
        BoardGeometry.AdjacentCells(attacker.Cell)
            .Select(state.UnitAt)
            .Where(u => u != null && u.OwnerSeat != attacker.OwnerSeat && u.HasKeyword(Keyword.Guard))
            .Select(u => u!.EntityId)
            .ToHashSet();
}
