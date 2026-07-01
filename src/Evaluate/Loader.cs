using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;
using Lua;
using Lua.Standard;

namespace Evaluate;

// A loaded "system" script: one that `register`s lifecycle hooks with the engine.
// A system belongs to the scenes in `Scenes`; an empty list means it is global
// (always active, never torn down).
public sealed class LoadedSystem
{
    public string Path = "";
    public List<string> Scenes = new();
    public List<string> Configs = new();   // declared config TOMLs (a change fires on_load)
    public readonly Dictionary<string, LuaValue> Hooks = new();

    public bool IsGlobal => Scenes.Count == 0;

    public LuaValue OnStart => Hooks.TryGetValue("on_start", out var v) ? v : LuaValue.Nil;
    public LuaValue OnUpdate => Hooks.TryGetValue("on_update", out var v) ? v : LuaValue.Nil;
}

// A loaded node-behavior script (`*.node.evt`) bound to one live Godot node.
// Unlike systems, node scripts are instantiated PER node (each gets its own
// sandbox with `self` bound), so the same script can drive many nodes.
public sealed class LoadedNode
{
    public string Path = "";
    public Godot.GodotObject Node = null!;
    public readonly Dictionary<string, LuaValue> Hooks = new();
    public List<string> Configs = new();   // declared config TOMLs (a change to one fires on_load)
    // Per-instance values the scene file supplied (`params = {..}`). Kept so a script
    // reload re-validates and re-exposes the SAME params (the scene didn't change).
    public IReadOnlyDictionary<string, object> Params = EmptyParams;

    internal static readonly Dictionary<string, object> EmptyParams = new();
}

// The heart of Evaluate. Turns a script's frontmatter signature into a real,
// capability-scoped runtime: per-script sandbox -> custom require -> returns
// contract. Built on one shared LuaState so handles pass freely between scripts.
public sealed class Loader
{
    private readonly LuaState _lua;
    private readonly Func<string, string> _readScript;
    private readonly Action<string> _log;
    private readonly GodotBinder _godotBinder;
    private Node? _globalRoot;              // persistent layer; never cleared
    private Node? _sceneContainer;          // current swappable scene; freed on scene change
    private string? _activeScene;           // name of the active scene
    private string? _pendingScene;          // requested by scene.goto, applied next frame
    private Persistence? _persistence;      // lazily opened SQLite save store (flat kv)
    private Sql? _sql;                       // lazily opened full-SQL capability (game schema)

    private readonly LuaTable _std;
    private readonly LuaTable _godot;     // ambient: godot.<Type> resolves lazily
    private readonly LuaValue _loadFn;
    private readonly Dictionary<string, LuaValue> _requireCache = new();
    private readonly Dictionary<string, LuaTable> _tomlCache = new();

    // Require dependency graph. Kept so a changed module reloads everything that
    // (transitively) requires it, and a require cycle is rejected up front instead of
    // overflowing the stack. Populated by Require() for BOTH forms — frontmatter
    // `require:` bindings and inline `require(path)` — since both funnel through it.
    //   `_dependents[dep]`      = scripts to reload when `dep` changes (dep -> consumers)
    //   `_dependencies[consumer]` = what `consumer` requires (cleared + rebuilt on re-run)
    //   `_loadStack`            = scripts currently being Run; its top is the consumer of
    //                             any require made now, and a path already on it is a cycle.
    private readonly Dictionary<string, HashSet<string>> _dependents = new();
    private readonly Dictionary<string, HashSet<string>> _dependencies = new();
    private readonly List<string> _loadStack = new();
    // (scene-namespace, hook) -> owning script. A hook may be claimed once per
    // scene namespace; global systems share the reserved "<global>" namespace.
    private readonly Dictionary<string, string> _claimedHooks = new();

    private readonly List<LoadedSystem> _systems = new();
    private readonly List<LoadedNode> _globalNodes = new();              // persistent node scripts
    private readonly List<Node> _globalLayerRoots = new();               // manifest's instantiated root nodes (for reload)
    private SceneSpec? _globalManifest;                                  // last-loaded manifest (for live rebuild)
    private readonly List<LoadedNode> _sceneNodes = new();               // active-scene node scripts
    private readonly HashSet<string> _loadedScripts = new();             // every .evt Run so far
    private readonly HashSet<string> _configFiles = new();               // every declared toml
    private readonly HashSet<string> _assetFiles = new();                // every declared asset
    private readonly HashSet<string> _sceneFiles = new();               // every declared *.scene file

    private const string GlobalNs = "<global>";

    // Systems discovered/registered in this project (scripts with a register: block).
    public IReadOnlyList<LoadedSystem> Systems => _systems;

    // The name of the active scene (null before the first scene change).
    public string? ActiveScene => _activeScene;

    // Input state the host can toggle to simulate the player pressing keys.
    public HashSet<string> Pressed { get; } = new();

    // The language-level globals every sandbox gets (capability-free). Exposed
    // internally so the docs emitter reads the SAME list the sandbox copies in
    // (Loader.cs BuildSandbox), never a parallel hand-maintained one.
    internal static readonly string[] SafeGlobals =
        { "pairs", "ipairs", "next", "type", "tostring", "tonumber",
          "error", "assert", "select", "string", "math", "table",
          // Metatable toolkit: lets scripts build classes/objects the idiomatic
          // Lua way (setmetatable + __index). Pure language features with no
          // engine/IO reach, so they are capability-free like `std`. The sandbox
          // scopes *capabilities*, not language expressiveness, and resource/DoS
          // limits are out of scope by design — so exposing these is safe. `pcall`
          // remains intentionally withheld (errors should surface, not be swallowed).
          "setmetatable", "getmetatable", "rawget", "rawset", "rawequal", "rawlen" };

