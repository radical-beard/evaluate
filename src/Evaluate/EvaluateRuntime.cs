using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Lua;

namespace Evaluate;

// The one Godot-facing class. Godot instantiates it from the scene and drives
// its lifecycle — Evaluate itself has no main(). Scripts are auto-discovered, and
// any script that declares a `register:` block is wired into these callbacks.
// Hot reload is on by default. [GlobalClass] so any game project's scene can add
// it by name without a script path.
[GlobalClass]
public partial class EvaluateRuntime : Node
{
    private Loader _loader = null!;
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentQueue<string> _changes = new();
    private int _frame;
    private int _quitAfter = -1;          // -1 = run forever (a real game)
    private string? _screenshot;          // dev: capture a frame to PNG, then quit
    private bool _quitFired;              // on_quit fires exactly once on teardown

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();

        // Test mode: `godot --headless -- --test` runs the enforcement suite.
        if (args.Contains("--test"))
        {
            GetTree().Quit(EvaluateTests.Run(GD.Print));
            return;
        }

        // Docs mode: `godot --headless -- --emit-api <dir>` dumps the full Lua API spec
        // for THIS Godot + Evaluate (ClassDB needs a live engine) and quits. Runs before
        // any script discovery, so it works in a bare consumer project.
        if (ArgValue(args, "--emit-api") is { } emitDir)
        {
            EvaluateDocs.Emit(emitDir, GD.Print);
            GetTree().Quit();
            return;
        }

        _quitAfter = ArgValue(args, "--quit-after") is { } q && int.TryParse(q, out var n) ? n : -1;
        _screenshot = ArgValue(args, "--screenshot");

        // Own the quit path so scripts can persist on exit: a window-close request is
        // turned into an explicit quit (below), and `on_quit` fires once as the tree
        // tears down (covers window-close, a script's get_tree():quit(), and --quit-after).
        GetTree().AutoAcceptQuit = false;

        _loader = new Loader(ReadScript, msg => GD.Print($"[evt] {msg}"));

        // Persistent global root: holds the player and other never-cleared nodes,
        // and parents the swappable per-scene container.
        var globalRoot = new Node { Name = "Global" };
        AddChild(globalRoot);
        _loader.SetGlobalRoot(globalRoot);

        // Auto-discovery: find every script, load the ones that register systems
        // (node scripts are loaded per-node via scene files, not here).
        var scripts = DiscoverScriptNames().ToList();
        GD.Print($"[evaluate] discovered {scripts.Count} script(s): {string.Join(", ", scripts)}");
        _loader.LoadAllSystems(scripts);

        foreach (var sys in _loader.Systems)
            GD.Print($"[evaluate] system registered: {sys.Path} " +
                     (sys.IsGlobal ? "(global)" : $"[{string.Join(",", sys.Scenes)}]"));

        // Build the persistent layer (instantiates manifest nodes + their
        // on_attach), run global on_start, then enter the start scene. A malformed
        // manifest/start scene is logged, not fatal — the game still launches.
        try
        {
            var start = _loader.LoadGlobalLayerFromFile();
            CallHook("on_start");
            _loader.FireSystemsLoad();          // systems' first on_load (after on_start)
            if (start is not null)
            {
                GD.Print($"[evaluate] entering start scene '{start}'");
                _loader.GotoScene(start);
            }
            else
            {
                GD.Print($"[evaluate] no start_scene in {Loader.ManifestName}; running global layer only");
            }
        }
        catch (System.Exception e)
        {
            GD.PushError($"[evaluate] startup load error: {e.Message}");
        }

        GD.Print($"[evaluate] world now has {GetChildCount()} node(s) in the scene tree");

