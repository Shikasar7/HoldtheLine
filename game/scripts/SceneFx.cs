using Godot;

namespace HoldTheLine.Game;

/// <summary>
/// Scene-change fade (docs/18 P4): fade to black (0.22s) → switch scene → fade back in (0.25s).
/// Deliberately NOT an autoload — the overlay CanvasLayer is parented to the tree root, which survives
/// ChangeSceneToFile, and the release pipeline's autoload stripping never has to know about it.
/// A transition in flight ignores further requests (double-clicking 开战 must not switch twice).
/// </summary>
public static class SceneFx
{
    private static bool _busy;

    public static void ChangeScene(Node from, string path) => Run(from, tree => tree.ChangeSceneToFile(path));

    public static void Reload(Node from) => Run(from, tree => tree.ReloadCurrentScene());

    private static void Run(Node from, System.Action<SceneTree> swap)
    {
        if (_busy || from.GetTree() is not { } tree) return;
        _busy = true;

        var layer = new CanvasLayer { Layer = 120 };
        var black = new ColorRect { Color = new Color(0.02f, 0.015f, 0.01f, 0f) };
        black.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        black.MouseFilter = Control.MouseFilterEnum.Stop; // swallow clicks while fading
        layer.AddChild(black);
        tree.Root.AddChild(layer);

        // One chained tween, no async: the tween is bound to the overlay (a root child that survives the
        // scene swap), so every step runs regardless of what ChangeSceneToFile frees mid-way. An awaited
        // ToSignal continuation proved unreliable across the swap — the fade-in never resumed.
        var tw = black.CreateTween();
        tw.TweenProperty(black, "color:a", 1f, 0.22).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
        tw.TweenCallback(Callable.From(() => swap(tree)));
        tw.TweenInterval(0.06);
        tw.TweenProperty(black, "color:a", 0f, 0.25).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        tw.TweenCallback(Callable.From(() =>
        {
            layer.QueueFree();
            _busy = false;
        }));
    }
}