    public Loader(Func<string, string> readScript, Action<string> log)
    {
        _readScript = readScript;
        _log = log;

        _lua = LuaState.Create();
        _lua.OpenStandardLibraries();
        _godotBinder = new GodotBinder(_lua);
        _lua.Environment["print"] = new LuaFunction((ctx, ct) =>
        {
            var sb = new StringBuilder();
            for (int i = 0; i < ctx.ArgumentCount; i++)
            {
                if (i > 0) sb.Append('\t');
                sb.Append(ctx.GetArgument(i).ToString());
            }
            _log(sb.ToString());
            return new(0);
        });

        _std = Std.Build();

        // Ambient `godot` namespace: godot.<Type> resolves & caches on first use.
        _godot = new LuaTable();
        var godotMt = new LuaTable();
        godotMt["__index"] = new LuaFunction((ctx, ct) =>
        {
            var typeName = ctx.GetArgument(1).Read<string>();
            var binding = _godotBinder.Resolve(typeName) ?? LuaValue.Nil;
            if (binding.Type != LuaValueType.Nil) _godot[typeName] = binding;   // memoize
            ctx.Return(binding);
            return new(1);
        });
        _godot.Metatable = godotMt;

        _loadFn = _lua.Environment["load"];
    }

    // The persistent global-root node (set by the host). The `world` API wraps
    // it, and both global and scene nodes are parented beneath it.
    public void SetGlobalRoot(Node node) => _globalRoot = node;

    // ---- public entry points -------------------------------------------------

    // Auto-discovery: load every discovered script that declares a `register:`
    // block as a system. The host supplies the file list (directory scan).
    public void LoadAllSystems(IEnumerable<string> scriptFiles)
    {
        foreach (var file in scriptFiles)
        {
            if (!file.EndsWith(".evt") || file.EndsWith(".node.evt")) continue;
            // One malformed script (or its malformed config) must not stop the rest
            // from loading — log it and move on.
            try
            {
                if (Frontmatter.Parse(_readScript(file)).Register.Count == 0) continue;
                LoadSystem(file);
            }
            catch (Exception e)
            {
                _log($"failed to load system {file}: {e.Message}");
            }
        }
    }

    public LoadedSystem LoadSystem(string path)
    {
        var sys = _systems.Find(s => s.Path == path) ?? new LoadedSystem { Path = path };
        // A system's `world` is the persistent global root.
        var (env, _, fm) = Run(path, new LoadContext(_globalRoot, null));

        sys.Scenes = fm.Scenes;
        sys.Configs = fm.Configs;
        // Each scene the system belongs to is a claim namespace; a global system
        // (no scenes:) claims in the reserved global namespace.
        var namespaces = fm.Scenes.Count > 0 ? fm.Scenes : new List<string> { GlobalNs };

        foreach (var name in fm.Register)
        {
            if (!SystemHooks.Contains(name))
                throw new EvaluateException(
                    $"{path} registers unknown hook '{name}' (valid: {string.Join(", ", SystemHooks)})");
            if ((name == "on_start" || name == "on_quit") && fm.Scenes.Count > 0)
                throw new EvaluateException(
                    $"{path} registers '{name}' but is scene-scoped; '{name}' is global-only " +
                    "(use 'on_enter'/'on_exit' for per-scene bookends)");
            if ((name == "on_enter" || name == "on_exit") && fm.Scenes.Count == 0)
                throw new EvaluateException(
                    $"{path} registers '{name}' but is global; '{name}' is for scene-scoped systems");
            if (env[name].Type != LuaValueType.Function)
                throw new EvaluateException($"{path} registers '{name}' but never defines it");

            // One registration per hook PER scene namespace — but a script may
            // re-register its own hooks on reload.
            foreach (var ns in namespaces)
            {
                var key = ns + " " + name;
                if (_claimedHooks.TryGetValue(key, out var owner) && owner != path)
                    throw new EvaluateException(
                        $"hook '{name}' is already registered for scene '{ns}' by '{owner}'; " +
                        $"only one registration per scene is allowed (attempted again by '{path}')");
                _claimedHooks[key] = path;
            }

            sys.Hooks[name] = env[name];
        }

        if (!_systems.Contains(sys)) _systems.Add(sys);
        return sys;
    }

    // Lifecycle hooks a SYSTEM script may register. on_start fires once (global systems);
    // on_load runs after on_start + on every hot reload (rebuildable setup); on_unload fires
    // before a reload tears the old setup down; on_enter/on_exit fire on scene activation;
    // on_focus_in/out + on_pause/on_resume on app focus / tree pause; the rest fire while active.
    public static readonly string[] SystemHooks =
        { "on_start", "on_load", "on_unload", "on_quit", "on_enter", "on_exit",
          "on_focus_in", "on_focus_out", "on_pause", "on_resume",
          "on_update", "on_physics_update", "on_input" };

    // Lifecycle hooks a NODE script (`*.node.evt`) may register. on_attach fires ONCE, when
    // the node + its script first enter the tree (never again); on_load runs EVERY (re)load —
    // right after on_attach on first load AND on each hot reload of the script or a declared
    // config (so imperative builders rebuild; make it idempotent); on_exit when its container
    // is freed; the rest fire while alive. Each runs with `self` bound.
    public static readonly string[] NodeHooks =
        { "on_attach", "on_load", "on_unload", "on_update", "on_physics_update", "on_input",
          "on_exit", "on_quit", "on_focus_in", "on_focus_out", "on_pause", "on_resume" };

    // Wrap a live Godot object for Lua (used by the host to pass e.g. InputEvents).
    public LuaValue Wrap(GodotObject o) => _godotBinder.WrapInstance(o);

    // ---- global + scene layer lifetime ---------------------------------------

    // The reserved manifest naming the persistent layer + the starting scene.
    public const string ManifestName = "global.scene";

    // Read + parse the global manifest, build its persistent layer, and return
    // the scene to start in (or null if the manifest declares none).
    public string? LoadGlobalLayerFromFile()
    {
        _sceneFiles.Add(ManifestName);
        return LoadGlobalLayer(SceneFile.Parse(_readScript(ManifestName)));
    }

