using Godot;

namespace HoldTheLine.Game;

/// <summary>
/// 开发者测试修改器 (dev-only) 的可用性判定。战斗内 Ctrl+Alt+0 切换作弊面板 (见 <see cref="BattleScene"/>)，
/// 但仅当 <see cref="Available"/> 为真——即"开发者模式"成立——时热键才有效。
///
/// <para><see cref="Available"/> 在调试构建 (编辑器运行 / debug 导出) 恒为真；正式 release 导出默认为假，
/// 除非显式用 <c>--dev</c> 命令行参数或 <c>HTL_DEV=1</c> 环境变量开启。这样作弊在发布包里对普通玩家不可用，
/// 而开发者仍能在任意构建里手动打开。只评估一次并缓存。</para>
///
/// <para>作弊只对单机 (LocalGameHost) 生效：联机是服务器权威，面板在联机局里根本不会打开。</para>
/// </summary>
public static class DevCheats
{
    private static bool? _available;

    /// <summary>Whether dev cheats may be used in this build/run (see the class remarks).</summary>
    public static bool Available => _available ??=
        OS.IsDebugBuild()
        || OS.GetEnvironment("HTL_DEV") == "1"
        || System.Array.IndexOf(OS.GetCmdlineArgs(), "--dev") >= 0;
}
