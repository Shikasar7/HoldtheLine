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

    /// <summary>
    /// Dependency direction (M2 plan §7): the networking layer and the server are engine-agnostic —
    /// they must never take a Godot dependency, or the "single rules engine, many hosts" thesis
    /// leaks presentation concerns into the authoritative path.
    /// </summary>
    [Theory]
    [InlineData("HoldTheLine.Net")]
    [InlineData("HoldTheLine.Server")]
    [InlineData("HoldTheLine.BotClient")]
    public void Networking_projects_never_reference_Godot(string projectName)
    {
        var projectDir = Path.Combine(RepoPaths.Root, "src", projectName);
        if (!Directory.Exists(projectDir))
            return;

        var offenders = new List<string>();

        foreach (var csproj in Directory.EnumerateFiles(projectDir, "*.csproj", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(csproj);
            // Only an actual Godot SDK or reference counts — not the word "Godot" in prose/description.
            if (Regex.IsMatch(text, @"(Sdk|Include)\s*=\s*""[^""]*Godot", RegexOptions.IgnoreCase))
                offenders.Add($"{Path.GetFileName(csproj)}: references Godot");
        }

        foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            if (Regex.IsMatch(File.ReadAllText(file), @"\busing\s+Godot\b"))
                offenders.Add($"{Path.GetFileName(file)}: uses Godot namespace");
        }

        Assert.True(offenders.Count == 0,
            $"{projectName} must stay engine-agnostic (plan §7). Violations:\n" + string.Join("\n", offenders));
    }
}
