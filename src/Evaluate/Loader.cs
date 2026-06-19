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
    private Persistence? _persistence;      // lazily opened SQLite save store

    private readonly LuaTable _std;
    private readonly LuaTable _godot;     // ambient: godot.<Type> resolves lazily
    private readonly LuaValue _loadFn;
    private readonly Dictionary<string, LuaValue> _requireCache = new();
    private readonly Dictionary<string, LuaTable> _tomlCache = new();
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

    private static readonly string[] SafeGlobals =
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
        // Each scene the system belongs to is a claim namespace; a global system
        // (no scenes:) claims in the reserved global namespace.
        var namespaces = fm.Scenes.Count > 0 ? fm.Scenes : new List<string> { GlobalNs };

        foreach (var name in fm.Register)
        {
            if (!SystemHooks.Contains(name))
                throw new EvaluateException(
                    $"{path} registers unknown hook '{name}' (valid: {string.Join(", ", SystemHooks)})");
            if (name == "on_start" && fm.Scenes.Count > 0)
                throw new EvaluateException(
                    $"{path} registers 'on_start' but is scene-scoped; use 'on_enter' (on_start is global-only)");
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

    // Lifecycle hooks a SYSTEM script may register. on_start fires once (global
    // systems); on_enter/on_exit fire on scene activation (scene-scoped systems);
    // the rest fire while the system is active.
    public static readonly string[] SystemHooks =
        { "on_start", "on_enter", "on_exit", "on_update", "on_physics_update", "on_input" };

    // Lifecycle hooks a NODE script (`*.node.evt`) may register. on_ready fires
    // when the node enters; on_exit when its container is freed; the rest fire
    // while the node is alive. Each runs with `self` bound to the node.
    public static readonly string[] NodeHooks =
        { "on_ready", "on_update", "on_physics_update", "on_input", "on_exit" };

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
    // the global root, attach their node scripts, fire on_ready. Never torn down.
    public string? LoadGlobalLayer(SceneSpec manifest)
    {
        if (_globalRoot is null) throw new EvaluateException("LoadGlobalLayer called before SetGlobalRoot");
        _globalManifest = manifest;
        InstantiateTree(manifest, _globalRoot, _globalNodes, _globalLayerRoots);
        foreach (var n in _globalNodes) Fire(n, "on_ready");
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
        foreach (var n in _sceneNodes) Fire(n, "on_ready");
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
        void Attach(NodeSpec ns, Node node)
        {
            if (!string.IsNullOrEmpty(ns.Script))
                nodes.Add(LoadNodeScript(ns.Script!, node, container));
        }
        foreach (var ns in spec.Nodes)
        {
            var node = SceneBuilder.BuildNode(ns, _godotBinder, Attach);
            container.AddChild(node);
            roots?.Add(node);
        }
    }

    // Load a `*.node.evt` bound to one live node: fresh sandbox with `self`, then
    // collect its registered node hooks.
    private LoadedNode LoadNodeScript(string path, GodotObject self, Node container)
    {
        var (env, _, fm) = Run(path, new LoadContext(container, self));
        var ln = new LoadedNode { Path = path, Node = self };
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
        _requireCache.Remove(path);

        // Node script: refresh its hook closures on every live instance, keeping
        // each node's identity and `self`.
        if (path.EndsWith(".node.evt"))
        {
            int n = RefreshNodeScript(path);
            return n > 0
                ? $"reloaded node script {path} ({n} live instance(s) refreshed)"
                : $"invalidated node script {path} (no live instances)";
        }

        var sys = _systems.Find(s => s.Path == path);
        if (sys != null)
        {
            LoadSystem(path);                     // re-run body, refresh hook closures
            return $"reloaded system {path} (hooks refreshed, entities preserved)";
        }

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
                return $"reloaded config {path} (live)";
            }
            catch (EvaluateException e)
            {
                return $"kept last-good config {path} ({e.Message})";
            }
        }
        if (path.EndsWith(".evt")) return $"invalidated module {path} (re-required on next use)";
        return $"noted change to asset {path} (would re-import)";
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
                var (env, _, fm) = Run(path, new LoadContext(container, ln.Node));
                ln.Hooks.Clear();
                foreach (var name in fm.Register)
                    if (NodeHooks.Contains(name) && env[name].Type == LuaValueType.Function)
                        ln.Hooks[name] = env[name];
                count++;
            }
            return count;
        }
        return RefreshIn(_globalNodes, _globalRoot) + RefreshIn(_sceneNodes, _sceneContainer);
    }

    // Rebuild the persistent global layer in place from the re-read manifest: fire
    // on_exit for its node scripts, detach + free the manifest's nodes, then
    // re-instantiate and fire on_ready. The active scene and any world-spawned nodes
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
        foreach (var n in _globalNodes) Fire(n, "on_ready");
        return $"reloaded global manifest {ManifestName} (persistent layer rebuilt)";
    }

    public void Call(LuaValue fn, params LuaValue[] args) => Sync(_lua.CallAsync(fn, args));

    // ---- sandbox + require ----------------------------------------------------

    // What a script's sandbox is wired to: the `world` container its spawned
    // nodes parent to, and (for node scripts) the `self` node it drives.
    private readonly record struct LoadContext(GodotObject? Container, GodotObject? Self);

    private (LuaTable env, LuaValue ret, Frontmatter fm) Run(string path, LoadContext ctx)
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

        return env;
    }

    public LuaValue Require(string path)
    {
        if (_requireCache.TryGetValue(path, out var cached)) return cached;
        var (_, ret, fm) = Run(path, new LoadContext(_globalRoot, null));
        var handle = Narrow(path, ret, fm);
        _requireCache[path] = handle;
        return handle;
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

    private LuaValue BuildApi(string path, string name, LoadContext ctx) => name switch
    {
        "input" => BuildInputApi(),
        "world" => BuildWorldApi(path, ctx),
        "scene" => BuildSceneApi(),
        "save" => BuildSaveApi(),
        _ => throw new EvaluateException($"{path} requests unknown api '{name}'"),
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

    private static T Sync<T>(ValueTask<T> vt) =>
        vt.IsCompleted ? vt.Result : vt.AsTask().GetAwaiter().GetResult();
}

// Evaluate-layer error (capability violation, contract breach, etc.).
public sealed class EvaluateException : Exception
{
    public EvaluateException(string message) : base(message) { }
}
