namespace HoldTheLine.Rules.Cards;

/// <summary>
/// Static leader data, loaded from game/data/leaders. A leader's skill is expressed as EffectSpecs
/// with trigger "leader_skill", reusing the same effect vocabulary as cards.
/// </summary>
public sealed record LeaderDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Faction { get; init; } = "neutral";
    public int SkillCost { get; init; } = 2;
    public string SkillName { get; init; } = "";
    public string Text { get; init; } = "";
    public string ArtPrompt { get; init; } = "";
    public IReadOnlyList<EffectSpec> SkillEffects { get; init; } = [];

    /// <summary>Whether the skill needs an explicit unit target (derived from its effects).</summary>
    public bool SkillNeedsUnitTarget => SkillEffects.Any(e => e.NeedsUnitTarget);
    public bool SkillNeedsCellTarget => SkillEffects.Any(e => e.NeedsCellTarget);
}
