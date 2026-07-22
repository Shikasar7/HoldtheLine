using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>
/// Everything the greedy AI needs to price one effect, precomputed once by GreedyAi.ScoreEffect
/// (explicit-target resolution, side booleans, 加深/蓄能-amplified amount) and shared by every handler.
/// </summary>
internal readonly record struct EffectScoreArgs(
    GameState State,
    CardDatabase Db,
    int Seat,
    EffectSpec Spec,
    UnitInstance? Target,
    bool TargetIsEnemy,
    bool TargetIsAlly,
    Cell? TargetCell,
    int Cost,
    int EffectAmount);

/// <summary>
/// One effect action = one registration point (docs/22 D1). A handler owns the three per-action
/// concerns that used to be spread over four hand-synced sites:
///  - <see cref="ValidateCard"/>: load-time data validation (CardDatabase.Validate's old per-action if chain),
///  - <see cref="Execute"/>:      resolution (EffectEngine.Run's old switch),
///  - <see cref="Score"/>:        the greedy AI's value heuristic (GreedyAi.ScoreEffect's old switch).
/// EffectSpec.KnownActions is derived from <see cref="EffectActionRegistry"/>, so adding an action is:
/// write one sealed class, register it — nothing else to keep in sync. Cross-cutting validation keyed
/// on trigger/target/shared fields (not on a single action) stays in CardDatabase.Validate.
/// </summary>
internal interface IEffectAction
{
    /// <summary>The EffectSpec.Action string this handler owns.</summary>
    string Name { get; }

    /// <summary>Load-time per-action validation. Null = OK; otherwise the exact error message —
    /// CardDatabase wraps it in an InvalidDataException verbatim.</summary>
    string? ValidateCard(EffectSpec spec, CardDefinition card);

    /// <summary>Resolution-time execution. <paramref name="targets"/> and <paramref name="amount"/> are
    /// pre-resolved by EffectEngine.Run — target selection, the 双模式 side gate, 法术护体, and the
    /// amount roll + 薪炎 amplification are generic and stay there.</summary>
    void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId);

    /// <summary>Greedy-AI value of this effect with the given targets (GreedyAi.ScoreEffect's old case).</summary>
    double Score(EffectScoreArgs args);
}

/// <summary>Default base: most actions need no per-action load validation. Execute/Score stay abstract
/// on purpose — a silent default score is exactly the trap this registry removes (docs/22 D1).</summary>
internal abstract class EffectActionBase : IEffectAction
{
    public abstract string Name { get; }

    public virtual string? ValidateCard(EffectSpec spec, CardDefinition card) => null;

    public abstract void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId);

    public abstract double Score(EffectScoreArgs args);
}
