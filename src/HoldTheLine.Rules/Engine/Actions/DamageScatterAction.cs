using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>damage_scatter (燔火, docs/21 §3.1): Amount missiles of 1 薪炎 at random enemy minions.</summary>
internal sealed class DamageScatterAction : EffectActionBase
{
    public override string Name => "damage_scatter";

    public override string? ValidateCard(EffectSpec spec, CardDefinition card) =>
        spec.Amount < 1 || spec.Target != "none"
            ? $"Card '{card.Id}': 燔火 (damage_scatter) needs amount >= 1 and target 'none'."
            : null;

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        // 燔火 (docs/21 §3.1): fire `amount` missiles of 1 薪炎 damage, each at a RANDOM live enemy minion
        // (re-rolled per missile among survivors, 炉石奥术飞弹 semantics). The roll is on the match Rng so
        // replays are deterministic. 加深/蓄能 already folded into `amount` upstream (+1 missile per point).
        for (int i = 0; i < amount; i++)
        {
            var live = ctx.State.Units.Where(u => u.OwnerSeat != ownerSeat && u.CurrentHp > 0).ToList();
            if (live.Count == 0)
                break;
            var victim = live[ctx.State.Rng.NextInt(live.Count)];
            ctx.DamageUnit(victim, 1, school: spec.School, effectDamage: true); // 架设 +1 applied inside
        }
    }

    public override double Score(EffectScoreArgs a)
    {
        // 燔火: `amount` missiles of 1 at random enemies — worth ~ per-missile enemy value.
        int seat = a.Seat;
        int enemies = a.State.Units.Count(u => u.OwnerSeat != seat);
        return enemies == 0 ? 0 : Math.Min(a.EffectAmount, enemies * 3) * 1.5;
    }
}
