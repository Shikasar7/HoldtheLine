using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>move_bonus: +Amount movement this turn for each resolved target.</summary>
internal sealed class MoveBonusAction : EffectActionBase
{
    public override string Name => "move_bonus";

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        foreach (var t in targets)
            ctx.AddMoveBonus(t, spec.Amount);
    }

    public override double Score(EffectScoreArgs a) => GrantKeywordAction.ScoreFriendlyReceiver(a);
}
