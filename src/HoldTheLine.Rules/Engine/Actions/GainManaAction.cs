using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>gain_mana (辉尘): the owner gains Amount mana this turn (归魂's payoff, docs/21 §1.4).</summary>
internal sealed class GainManaAction : EffectActionBase
{
    public override string Name => "gain_mana";

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        ctx.GainMana(ownerSeat, spec.Amount);
    }

    public override double Score(EffectScoreArgs a) => 0.5;
}