        StartHotReload();
    }

    public override void _Process(double delta)
    {
        if (_loader is null) return;     // test mode already quit

        // Apply any pending hot-reloads on the main thread. A bad edit (e.g. a
        // half-saved, malformed .scene/.toml) is logged and skipped, never crashes.
        while (_changes.TryDequeue(out var changed))
        {
            try { GD.Print($"[evaluate] hot reload: {_loader.ReloadOnChange(changed)}"); }
            catch (System.Exception e) { GD.PushError($"[evaluate] hot reload failed for {changed}: {e.Message}"); }
        }

        // Apply a requested scene switch at a safe point — never mid-hook, so a
        // scene's nodes are never freed while their on_update is running. A
        // malformed/unknown target is logged; the current scene keeps running.
        if (_loader.TakePendingScene() is { } next)
        {
            try { GD.Print($"[evaluate] scene change -> '{next}'"); _loader.GotoScene(next); }
            catch (System.Exception e) { GD.PushError($"[evaluate] failed to enter scene '{next}': {e.Message}"); }
        }

        CallHook("on_update", delta);
        _frame++;

        // Dev: let a couple of frames render, capture a screenshot, then quit.
        if (_screenshot is not null && _frame == 3)
        {
            GetViewport().GetTexture().GetImage().SavePng(_screenshot);
            GD.Print($"[evaluate] screenshot -> {_screenshot}");
            _watcher?.Dispose();
            GetTree().Quit();
            return;
        }

        if (_quitAfter >= 0 && _frame >= _quitAfter)
        {
            _watcher?.Dispose();
            GetTree().Quit();
        }
    }

    private static string? ArgValue(string[] args, string flag)
    {
        var i = System.Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public override void _PhysicsProcess(double delta) => CallHook("on_physics_update", delta);

    public override void _Input(InputEvent @event)
    {
        if (_loader is not null) CallHook("on_input", _loader.Wrap(@event));
    }

    // App-level notifications routed to script hooks. AutoAcceptQuit is off, so a
    // window-close request is turned into an explicit quit (on_quit then fires via
    // _ExitTree). Focus + pause changes fan out to on_focus_*/on_pause/on_resume.
    public override void _Notification(int what)
    {
        if (_loader is null) return;
        switch (what)
        {
            case (int)NotificationWMCloseRequest: GetTree().Quit(); break;
            case (int)NotificationApplicationFocusOut: CallHook("on_focus_out"); break;
            case (int)NotificationApplicationFocusIn: CallHook("on_focus_in"); break;
            case (int)NotificationPaused: CallHook("on_pause"); break;
            case (int)NotificationUnpaused: CallHook("on_resume"); break;
        }
    }

    // The universal quit point: fires `on_quit` exactly once on any teardown, so global
    // systems/nodes can flush persistence. on_quit is for teardown/persistence — the
    // scene tree may already be unwinding, so handlers must not rely on other nodes.
    public override void _ExitTree()
    {
        if (_quitFired || _loader is null) return;
        _quitFired = true;
        CallHook("on_quit");
        _watcher?.Dispose();
    }

    // Drive a registered lifecycle hook across everything active this frame:
    // global + active-scene systems, plus global + active-scene node scripts.
    private void CallHook(string name, params LuaValue[] args)
    {
        if (_loader is null) return;
        foreach (var sys in _loader.ActiveSystems)
            if (sys.Hooks.TryGetValue(name, out var fn)) _loader.Call(fn, args);
        foreach (var node in _loader.ActiveNodes)
            if (node.Hooks.TryGetValue(name, out var fn)) _loader.Call(fn, args);
    }

    // ---- hot reload (default) -------------------------------------------------

    private void StartHotReload()
    {
        var targets = _loader.WatchTargets().ToList();
        GD.Print($"[evaluate] hot reload watching {targets.Count} file(s): {string.Join(", ", targets)}");

        var dir = ProjectSettings.GlobalizePath("res://scripts");
        if (!Directory.Exists(dir)) return;

        _watcher = new FileSystemWatcher(dir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        // Fires on a background thread; just enqueue and reload on the main thread.
        void Enqueue(object _, FileSystemEventArgs e) =>
            _changes.Enqueue(Path.GetRelativePath(dir, e.FullPath).Replace('\\', '/'));
        _watcher.Changed += Enqueue;
        _watcher.Created += Enqueue;
        _watcher.Renamed += (_, e) =>
            _changes.Enqueue(Path.GetRelativePath(dir, e.FullPath).Replace('\\', '/'));
    }

    // ---- discovery ------------------------------------------------------------

    private static string ReadScript(string name) =>
        Godot.FileAccess.GetFileAsString($"res://scripts/{name}");

    private static IEnumerable<string> DiscoverScriptNames()
    {
        var dir = ProjectSettings.GlobalizePath("res://scripts");
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.evt", SearchOption.AllDirectories))
            yield return Path.GetRelativePath(dir, f).Replace('\\', '/');
    }
}
