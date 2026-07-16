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

    public Resolver(CardDatabase db) => _db = db;

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
            UseLeaderSkillCommand => new RuleError(RuleErrorCode.NotImplemented, "Leader skills arrive in P2."),
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
        if (EffectEngine.ValidateExplicitTargets(ctx, def.Effects, "battlecry", cmd.TargetUnitId) is { } targetError)
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

        EffectEngine.RunTrigger(ctx, unit, cmd.Seat, def.Effects, "battlecry", cmd.TargetUnitId);
        ctx.CheckGameEnd();
        return null;
    }

    private RuleError? ResolveOrder(ResolutionContext ctx, PlayCardCommand cmd, PlayerState player, CardInstance card, CardDefinition def)
    {
        if (EffectEngine.ValidateExplicitTargets(ctx, def.Effects, "play", cmd.TargetUnitId) is { } targetError)
            return targetError;

        player.Mana -= def.Cost;
        player.Hand.Remove(card);
        player.Graveyard.Add(def.Id);

        ctx.Emit(new CardPlayedEvent { Seat = cmd.Seat, CardEntityId = card.EntityId, CardId = def.Id, ManaSpent = def.Cost });
        EffectEngine.RunTrigger(ctx, source: null, cmd.Seat, def.Effects, "play", cmd.TargetUnitId);
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
        if (IsSummoningSick(ctx.State, unit) && !unit.HasKeyword(Keyword.Charge))
            return new RuleError(RuleErrorCode.SummoningSickness, "This unit is still mustering (集结中).");
        if (unit.MovementUsed >= unit.MovementPerTurn)
            return new RuleError(RuleErrorCode.NoMovementLeft, "No movement left this turn.");
        if (!BoardGeometry.IsInside(cmd.To))
            return new RuleError(RuleErrorCode.CellOutsideBoard, $"{cmd.To} is outside the board.");
        if (!BoardGeometry.AreAdjacent(unit.Cell, cmd.To))
            return new RuleError(RuleErrorCode.NotAdjacent, "Movement is one orthogonal step at a time.");
        if (ctx.State.UnitAt(cmd.To) != null)
            return new RuleError(RuleErrorCode.CellOccupied, "Units never pass through or share cells — friend or foe (GDD §2.4).");

        var from = unit.Cell;
        unit.Cell = cmd.To;
        unit.MovementUsed++;
        unit.MovedThisRound = true;
        ctx.Emit(new UnitMovedEvent { UnitEntityId = unit.EntityId, From = from, To = cmd.To });
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
        bool adjacentToTarget = BoardGeometry.AreAdjacent(attacker.Cell, target.Cell);

        if (range == 0)
        {
            if (!adjacentToTarget)
                return new RuleError(RuleErrorCode.NotAdjacent, "Melee units attack adjacent enemies only.");
        }
        else
        {
            int distance = BoardGeometry.LineDistance(attacker.Cell, target.Cell);
            if (distance < 1)
                return new RuleError(RuleErrorCode.OutOfRange, "Ranged attacks travel along a row or column.");
            if (distance > range)
                return new RuleError(RuleErrorCode.OutOfRange, $"Target is {distance} cells away; range is {range}.");
            if (BoardGeometry.CellsBetween(attacker.Cell, target.Cell).Any(c => ctx.State.UnitAt(c) != null))
                return new RuleError(RuleErrorCode.LineBlocked, "Line of fire is blocked.");
        }

        // 守护: an attacker adjacent to any enemy Guard must pick one of those Guards.
        var guards = AdjacentEnemyGuards(ctx.State, attacker);
        if (guards.Count > 0 && !guards.Contains(target.EntityId))
            return new RuleError(RuleErrorCode.GuardEnforced, "An adjacent enemy with 守护 must be attacked first.");

        attacker.AttacksUsed++;
        ctx.Emit(new AttackedEvent { AttackerEntityId = attacker.EntityId, TargetUnitId = target.EntityId });

        // Simultaneous strike (GDD §2.5): compute both sides before applying either.
        // Retaliation happens only against melee attacks, and never against 偷袭 (CheapShot).
        bool retaliates = range == 0 && !attacker.HasKeyword(Keyword.CheapShot) && target.Atk > 0;
        ctx.DamageUnit(target, attacker.Atk);
        if (retaliates)
            ctx.DamageUnit(attacker, target.Atk);

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
        }

        ctx.CheckGameEnd();
        return null;
    }

    // ---- turn / concede ----

    private static RuleError? ResolveEndTurn(ResolutionContext ctx)
    {
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

    private static HashSet<int> AdjacentEnemyGuards(GameState state, UnitInstance attacker) =>
        BoardGeometry.AdjacentCells(attacker.Cell)
            .Select(state.UnitAt)
            .Where(u => u != null && u.OwnerSeat != attacker.OwnerSeat && u.HasKeyword(Keyword.Guard))
            .Select(u => u!.EntityId)
            .ToHashSet();
}
