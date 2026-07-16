using System.Text.RegularExpressions;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>
/// Hard constraint #3 (plan §3.1): the presentation layer consumes events and PlayerView only.
/// This scans Godot-side C# sources for forbidden references to authoritative internals.
/// </summary>
public class ArchitectureTests
{
    private static readonly Regex Forbidden = new(
        @"\b(GameState|Resolver|ResolutionContext|GameFactory|EffectEngine)\b",
        RegexOptions.Compiled);

    [Fact]
    public void Godot_scripts_never_touch_authoritative_state()
    {
        var scriptsDir = Path.Combine(RepoPaths.Root, "game", "scripts");
        if (!Directory.Exists(scriptsDir))
            return; // nothing to police yet

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(scriptsDir, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            var match = Forbidden.Match(source);
            if (match.Success)
                offenders.Add($"{Path.GetFileName(file)}: uses '{match.Value}'");
        }

        Assert.True(offenders.Count == 0,
            "Presentation code must only consume events/PlayerView (plan §3.1 #3). Violations:\n" +
            string.Join("\n", offenders));
    }
}
