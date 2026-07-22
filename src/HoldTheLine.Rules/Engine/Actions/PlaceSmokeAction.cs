using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>place_smoke (烟幕弹, docs/21 §1.6): smoke the chosen cell and its cross.</summary>
internal sealed class PlaceSmokeAction : EffectActionBase
{
    public override string Name => "place_smoke";

    public override string? ValidateCard(EffectSpec spec, CardDefinition card) =>
        spec.Target != "cell"
            ? $"Card '{card.Id}': place_smoke needs target 'cell'."
            : null;

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        // 烟幕弹 (docs/21 §1.6): smoke the 落点格 and its cross (5 cells).
        if (targetCell is { } smokeCell)
            ctx.PlaceSmoke(ownerSeat, smokeCell);
    }

    public override double Score(EffectScoreArgs a) => 2; // tempo denial (区内不能攻击/反击)
}