    // Build the persistent global layer: instantiate the manifest's nodes under
    // the global root, attach their node scripts, fire on_attach. Never torn down.
    public string? LoadGlobalLayer(SceneSpec manifest)
    {
        if (_globalRoot is null) throw new EvaluateException("LoadGlobalLayer called before SetGlobalRoot");
        _globalManifest = manifest;
        InstantiateTree(manifest, _globalRoot, _globalNodes, _globalLayerRoots);
        foreach (var n in _globalNodes) Fire(n, "on_attach");
        foreach (var n in _globalNodes) Fire(n, "on_load");
        return manifest.StartScene;
    }

    // Switch the active scene: tear the current scene container down wholesale,
    // instantiate the new scene under a fresh container, and move hook dispatch
    // to it. The global layer (player, etc.) is untouched.
    public void GotoScene(string name)
    {
        if (_globalRoot is null) throw new EvaluateException("GotoScene called before SetGlobalRoot");

        // Leaving the current scene: on_exit for its nodes + scene systems.
        if (_activeScene is not null)
        {
            foreach (var n in _sceneNodes) Fire(n, "on_exit");
            foreach (var s in SystemsFor(_activeScene)) FireSystem(s, "on_exit");
        }
        _sceneNodes.Clear();
        if (_sceneContainer is not null)
        {
            _globalRoot.RemoveChild(_sceneContainer);   // free the name slot NOW, so a
            _sceneContainer.QueueFree();                // same-name rebuild keeps its name
        }

        // Entering the new scene under a fresh container.
        var spec = SceneFile.Parse(_readScript(SceneFileName(name)));
        _sceneFiles.Add(SceneFileName(name));
        if (spec.Nodes.Count == 0)
            _log($"scene '{name}' has no nodes — '{SceneFileName(name)}' is empty or missing");
        var container = new Node { Name = name };
        _globalRoot.AddChild(container);
        _sceneContainer = container;
        _activeScene = name;
        InstantiateTree(spec, container, _sceneNodes);
        foreach (var n in _sceneNodes) Fire(n, "on_attach");
        foreach (var n in _sceneNodes) Fire(n, "on_load");
        foreach (var s in SystemsFor(name)) FireSystem(s, "on_enter");
    }

    // A pending scene.change target (applied by the host at the next frame
    // boundary, never mid-hook), consumed once.
    public string? TakePendingScene()
    {
        var p = _pendingScene;
        _pendingScene = null;
        return p;
    }

    // Fire on_load on every loaded system — their first-load rebuild point. The host calls
    // this once after on_start (node on_load is fired by the build path instead).
    public void FireSystemsLoad()
    {
        foreach (var s in _systems) FireSystem(s, "on_load");
    }

    // Systems + node scripts whose hooks the host should drive this frame.
    public IEnumerable<LoadedSystem> ActiveSystems =>
        _systems.Where(s => s.IsGlobal || (_activeScene is not null && s.Scenes.Contains(_activeScene)));
    public IEnumerable<LoadedNode> ActiveNodes => _globalNodes.Concat(_sceneNodes);

    private IEnumerable<LoadedSystem> SystemsFor(string scene) =>
        _systems.Where(s => s.Scenes.Contains(scene));

    private void Fire(LoadedNode n, string hook)
    {
        if (n.Hooks.TryGetValue(hook, out var fn)) Call(fn);
    }
    private void FireSystem(LoadedSystem s, string hook)
    {
        if (s.Hooks.TryGetValue(hook, out var fn)) Call(fn);
    }

    private static string SceneFileName(string name) => name + ".scene";

    // Instantiate a scene/manifest node tree under `container`: SceneBuilder builds
    // the visual tree (types, properties, hierarchy) and, via the visit callback,
    // we attach each node's `*.node.evt` behavior script (bound to that node).
    private void InstantiateTree(SceneSpec spec, Node container, List<LoadedNode> nodes, List<Node>? roots = null)
    {
        var conns = new List<(Node from, ConnectionSpec spec)>();
        IReadOnlyList<NodeSpec> Resolve(string name) => SceneFile.Parse(_readScript(SceneFileName(name))).Nodes;

        void Attach(NodeSpec ns, Node node)
        {
            if (!string.IsNullOrEmpty(ns.Script))
                nodes.Add(LoadNodeScript(ns.Script!, node, container, ns.Params));
            foreach (var c in ns.Connections) conns.Add((node, c));
        }
        foreach (var ns in spec.Nodes)
        {
            var node = SceneBuilder.BuildNode(ns, _godotBinder, Attach, Resolve);
            container.AddChild(node);
            roots?.Add(node);
            node.Owner = container;                 // owner chain -> %UniqueName resolves at runtime
            SetOwnerRecursive(node, container);
        }

        // Wire declarative signal connections now the whole tree exists; `to` resolves from
        // the scene container (built-in target methods; Lua handlers use obj:connect instead).
        foreach (var (from, c) in conns)
        {
            var target = container.GetNodeOrNull(c.To);
            if (target is not null) from.Connect(c.Signal, new Callable(target, c.Method));
            else _log($"connection on '{from.Name}': target '{c.To}' not found");
        }
    }

    private static void SetOwnerRecursive(Node node, Node owner)
    {
        foreach (var child in node.GetChildren()) { child.Owner = owner; SetOwnerRecursive(child, owner); }
    }

    // Load a `*.node.evt` bound to one live node: fresh sandbox with `self` + the scene's
    // per-instance `params`, then collect its registered node hooks.
    private LoadedNode LoadNodeScript(string path, GodotObject self, Node container,
        IReadOnlyDictionary<string, object> sceneParams)
    {
        var (env, _, fm) = Run(path, new LoadContext(container, self, sceneParams));
        var ln = new LoadedNode { Path = path, Node = self, Configs = fm.Configs, Params = sceneParams };
        foreach (var name in fm.Register)
        {
            if (!NodeHooks.Contains(name))
                throw new EvaluateException(
                    $"{path} registers unknown node hook '{name}' (valid: {string.Join(", ", NodeHooks)})");
            if (env[name].Type != LuaValueType.Function)
                throw new EvaluateException($"{path} registers '{name}' but never defines it");
            ln.Hooks[name] = env[name];
        }
        return ln;
    }

    // ---- hot reload + watch targets ------------------------------------------

