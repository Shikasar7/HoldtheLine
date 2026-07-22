using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>amplify_next (蓄能, docs/21 §1.3): bank +Amount for the seat's next 薪炎 order.</summary>
internal sealed class AmplifyNextAction : EffectActionBase
{
    public override string Name => "amplify_next";

    public override string? ValidateCard(EffectSpec spec, CardDefinition card) =>
        spec.Amount < 1
            ? $"Card '{card.Id}': amplify_next (蓄能) needs amount >= 1."
            : null;

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        // 蓄能 N: bank a bonus for the seat's next 薪炎 order (焰跃术士). Consumption happens in the
        // Resolver's order pipeline (ResolveOrder folds SpellCharge into spellDamageBonus, then spends it).
        ctx.AddSpellCharge(ownerSeat, spec.Amount);
    }

    public override double Score(EffectScoreArgs a) => 1.5; // banks a bigger 薪炎 order next turn
}
