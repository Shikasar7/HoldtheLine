using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine;

/// <summary>
/// Executes data-driven EffectSpecs. P1 implements the minimal primitive set (damage / buff /
/// draw / gain_mana × self / target_unit / adjacent_*); P2 extends this class ONLY — new
/// primitives must not leak special cases into the resolver.
/// </summary>
internal static class EffectEngine
{
    /// <param name="source">The unit the effect originates from; null for orders.</param>
    /// <param name="targetUnitId">Explicit target carried by the command (target == "target_unit").</param>
    public static void RunTrigger(
        ResolutionContext ctx,
        UnitInstance? source,
        int ownerSeat,
        IReadOnlyList<EffectSpec> effects,
        string trigger,
        int? targetUnitId)
    {
        foreach (var spec in effects)
        {
            if (spec.Trigger != trigger)
                continue;
            Run(ctx, source, ownerSeat, spec, targetUnitId);
        }
        ctx.ProcessDeaths();
    }

    /// <summary>Pre-validation used by the resolver before paying costs: does this effect's explicit target exist?</summary>
    public static RuleError? ValidateExplicitTargets(ResolutionContext ctx, IReadOnlyList<EffectSpec> effects, string trigger, int? targetUnitId)
    {
        foreach (var spec in effects)
        {
            if (spec.Trigger != trigger || spec.Target != "target_unit")
                continue;
            if (targetUnitId is null)
                return new RuleError(RuleErrorCode.InvalidTarget, "This card requires a unit target.");
            if (ctx.State.FindUnit(targetUnitId.Value) is null)
                return new RuleError(RuleErrorCode.UnknownEntity, $"Target unit {targetUnitId.Value} does not exist.");
        }
        return null;
    }

    private static void Run(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec, int? targetUnitId)
    {
        var targets = ResolveTargets(ctx, source, spec.Target, targetUnitId);

        switch (spec.Action)
        {
            case "damage":
                foreach (var t in targets)
                    ctx.DamageUnit(t, spec.Amount);
                break;

            case "buff":
                foreach (var t in targets)
                {
                    t.Atk += spec.Atk;
                    t.MaxHp += spec.Hp;
                    t.CurrentHp += spec.Hp;
                    ctx.Emit(new UnitBuffedEvent
                    {
                        UnitEntityId = t.EntityId,
                        AtkDelta = spec.Atk,
                        HpDelta = spec.Hp,
                        NewAtk = t.Atk,
                        NewHp = t.CurrentHp,
                    });
                }
                break;

            case "draw":
                ctx.DrawCards(ownerSeat, spec.Amount);
                break;

            case "gain_mana":
                ctx.GainMana(ownerSeat, spec.Amount);
                break;

            default:
                // CardDatabase validation guarantees this is unreachable; keep it loud, not silent.
                throw new InvalidOperationException($"Unknown effect action '{spec.Action}'.");
        }
    }

    private static List<UnitInstance> ResolveTargets(ResolutionContext ctx, UnitInstance? source, string target, int? targetUnitId)
    {
        switch (target)
        {
            case "none":
                return [];

            case "self":
                return source is null ? [] : [source];

            case "target_unit":
                var explicitTarget = targetUnitId is null ? null : ctx.State.FindUnit(targetUnitId.Value);
                return explicitTarget is null ? [] : [explicitTarget];

            case "adjacent_allies":
            case "adjacent_enemies":
                if (source is null)
                    return [];
                bool allies = target == "adjacent_allies";
                return BoardGeometry.AdjacentCells(source.Cell)
                    .Select(ctx.State.UnitAt)
                    .Where(u => u != null && (u.OwnerSeat == source.OwnerSeat) == allies)
                    .Select(u => u!)
                    .ToList();

            default:
                throw new InvalidOperationException($"Unknown effect target '{target}'.");
        }
    }
}