    // Files the host should watch: every loaded script, declared config, and
    // declared asset. (Assets are declared in frontmatter precisely so we know
    // to watch them.)
    public IEnumerable<string> WatchTargets()
    {
        foreach (var s in _loadedScripts) yield return s;   // .evt + .node.evt
        foreach (var c in _configFiles) yield return c;
        foreach (var a in _assetFiles) yield return a;
        foreach (var sc in _sceneFiles) yield return sc;    // *.scene + manifest
    }

    // Apply a live change to `path`. Returns a short description of what happened.
    // Engine entities are preserved; a changed system re-runs its body to refresh
    // its hook closures, but on_start is NOT re-invoked (no re-spawn).
    public string ReloadOnChange(string path)
    {
        // .evt scripts (systems, node scripts, plain modules) reload through the require
        // graph, so a change also refreshes everything that requires it (transitively).
        if (path.EndsWith(".evt")) return ReloadModuleGraph(path);

        // Scene file: rebuild the active scene in place; others rebuild on entry.
        // The global manifest is the heavy case — it owns persistent nodes, so a
        // restart applies it cleanly.
        if (path == ManifestName) return ReloadGlobalLayer();
        if (path.EndsWith(".scene"))
        {
            if (_activeScene is not null && SceneFileName(_activeScene) == path)
            {
                GotoScene(_activeScene);
                return $"reloaded scene {path} (active scene rebuilt)";
            }
            return $"invalidated scene {path} (rebuilt on next entry)";
        }

        if (path.EndsWith(".toml"))
        {
            // Re-read into the cache so every live `config` view reflects the new
            // values immediately. A malformed/half-saved file keeps the last-good
            // values rather than blanking config mid-frame.
            try
            {
                _tomlCache[path] = BuildTomlTable(path);
                // A node/system that builds from this config reads the live view at build time,
                // so re-fire on_unload + on_load to let it rebuild against the new values.
                foreach (var ln in _globalNodes.Concat(_sceneNodes))
                    if (ln.Configs.Contains(path)) { Fire(ln, "on_unload"); Fire(ln, "on_load"); }
                foreach (var s in _systems)
                    if (s.Configs.Contains(path)) { FireSystem(s, "on_unload"); FireSystem(s, "on_load"); }
                return $"reloaded config {path} (live)";
            }
            catch (EvaluateException e)
            {
                return $"kept last-good config {path} ({e.Message})";
            }
        }
        return $"noted change to asset {path} (would re-import)";
    }

    // Reload a changed `.evt` and everything that (transitively) requires it. All affected
    // modules are evicted and their edges cleared FIRST, so when the concrete consumers
    // (systems + live node scripts) re-run, each require() rebuilds against fresh
    // dependencies in natural order — no matter which consumer runs first. Plain modules
    // with no live presence stay evicted and rebuild lazily on next use.
    private string ReloadModuleGraph(string changed)
    {
        var affected = AffectedByChange(changed);
        foreach (var p in affected) { _requireCache.Remove(p); ClearDependencies(p); }

        var messages = new List<string>();
        foreach (var p in affected)
        {
            if (p.EndsWith(".node.evt"))
            {
                int n = RefreshNodeScript(p);
                messages.Add(n > 0
                    ? $"reloaded node script {p} ({n} live instance(s) refreshed)"
                    : $"invalidated node script {p} (no live instances)");
            }
            else if (_systems.Find(s => s.Path == p) is { } sys)
            {
                FireSystem(sys, "on_unload");   // tear down old closures before the body re-runs
                LoadSystem(p);                  // re-run body -> fresh hooks + fresh require bindings
                FireSystem(sys, "on_load");     // rebuild against the fresh body
                messages.Add($"reloaded system {p} (hooks refreshed, entities preserved)");
            }
            else
            {
                messages.Add($"invalidated module {p} (re-required on next use)");
            }
        }
        return string.Join("; ", messages);
    }

    // Re-run a changed node script's body on each live instance, refreshing its
    // hook closures while preserving the node and its `self`.
    private int RefreshNodeScript(string path)
    {
        int RefreshIn(List<LoadedNode> list, Node? container)
        {
            int count = 0;
            foreach (var ln in list)
            {
                if (ln.Path != path) continue;
                Fire(ln, "on_unload");   // old closures still valid: tear down BEFORE the body re-runs
                var (env, _, fm) = Run(path, new LoadContext(container, ln.Node, ln.Params));
                ln.Hooks.Clear();
                ln.Configs = fm.Configs;
                foreach (var name in fm.Register)
                    if (NodeHooks.Contains(name) && env[name].Type == LuaValueType.Function)
                        ln.Hooks[name] = env[name];
                Fire(ln, "on_load");   // the body's upvalues are fresh; let it rebuild imperative content
                count++;
            }
            return count;
        }
        return RefreshIn(_globalNodes, _globalRoot) + RefreshIn(_sceneNodes, _sceneContainer);
    }

    // Rebuild the persistent global layer in place from the re-read manifest: fire
    // on_exit for its node scripts, detach + free the manifest's nodes, then
    // re-instantiate and fire on_attach. The active scene and any world-spawned nodes
    // are untouched. NOTE: persistent nodes are recreated, so their runtime state
    // resets — this applies a *structural* manifest change live (it is not
    // state-preserving the way a node-script reload is). Detach-then-QueueFree frees
    // the old names immediately so the rebuilt nodes keep their declared names.
    private string ReloadGlobalLayer()
    {
        if (_globalRoot is null || _globalManifest is null)
            return $"invalidated global manifest {ManifestName} (no global layer loaded yet)";

        foreach (var n in _globalNodes) Fire(n, "on_exit");
        _globalNodes.Clear();
        foreach (var root in _globalLayerRoots)
        {
            _globalRoot.RemoveChild(root);   // free the name slot now, before re-adding
            root.QueueFree();
        }
        _globalLayerRoots.Clear();

        var manifest = SceneFile.Parse(_readScript(ManifestName));
        _globalManifest = manifest;
        InstantiateTree(manifest, _globalRoot, _globalNodes, _globalLayerRoots);
        foreach (var n in _globalNodes) Fire(n, "on_attach");
        foreach (var n in _globalNodes) Fire(n, "on_load");
        return $"reloaded global manifest {ManifestName} (persistent layer rebuilt)";
    }

