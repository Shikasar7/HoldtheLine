using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>summon: put Amount copies of SummonCardId into play for the owner. (The SummonCardId
/// cross-reference is checked in the CardDatabase constructor once every card is loaded.)</summary>
internal sealed class SummonAction : EffectActionBase
{
    public override string Name => "summon";

    public override string? ValidateCard(EffectSpec spec, CardDefinition card) =>
        spec.Amount < 1
            ? $"Card '{card.Id}': summon needs amount >= 1."
            : null;

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        ctx.SummonUnits(ownerSeat, spec.SummonCardId!, spec.Amount);
    }

    public override double Score(EffectScoreArgs a) => 3 + a.Cost;
}
