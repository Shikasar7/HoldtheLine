using System.Text.Json;
using HoldTheLine.Rules.Serialization;

namespace HoldTheLine.Rules.Cards;

/// <summary>Immutable leader registry, validated at construction like <see cref="CardDatabase"/>.</summary>
public sealed class LeaderDatabase
{
    private readonly Dictionary<string, LeaderDefinition> _leaders;

    public LeaderDatabase(IEnumerable<LeaderDefinition> leaders)
    {
        _leaders = new Dictionary<string, LeaderDefinition>(StringComparer.Ordinal);
        foreach (var leader in leaders)
        {
            if (!_leaders.TryAdd(leader.Id, leader))
                throw new InvalidDataException($"Duplicate leader id '{leader.Id}'.");
            Validate(leader);
        }
    }

    public static LeaderDatabase Empty { get; } = new(Array.Empty<LeaderDefinition>());

    public IReadOnlyCollection<LeaderDefinition> All => _leaders.Values;

    public LeaderDefinition Get(string id) =>
        _leaders.TryGetValue(id, out var def)
            ? def
            : throw new KeyNotFoundException($"Unknown leader id '{id}'.");

    public bool TryGet(string id, out LeaderDefinition def) => _leaders.TryGetValue(id, out def!);

    public static IReadOnlyList<LeaderDefinition> ParseJson(string json) =>
        JsonSerializer.Deserialize<List<LeaderDefinition>>(json, RulesJson.Options)
            ?? throw new InvalidDataException("Leader JSON deserialized to null.");

    public static LeaderDatabase LoadFromDirectory(string directory)
    {
        var leaders = new List<LeaderDefinition>();
        if (Directory.Exists(directory))
            foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                leaders.AddRange(ParseJson(File.ReadAllText(file)));
        return new LeaderDatabase(leaders);
    }

    private static void Validate(LeaderDefinition leader)
    {
        if (string.IsNullOrWhiteSpace(leader.Name))
            throw new InvalidDataException($"Leader '{leader.Id}' has no name.");
        if (leader.SkillCost < 0 || leader.SkillCost > 10)
            throw new InvalidDataException($"Leader '{leader.Id}' has invalid skill cost {leader.SkillCost}.");
        foreach (var spec in leader.SkillEffects)
        {
            if (spec.Trigger != "leader_skill")
                throw new InvalidDataException($"Leader '{leader.Id}': skill effects must use trigger 'leader_skill'.");
            if (!EffectSpec.KnownActions.Contains(spec.Action))
                throw new InvalidDataException($"Leader '{leader.Id}': unknown action '{spec.Action}'.");
            if (!EffectSpec.KnownTargets.Contains(spec.Target))
                throw new InvalidDataException($"Leader '{leader.Id}': unknown target '{spec.Target}'.");
            if (!EffectSpec.KnownDurations.Contains(spec.Duration))
                throw new InvalidDataException($"Leader '{leader.Id}': unknown duration '{spec.Duration}'.");
            if (spec.Action == "grant_keyword" && spec.GrantKeyword is null)
                throw new InvalidDataException($"Leader '{leader.Id}': grant_keyword needs a 'keyword'.");
        }
    }
}