    public void Call(LuaValue fn, params LuaValue[] args) => Sync(_lua.CallAsync(fn, args));

    // ---- sandbox + require ----------------------------------------------------

    // What a script's sandbox is wired to: the `world` container its spawned
    // nodes parent to, (for node scripts) the `self` node it drives, and the
    // per-instance `params` the scene supplied for that node (empty otherwise).
    private readonly record struct LoadContext(
        GodotObject? Container, GodotObject? Self,
        IReadOnlyDictionary<string, object>? Params = null);

    private (LuaTable env, LuaValue ret, Frontmatter fm) Run(string path, LoadContext ctx)
    {
        // A path already mid-load means a require cycle (frontmatter or inline). Reject it
        // with the offending chain instead of recursing into a stack overflow.
        if (_loadStack.Contains(path))
            throw new EvaluateException(
                $"require cycle: {string.Join(" -> ", _loadStack)} -> {path} " +
                "(a script cannot require itself, directly or transitively)");
        _loadStack.Add(path);
        try
        {
            var fm = Frontmatter.Parse(_readScript(path));

            // Record watch targets: the script itself, its configs, its assets.
            _loadedScripts.Add(path);
            foreach (var c in fm.Configs) _configFiles.Add(c);
            foreach (var a in fm.Assets) _assetFiles.Add(a);

            var env = BuildSandbox(path, fm, ctx);

            var loaded = Sync(_lua.CallAsync(_loadFn, new LuaValue[] { fm.Body, path, "t", env }));
            if (loaded[0].Type != LuaValueType.Function)
            {
                var err = loaded.Length > 1 ? loaded[1].ToString() : "syntax error";
                throw new EvaluateException($"failed to load {path}: {err}");
            }

            var ret = Sync(_lua.CallAsync(loaded[0], Array.Empty<LuaValue>()));
            return (env, ret.Length > 0 ? ret[0] : LuaValue.Nil, fm);
        }
        finally { _loadStack.RemoveAt(_loadStack.Count - 1); }
    }

    // The ONLY environment the script body sees: declared capabilities + std +
    // a few safe primitives. Standard libs like os/io exist on the shared state
    // but are never copied in, so a script cannot reach them.
    private LuaTable BuildSandbox(string path, Frontmatter fm, LoadContext ctx)
    {
        var env = new LuaTable();
        foreach (var name in SafeGlobals) env[name] = _lua.Environment[name];
        env["print"] = _lua.Environment["print"];
        env["std"] = _std;
        env["godot"] = _godot;        // ambient: Godot types available by default

        // Node scripts drive one node, exposed as `self`.
        if (ctx.Self is not null) env["self"] = _godotBinder.WrapInstance(ctx.Self);

        env["config"] = BuildConfigView(fm.Configs);

        // `params`: per-instance values the scene supplied for this node, resolved against
        // the script's declared `params:` (defaults filled, types checked, undeclared/missing
        // rejected). Only node scripts get it — systems aren't attached to a scene node, so
        // they have no instance to be parameterized. A script that declares `params:` but is
        // loaded as a system would never receive values, so that is rejected up front.
        if (ctx.Self is not null)
            env["params"] = BuildParamsView(path, fm.Params, ctx.Params);
        else if (fm.Params.Count > 0)
            throw new EvaluateException(
                $"{path} declares 'params:' but is not a node script; params are per-node " +
                "(declare them in a '*.node.evt' attached via a scene, not a system script)");

        foreach (var api in fm.Apis)
        {
            if (api.StartsWith("godot:")) continue;     // godot is ambient; declaration optional
            env[api] = BuildApi(path, api, ctx);
        }

        env["require"] = new LuaFunction((c, ct) =>
        {
            c.Return(Require(c.GetArgument<string>(0)));
            return new(1);
        });

        // Frontmatter `require:` bindings — resolve each declared module up front and
        // bind it as a sandbox local, so composition is declared in the signature
        // rather than restated as `local x = require("…")` boilerplate atop the body.
        // Resolved via the SAME custom require (cache + returns-narrowing) as an inline
        // call, so `require: { base: p }` yields exactly what `require(p)` would. Done
        // last so a binding cannot silently shadow std/godot/config/params/self, a
        // declared api, a language global, or the `require` function itself.
        foreach (var req in fm.Requires)
        {
            if (env[req.Name].Type != LuaValueType.Nil)
                throw new EvaluateException(
                    $"{path}: require binding '{req.Name}' collides with a reserved or declared " +
                    "sandbox name (std/godot/config/params/self, an api, a language global, " +
                    "the require function, or another require binding)");
            env[req.Name] = Require(req.Path);
        }

        return env;
    }

    public LuaValue Require(string path)
    {
        // Record the edge on EVERY require — even a cache hit — so the consumer is
        // reloaded whenever `path` later changes. The consumer is whatever is loading
        // now (top of the stack); a top-level require (empty stack) has no consumer.
        if (_loadStack.Count > 0) AddDependency(_loadStack[^1], path);

        if (_requireCache.TryGetValue(path, out var cached)) return cached;
        var (_, ret, fm) = Run(path, new LoadContext(_globalRoot, null));
        var handle = Narrow(path, ret, fm);
        _requireCache[path] = handle;
        return handle;
    }

    // Record "consumer requires dep" in both directions. Self-edges are skipped (a
    // self-require is already rejected as a cycle by Run).
    private void AddDependency(string consumer, string dep)
    {
        if (consumer == dep) return;
        (_dependents.TryGetValue(dep, out var d) ? d : _dependents[dep] = new()).Add(consumer);
        (_dependencies.TryGetValue(consumer, out var c) ? c : _dependencies[consumer] = new()).Add(dep);
    }

