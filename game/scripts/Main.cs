using Godot;
using HoldTheLine.Rules;

namespace HoldTheLine.Game;

/// <summary>
/// Scaffold entry point (P0). The battle scene arrives in P3.
/// Architecture rule (plan §3.1 #3): scripts under game/scripts consume ONLY commands, events,
/// and PlayerView from the rules assembly — never authoritative state. Enforced by ArchitectureTests.
/// </summary>
public partial class Main : Node2D
{
    public override void _Ready()
    {
        GD.Print($"Hold the Line — prototype scaffold. Rules v{RulesInfo.Version}");
    }
}