    // Drop all of `consumer`'s outgoing edges. Called before its body re-runs so a
    // require the edit removed no longer leaves a stale dependency; the re-run rebuilds
    // whatever it still requires.
    private void ClearDependencies(string consumer)
    {
        if (!_dependencies.TryGetValue(consumer, out var deps)) return;
        foreach (var dep in deps)
            if (_dependents.TryGetValue(dep, out var set)) set.Remove(consumer);
        deps.Clear();
    }

    // `changed` plus every script that (transitively) requires it — the full set a
    // change must refresh.
    private HashSet<string> AffectedByChange(string changed)
    {
        var affected = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(changed);
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            if (!affected.Add(p)) continue;                 // guards graph cycles + diamonds
            if (_dependents.TryGetValue(p, out var consumers))
                foreach (var c in consumers) stack.Push(c);
        }
        return affected;
    }

    // Enforce the returns contract: a fresh table containing ONLY the declared
    // members/accessors. A caller cannot reach anything undeclared.
    private LuaValue Narrow(string path, LuaValue module, Frontmatter fm)
    {
        if (fm.Returns.Count == 0) return module;
        if (module.Type != LuaValueType.Table)
            throw new EvaluateException($"{path} declares returns but did not return a table");

        var mod = module.Read<LuaTable>();
        var handle = new LuaTable();
        foreach (var spec in fm.Returns)
        {
            if (!spec.CanGet && !spec.CanSet)   // plain exposed member (e.g. a factory's spawn)
            {
                var member = mod[spec.Name];
                if (member.Type == LuaValueType.Nil)
                    throw new EvaluateException($"{path} declares '{spec.Name}' but exposes no such member");
                handle[spec.Name] = member;
                continue;
            }
            foreach (var accessor in spec.RequiredAccessors())   // get/set property -> get_/set_
            {
                if (mod[accessor].Type != LuaValueType.Function)
                    throw new EvaluateException(
                        $"{path} declares '{spec.Name}' ({Access(spec)}) but exposes no '{accessor}'");
                handle[accessor] = mod[accessor];
            }
        }
        return handle;
    }

    private static string Access(ReturnSpec s) =>
        (s.CanGet ? "get " : "") + (s.CanSet ? "set " : "") + s.Type;

    // ---- engine APIs ----------------------------------------------------------

    // The capability APIs a script may declare in `apis:`. Single source of truth:
    // the BuildApi switch dispatches on these names and the docs emitter walks them,
    // so a newly-added api (a new switch arm + array entry) self-documents.
    internal static readonly string[] ApiNames = { "input", "world", "scene", "save", "sql" };

    private LuaValue BuildApi(string path, string name, LoadContext ctx) => name switch
    {
        "input" => BuildInputApi(),
        "world" => BuildWorldApi(path, ctx),
        "scene" => BuildSceneApi(),
        "save" => BuildSaveApi(),
        "sql" => BuildSqlApi(),
        _ => throw new EvaluateException($"{path} requests unknown api '{name}'"),
    };

    // Build each table-shaped capability api so the docs emitter can enumerate its
    // members by walking the live LuaTable keys (no hardcoded member list). `world`
    // is a wrapped Godot node, not a table, so it is reported separately by name.
    // Side-effecting builders (sql opens a db, save touches user://) are tolerated:
    // this only runs under the `--emit-api` doc path, never in a real game.
    internal IReadOnlyList<(string Name, LuaTable Table)> DocApiTables()
    {
        var result = new List<(string, LuaTable)>();
        foreach (var name in ApiNames)
        {
            if (name == "world") continue;   // a wrapped instance, documented as a note
            try
            {
                if (BuildApi("<docs>", name, new LoadContext(_globalRoot, null)).TryRead<LuaTable>(out var table))
                    result.Add((name, table));
            }
            catch (System.Exception e) { _log($"docs: could not build api '{name}': {e.Message}"); }
        }
        return result;
    }

    // `sql` exposes full SQL to scripts; the game owns its schema (slots, inventory, …),
    // the framework owns the connection, durability, async, crash-safety:
    //   sql.exec(stmt[, params])       - a write; returns { changes, last_insert_id } (sync)
    //   sql.exec_async(stmt[, params]) - a fire-and-forget write (returns immediately)
    //   sql.query(stmt[, params])      - a read; a list of column->value row tables
    //   sql.query_row(stmt[, params])  - the first row (or nil)
    //   sql.transaction(fn)            - run fn inside BEGIN/COMMIT (ROLLBACK if it errors)
    //   sql.flush()                    - drain pending async writes (e.g. from on_quit)
    //   sql.snapshot(path)             - a consistent backup copy of the whole DB
    // Params bind positionally to @p1, @p2, ... from a Lua list (number/string/bool/nil).
    private LuaValue BuildSqlApi()
    {
        _sql ??= new Sql(_log);
        var api = new LuaTable();

        api["exec"] = new LuaFunction((c, ct) =>
        {
            var (ch, id) = _sql.ExecStmt(c.GetArgument<string>(0), ReadParams(c, 1), wait: true);
            var r = new LuaTable();
            r["changes"] = (double)ch; r["last_insert_id"] = (double)id;
            c.Return(r);
            return new(1);
        });
        api["exec_async"] = new LuaFunction((c, ct) =>
        {
            _sql.ExecStmt(c.GetArgument<string>(0), ReadParams(c, 1), wait: false);
            return new(0);
        });
        api["query"] = new LuaFunction((c, ct) =>
        {
            c.Return(RowsToLua(_sql.QueryStmt(c.GetArgument<string>(0), ReadParams(c, 1))));
            return new(1);
        });
        api["query_row"] = new LuaFunction((c, ct) =>
        {
            var rows = _sql.QueryStmt(c.GetArgument<string>(0), ReadParams(c, 1));
            c.Return(rows.Count > 0 ? RowToLua(rows[0]) : LuaValue.Nil);
            return new(1);
        });
        api["transaction"] = new LuaFunction((c, ct) =>
        {
            var fn = c.GetArgument(0);
            _sql.ExecStmt("BEGIN", System.Array.Empty<object?>(), wait: true);
            try { Sync(_lua.CallAsync(fn, System.Array.Empty<LuaValue>())); }
            catch { _sql.ExecStmt("ROLLBACK", System.Array.Empty<object?>(), wait: true); throw; }
            _sql.ExecStmt("COMMIT", System.Array.Empty<object?>(), wait: true);
            return new(0);
        });
        api["flush"] = new LuaFunction((c, ct) => { _sql.Flush(); return new(0); });
        api["snapshot"] = new LuaFunction((c, ct) => { _sql.Snapshot(c.GetArgument<string>(0)); return new(0); });
        return api;
    }

    // A Lua params list (argument index `from`) -> CLR objects for positional binding.
    private static List<object?> ReadParams(LuaFunctionExecutionContext c, int from)
    {
        var ps = new List<object?>();
        if (c.ArgumentCount > from && c.GetArgument(from).Type == LuaValueType.Table)
        {
            var t = c.GetArgument(from).Read<LuaTable>();
            int n = t.ArrayLength;
            for (int i = 1; i <= n; i++) ps.Add(LuaToObj(t[i]));
        }
        return ps;
    }

    private static object? LuaToObj(LuaValue v) => v.Type switch
    {
        LuaValueType.Nil => null,
        LuaValueType.Boolean => v.Read<bool>() ? 1L : 0L,   // SQLite has no bool
        LuaValueType.Number => v.Read<double>(),
        LuaValueType.String => v.Read<string>(),
        _ => v.ToString(),
    };

    private static LuaValue RowsToLua(List<Dictionary<string, object?>> rows)
    {
        var list = new LuaTable();
        for (int i = 0; i < rows.Count; i++) list[i + 1] = RowToLua(rows[i]);
        return list;
    }

    private static LuaValue RowToLua(Dictionary<string, object?> row)
    {
        var t = new LuaTable();
        foreach (var kv in row) t[kv.Key] = ObjToLua(kv.Value);
        return t;
    }

    private static LuaValue ObjToLua(object? o) => o switch
    {
        null => LuaValue.Nil,
        long l => (double)l,
        double d => d,
        string s => s,
        bool b => b,
        byte[] bytes => System.Convert.ToBase64String(bytes),
        _ => o.ToString() ?? "",
    };

    // `scene` is the routing + active-scene capability:
    //   scene.change(name) - request a switch (applied at the next frame boundary)
    //   scene.current()    - the active scene's name (or nil)
    //   scene.find(path)   - look up a node by PATH in the active scene
    //                        (e.g. "Level/Enemy") — unique by construction
    //   scene.add(node)    - parent a node under the active scene container
    //                        (so it is freed when the scene is left)
    // (`change`, not `goto`: `goto` is a reserved word in Lua 5.2.)
    private LuaValue BuildSceneApi()
    {
        var api = new LuaTable();
        api["change"] = new LuaFunction((c, ct) =>
        {
            _pendingScene = c.GetArgument<string>(0);
            return new(0);
        });
        api["current"] = new LuaFunction((c, ct) =>
        {
            c.Return(_activeScene is null ? LuaValue.Nil : _activeScene);
            return new(1);
        });
        api["find"] = new LuaFunction((c, ct) =>
        {
            // Resolve against the live scene tree: a node path is unique by
            // construction, so there is no name-collision ambiguity.
            var node = _sceneContainer?.GetNodeOrNull(c.GetArgument<string>(0));
            c.Return(node is null ? LuaValue.Nil : _godotBinder.WrapInstance(node));
            return new(1);
        });
        api["add"] = new LuaFunction((c, ct) =>
        {
            if (_sceneContainer is null) throw new EvaluateException("scene.add called with no active scene");
            var arg = c.GetArgument(0);
            if (arg.TryRead<GodotInstanceProxy>(out var p) && p.Target is Node child)
                _sceneContainer.AddChild(child);
            return new(0);
        });
        return api;
    }

    // `save` persists runtime/player data to SQLite in Godot's per-project user://
    // dir. save.set(key, v) / save.get(key[, default]) / save.delete(key).
    private LuaValue BuildSaveApi()
    {
        _persistence ??= new Persistence();
        var api = new LuaTable();
        api["set"] = new LuaFunction((ctx, ct) =>
        {
            var v = ctx.GetArgument(1);
            _persistence.Set(ctx.GetArgument<string>(0), SaveTag(v), SaveText(v));
            return new(0);
        });
        api["get"] = new LuaFunction((ctx, ct) =>
        {
            var got = _persistence.Get(ctx.GetArgument<string>(0));
            if (got is null)
                ctx.Return(ctx.ArgumentCount > 1 ? ctx.GetArgument(1) : LuaValue.Nil);
            else
                ctx.Return(FromSave(got.Value.type, got.Value.value));
            return new(1);
        });
        api["delete"] = new LuaFunction((ctx, ct) =>
        {
            _persistence.Delete(ctx.GetArgument<string>(0));
            return new(0);
        });
        return api;
    }

    private static string SaveTag(LuaValue v) => v.Type switch
    {
        LuaValueType.Boolean => "bool",
        LuaValueType.String => "string",
        _ => "number",
    };

    private static string SaveText(LuaValue v) => v.Type switch
    {
        LuaValueType.Boolean => v.Read<bool>() ? "1" : "0",
        LuaValueType.String => v.Read<string>(),
        _ => v.Read<double>().ToString(CultureInfo.InvariantCulture),
    };

    private static LuaValue FromSave(string type, string text) => type switch
    {
        "bool" => text == "1",
        "string" => text,
        _ => double.Parse(text, CultureInfo.InvariantCulture),
    };

    private LuaValue BuildInputApi()
    {
        var api = new LuaTable();
        api["is_down"] = new LuaFunction((ctx, ct) =>
        {
            ctx.Return(Pressed.Contains(ctx.GetArgument<string>(0)));
            return new(1);
        });
        return api;
    }

    // `world` is the persistent global-root node that spawned objects parent to —
    // a real Godot node exposed through the binder (world:add_child(node), …).
    // Nodes added here survive scene switches; for scene-local content use
    // scene.add(node) or declare nodes in a *.scene file. Game objects ARE Godot
    // nodes; there is no separate entity system.
    private LuaValue BuildWorldApi(string path, LoadContext ctx)
    {
        var container = ctx.Container ?? _globalRoot;
        if (container is null)
            throw new EvaluateException($"{path} requests 'world' but no global root is set");
        return _godotBinder.WrapInstance(container);
    }

    // ---- helpers --------------------------------------------------------------

    private LuaTable LoadToml(string file)
    {
        if (_tomlCache.TryGetValue(file, out var cached)) return cached;
        var root = BuildTomlTable(file);
        _tomlCache[file] = root;
        return root;
    }

    // Parse a config file into a fresh section->table LuaTable (no caching). Throws
    // EvaluateException on a malformed file (see Toml.Model).
    private LuaTable BuildTomlTable(string file)
    {
        var root = new LuaTable();
        foreach (var section in Toml.Parse(_readScript(file)))
        {
            if (section.Key.Length == 0) continue;
            var t = new LuaTable();
            foreach (var kv in section.Value) t[kv.Key] = SceneBuilder.TomlToLua(kv.Value);
            root[section.Key] = t;
        }
        return root;
    }

    // Build the `config` table as a LIVE view over the declared TOML files. A
    // metatable resolves config.<section> to a section-view whose keys read the
    // CURRENT TOML cache on each access — so a saved .toml edit (re-read into the
    // cache by ReloadOnChange) is reflected immediately, with no restart and no
    // script re-run, even for a value captured once (e.g. self.cfg = config.x; then
    // cfg.field each frame). config tables were a frozen snapshot before, which is
    // why bare .toml edits did not hot-reload.
    private LuaTable BuildConfigView(List<string> files)
    {
        // section name -> declaring file (first declarer wins). Warm the cache so a
        // startup parse error surfaces here, not lazily inside a hook.
        var sectionFile = new Dictionary<string, string>();
        foreach (var file in files)
        {
            LoadToml(file);
            foreach (var section in Toml.Parse(_readScript(file)).Keys)
                if (section.Length > 0 && !sectionFile.ContainsKey(section))
                    sectionFile[section] = file;
        }

        var config = new LuaTable();
        var mt = new LuaTable();
        mt["__index"] = new LuaFunction((c, ct) =>
        {
            var key = c.GetArgument(1);
            if (key.Type == LuaValueType.String
                && sectionFile.TryGetValue(key.Read<string>(), out var file))
            {
                var view = MakeSectionView(file, key.Read<string>());
                config[key] = view;     // memoize the view object (it reads live internally)
                c.Return(view);
            }
            else c.Return(LuaValue.Nil);
            return new(1);
        });
        config.Metatable = mt;
        return config;
    }

    // A view onto one config section: every key access reads the current TOML cache,
    // so it always reflects the latest saved file.
    private LuaTable MakeSectionView(string file, string section)
    {
        var view = new LuaTable();
        var mt = new LuaTable();
        mt["__index"] = new LuaFunction((c, ct) =>
        {
            var root = LoadToml(file);    // cache hit in steady state (kept warm by ReloadOnChange)
            if (root[section].Type == LuaValueType.Table)
                c.Return(root[section].Read<LuaTable>()[c.GetArgument(1)]);
            else
                c.Return(LuaValue.Nil);
            return new(1);
        });
        view.Metatable = mt;
        return view;
    }

    // Resolve a node's declared `params:` against the values its scene supplied, into the
    // `params` table the body reads. The contract mirrors the sandbox's reject-undeclared
    // stance: a scene value for an UNDECLARED param is an error; a supplied value must match
    // its declared type; a declared param the scene omits falls back to its default, or — if
    // it has none — is a hard error (it was required). Unlike `config`, this is built once at
    // attach (and re-built on reload), not a live view: params are per-instance constants set
    // where the node is declared, so the natural reload unit is the scene rebuild.
    private static LuaTable BuildParamsView(string path, List<ParamSpec> declared,
        IReadOnlyDictionary<string, object>? supplied)
    {
        supplied ??= LoadedNode.EmptyParams;

        foreach (var key in supplied.Keys)
            if (!declared.Exists(p => p.Name == key))
                throw new EvaluateException(
                    $"{path}: the scene supplies param '{key}', but the script declares no such " +
                    "param (add it under 'params:' to accept it)");

        var t = new LuaTable();
        foreach (var p in declared)
        {
            if (supplied.TryGetValue(p.Name, out var v))
            {
                CheckParamType(path, p, v);
                t[p.Name] = SceneBuilder.TomlToLua(v);
            }
            else if (p.HasDefault)
                t[p.Name] = SceneBuilder.TomlToLua(p.Default!);
            else
                throw new EvaluateException(
                    $"{path}: required param '{p.Name}' ({TypeLabel(p.Type)}) was not supplied by the scene");
        }
        return t;
    }

    // A scene-supplied param value must match the declared type. Values come from TOML, so
    // they are bool / double / string / object[] / Dictionary<string,object>; `any` skips.
    private static void CheckParamType(string path, ParamSpec p, object v)
    {
        bool ok = p.Type switch
        {
            "" => true,                                     // `any`: no constraint
            "number" => v is double,
            "string" => v is string,
            "bool" => v is bool,
            "list" => v is object[],
            "table" => v is Dictionary<string, object>,
            _ => true,
        };
        if (!ok)
            throw new EvaluateException(
                $"{path}: param '{p.Name}' expects {p.Type}, but the scene supplies {ActualType(v)}");
    }

    private static string TypeLabel(string type) => type.Length == 0 ? "any" : type;

    private static string ActualType(object v) => v switch
    {
        bool => "bool",
        double => "number",
        string => "string",
        object[] => "list",
        Dictionary<string, object> => "table",
        _ => v.GetType().Name,
    };

    private static T Sync<T>(ValueTask<T> vt) =>
        vt.IsCompleted ? vt.Result : vt.AsTask().GetAwaiter().GetResult();
}

// Evaluate-layer error (capability violation, contract breach, etc.).
public sealed class EvaluateException : Exception
{
    public EvaluateException(string message) : base(message) { }
}
