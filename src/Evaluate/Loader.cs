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
    // Property keys the scene set on this node. The scene is the final owner of a
    // property, so a script's frontmatter `properties:` never overwrites these —
    // at attach or on reload.
    public HashSet<string> ScenePropKeys = new();
    // Frontmatter-composed attachments this script pulled onto the same node
    // (`behaviors:` / `machines:` lists). Recorded for reference; composition is
    // structural, so CHANGING these lists applies on scene rebuild, not hot reload.
    public List<string> ComposedBehaviors = new();
    public List<string> ComposedMachines = new();
    // GAS-lite declarations this script carries (re-applied on hot reload for live
    // attribute tuning).
    public Dictionary<string, object> DeclaredAttributes = new();
    public List<string> GrantedAbilities = new();

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
    private readonly Dictionary<string, LuaValue> _godotModules = new();   // memoized declared class/enum tables
    private readonly Dictionary<string, LuaTable> _hostApis = new();       // host-registered C# extension apis
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
    private readonly List<MachineInstance> _globalMachines = new();     // machines on persistent nodes
    private readonly List<MachineInstance> _sceneMachines = new();      // machines on active-scene nodes
    private readonly List<NodeAbilityState> _globalAbilityStates = new();   // ability/attribute state, persistent nodes
    private readonly List<NodeAbilityState> _sceneAbilityStates = new();    // ability/attribute state, scene nodes
    private AbilityRuntime _abilities = null!;                           // set in ctor (needs binder)
    private string? _hookOwner;                                          // script whose hook is executing (listener attribution)
    private readonly HashSet<string> _loadedScripts = new();             // every .evt Run so far
    private readonly HashSet<string> _configFiles = new();               // every declared toml
    private readonly HashSet<string> _assetFiles = new();                // every declared asset file (res://-relative, post-glob)
    private readonly HashSet<string> _sceneFiles = new();               // every declared *.scene file
    private readonly Dictionary<string, Resource> _assetCache = new();   // res-relative path -> loaded resource
    private readonly Dictionary<string, HashSet<string>> _scriptAssets = new();   // script -> its declared asset files

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

        _abilities = new AbilityRuntime(_godotBinder, _readScript, _log,
            () => _hookOwner, (fn, args) => Call(fn, args));

        _loadFn = _lua.Environment["load"];
    }

    // ---- host extension apis ---------------------------------------------------

    // Names a host api may not take: the framework capabilities, the sandbox's
    // context-injected globals, and the attachment surfaces reserved on nodes.
    private static readonly HashSet<string> ReservedApiNames = new()
        { "std", "print", "self", "config", "params", "assets", "require", "godot",
          "fsm", "attributes", "abilities" };

    // Godot classes that are NOT declarable capabilities: imperative file/resource IO
    // belongs to the C# layer — scripts get assets through frontmatter `assets:` and
    // persistence through `save`/`sql`.
    internal static readonly string[] BlockedApis =
        { "ResourceLoader", "ResourceSaver", "FileAccess", "DirAccess" };

    // Register a C#-side api module under `name`. Scripts reach it by declaring the
    // name in `apis:` — it is then implicitly callable as a normal Lua table of
    // functions. `impl` is either an object (public instance + static methods bind,
    // with `impl` as the receiver) or a `System.Type` (public statics only). Must be
    // called before any script loads, so every sandbox sees the same api set.
    public void RegisterApi(string name, object impl)
    {
        if (string.IsNullOrEmpty(name))
            throw new EvaluateException("RegisterApi: api name is empty");
        if (_loadedScripts.Count > 0)
            throw new EvaluateException(
                $"RegisterApi('{name}'): apis must be registered before any script loads " +
                "(call it before the runtime enters the tree)");
        if (ApiNames.Contains(name) || ReservedApiNames.Contains(name) || SafeGlobals.Contains(name))
            throw new EvaluateException($"RegisterApi('{name}'): the name is reserved");
        if (_hostApis.ContainsKey(name))
            throw new EvaluateException($"RegisterApi('{name}'): already registered");
        if (_godotBinder.Resolve(name) is not null)
            throw new EvaluateException(
                $"RegisterApi('{name}'): the name collides with a Godot class/enum of the same name");
        _hostApis[name] = _godotBinder.BindHost(impl);
    }

    // Host apis registered so far (docs emitter walks these like the built-in tables).
    internal IReadOnlyDictionary<string, LuaTable> HostApis => _hostApis;

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
            // Node-attached types load through scenes, never as systems.
            if (!file.EndsWith(".evt") || file.EndsWith(".node.evt")
                || file.EndsWith(".behavior.evt") || file.EndsWith(".statemachine.evt")) continue;
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
        InstantiateTree(manifest, _globalRoot, _globalNodes, _globalMachines, _globalLayerRoots);
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
        _sceneMachines.Clear();
        _sceneAbilityStates.Clear();
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
        InstantiateTree(spec, container, _sceneNodes, _sceneMachines);
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

    // Hook dispatch sets `_hookOwner` so anything the hook subscribes (fsm state
    // listeners) is attributed to the script — a hot reload of that script then
    // drops its stale listeners before on_load re-subscribes fresh ones.
    private void Fire(LoadedNode n, string hook)
    {
        if (!n.Hooks.TryGetValue(hook, out var fn)) return;
        var prev = _hookOwner; _hookOwner = n.Path;
        try { Call(fn); } finally { _hookOwner = prev; }
    }
    private void FireSystem(LoadedSystem s, string hook)
    {
        if (!s.Hooks.TryGetValue(hook, out var fn)) return;
        var prev = _hookOwner; _hookOwner = s.Path;
        try { Call(fn); } finally { _hookOwner = prev; }
    }

    private static string SceneFileName(string name) => name + ".scene";

    // Instantiate a scene/manifest node tree under `container`: SceneBuilder builds
    // the visual tree (types, properties, hierarchy) and, via the visit callback,
    // we attach each node's behaviors and state machines (bound to that node).
    private void InstantiateTree(SceneSpec spec, Node container, List<LoadedNode> nodes,
        List<MachineInstance> machines, List<Node>? roots = null)
    {
        var conns = new List<(Node from, ConnectionSpec spec)>();
        IReadOnlyList<NodeSpec> Resolve(string name) => SceneFile.Parse(_readScript(SceneFileName(name))).Nodes;

        void Attach(NodeSpec ns, Node node)
        {
            // One node, many attachments: the legacy single `script =` first, then the
            // scene's `behaviors = [...]` and `machines = [...]` in list order; each
            // attachment may compose more via ITS frontmatter (depth-first). Hooks fire
            // in this attachment order. A repeated path attaches once.
            var attached = new HashSet<string>();
            if (!string.IsNullOrEmpty(ns.Script))
                AttachBehavior(ns.Script!, node, container, ns.Params, ns.Props.Keys, nodes, machines, attached);
            foreach (var b in ns.Behaviors)
                AttachBehavior(b.Script, node, container, b.Params, ns.Props.Keys, nodes, machines, attached);
            foreach (var m in ns.Machines)
                AttachMachine(m.Script, node, container, m.Params, machines, attached);
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
    // per-instance `params`, apply the script's declared `properties:` (the scene's own
    // keys win), then collect its registered node hooks.
    private LoadedNode LoadNodeScript(string path, GodotObject self, Node container,
        IReadOnlyDictionary<string, object> sceneParams, IEnumerable<string>? scenePropKeys = null)
    {
        var (env, _, fm) = Run(path, new LoadContext(container, self, sceneParams));
        var ln = new LoadedNode
        {
            Path = path, Node = self, Configs = fm.Configs, Params = sceneParams,
            ComposedBehaviors = fm.Behaviors, ComposedMachines = fm.Machines,
            DeclaredAttributes = fm.Attributes, GrantedAbilities = fm.Abilities,
        };
        if (scenePropKeys is not null) ln.ScenePropKeys = new HashSet<string>(scenePropKeys);
        ApplyFrontmatterProperties(path, fm, self, ln.ScenePropKeys);
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

    // Apply a node-attached script's `properties:` block to its node. Runs at attach
    // (before on_attach) and again on script hot reload (before on_load), skipping any
    // key the scene set — the scene owns final placement/overrides. An unknown
    // property is a load error: the signature is the contract, typos fail loudly.
    private void ApplyFrontmatterProperties(string path, Frontmatter fm, GodotObject self,
        HashSet<string> scenePropKeys)
    {
        foreach (var kv in fm.Properties)
        {
            if (scenePropKeys.Contains(kv.Key)) continue;
            if (!_godotBinder.HasProperty(self, kv.Key))
                throw new EvaluateException(
                    $"{path}: properties: '{kv.Key}' is not a property of {self.GetClass()}");
            _godotBinder.SetProperty(self, kv.Key, SceneBuilder.TomlToLua(kv.Value));
        }
    }

    // ---- behaviors + state machines (the attachment pipeline) -----------------

    // Attach one behavior (`*.behavior.evt`, or the legacy `*.node.evt` alias) to a
    // node, then anything ITS frontmatter composes (`behaviors:`/`machines:` lists,
    // depth-first). `attached` dedupes per node and breaks composition cycles.
    private void AttachBehavior(string path, Node node, Node container,
        IReadOnlyDictionary<string, object> sceneParams, IEnumerable<string> scenePropKeys,
        List<LoadedNode> nodes, List<MachineInstance> machines, HashSet<string> attached)
    {
        if (!path.EndsWith(".behavior.evt") && !path.EndsWith(".node.evt"))
            throw new EvaluateException(
                $"'{path}' cannot attach as a behavior: expected *.behavior.evt (or the " +
                "legacy *.node.evt)");
        if (!attached.Add(path)) return;
        var ln = LoadNodeScript(path, node, container, sceneParams, scenePropKeys);
        nodes.Add(ln);
        ApplyGasDeclarations(path, ln, nodes == _globalNodes ? _globalAbilityStates : _sceneAbilityStates);
        foreach (var composed in ln.ComposedBehaviors)
            AttachBehavior(composed, node, container, LoadedNode.EmptyParams, scenePropKeys,
                nodes, machines, attached);
        foreach (var composed in ln.ComposedMachines)
            AttachMachine(composed, node, container, LoadedNode.EmptyParams, machines, attached);
    }

    // Wire a behavior's `attributes:` / `abilities:` declarations into its node's
    // ability state (created on first declarer, surfaces installed once).
    private void ApplyGasDeclarations(string path, LoadedNode ln, List<NodeAbilityState> states)
    {
        if (ln.DeclaredAttributes.Count == 0 && ln.GrantedAbilities.Count == 0) return;
        var st = states.FirstOrDefault(s => s.Node == ln.Node);
        if (st is null)
        {
            st = new NodeAbilityState { Node = ln.Node };
            states.Add(st);
            var aux = _godotBinder.Aux(ln.Node);
            aux["attributes"] = _abilities.BuildAttributesSurface(st);
            aux["abilities"] = _abilities.BuildAbilitiesSurface(st, StateOf);
        }
        _abilities.DeclareAttributes(path, ln.Node, st, ln.DeclaredAttributes);
        foreach (var ability in ln.GrantedAbilities) _abilities.Grant(path, st, ability);
    }

    private NodeAbilityState? StateOf(GodotObject node) =>
        _globalAbilityStates.FirstOrDefault(s => s.Node == node)
        ?? _sceneAbilityStates.FirstOrDefault(s => s.Node == node);

    // Attach one `*.statemachine.evt` to a node: run its body, parse the returned
    // transition list against the frontmatter's states, and install the
    // `self.fsm.<name>` surface.
    private void AttachMachine(string path, Node node, Node container,
        IReadOnlyDictionary<string, object> sceneParams, List<MachineInstance> machines,
        HashSet<string> attached)
    {
        if (!path.EndsWith(".statemachine.evt"))
            throw new EvaluateException(
                $"'{path}' cannot attach as a machine: expected *.statemachine.evt");
        if (!attached.Add(path)) return;

        var (_, ret, fm) = Run(path, new LoadContext(container, node, sceneParams));
        var def = ParseMachineDef(path, fm, ret);
        foreach (var other in _globalMachines.Concat(_sceneMachines))
            if (other.Node == node && other.Def.Name == def.Name)
                throw new EvaluateException(
                    $"{path}: node '{node.Name}' already has a machine named '{def.Name}' " +
                    $"(from {other.Def.Path})");

        var inst = new MachineInstance
        {
            Def = def, Node = node, Container = container,
            SelfProxy = _godotBinder.WrapInstance(node),
            Params = sceneParams, Current = def.Initial,
        };
        machines.Add(inst);
        InstallMachineSurface(inst);
    }

    // Validate a machine's signature + returned transitions into a MachineDef.
    private MachineDef ParseMachineDef(string path, Frontmatter fm, LuaValue ret)
    {
        if (fm.Register.Count > 0)
            throw new EvaluateException(
                $"{path}: a statemachine registers no hooks — the runtime ticks it; " +
                "put per-frame logic in a behavior");
        if (fm.Returns.Count > 0)
            throw new EvaluateException(
                $"{path}: a statemachine declares no 'returns:' — its return value IS " +
                "the transition list");
        if (fm.States.Count == 0)
            throw new EvaluateException($"{path}: a statemachine declares its 'states:' in frontmatter");
        if (fm.States.Distinct().Count() != fm.States.Count)
            throw new EvaluateException($"{path}: duplicate state names in 'states:'");

        var def = new MachineDef
        {
            Path = path,
            Name = fm.MachineName ?? MachineNameFromPath(path),
            States = fm.States,
            Initial = fm.Initial ?? fm.States[0],
        };
        if (!def.States.Contains(def.Initial))
            throw new EvaluateException(
                $"{path}: initial state '{def.Initial}' is not in states: [{string.Join(", ", def.States)}]");

        if (ret.Type != LuaValueType.Table)
            throw new EvaluateException(
                $"{path}: a statemachine's body returns its transition list " +
                "(`return {{ {{ from = ..., to = ..., when|on|after = ... }}, ... }}`)");
        var list = ret.Read<LuaTable>();
        for (int i = 1; i <= list.ArrayLength; i++)
        {
            if (list[i].Type != LuaValueType.Table)
                throw new EvaluateException($"{path}: transition #{i} is not a table");
            var t = list[i].Read<LuaTable>();
            var tr = new TransitionDef
            {
                From = t["from"].Type == LuaValueType.String ? t["from"].Read<string>() : "",
                To = t["to"].Type == LuaValueType.String ? t["to"].Read<string>() : "",
                When = t["when"],
                On = t["on"].Type == LuaValueType.String ? t["on"].Read<string>() : null,
                After = t["after"].Type == LuaValueType.Number ? t["after"].Read<double>() : -1,
                Run = t["run"],
            };
            if (tr.From.Length == 0 || tr.To.Length == 0)
                throw new EvaluateException($"{path}: transition #{i} needs 'from' and 'to' state names");
            if (tr.From != "*" && !def.States.Contains(tr.From))
                throw new EvaluateException($"{path}: transition #{i} 'from' names unknown state '{tr.From}'");
            if (!def.States.Contains(tr.To))
                throw new EvaluateException($"{path}: transition #{i} 'to' names unknown state '{tr.To}'");
            int triggers = (tr.When.Type == LuaValueType.Function ? 1 : 0)
                         + (tr.On is not null ? 1 : 0) + (tr.After >= 0 ? 1 : 0);
            if (triggers != 1)
                throw new EvaluateException(
                    $"{path}: transition #{i} needs exactly ONE trigger — " +
                    "when = fn(self), on = \"event\", or after = seconds");
            if (tr.Run.Type is not (LuaValueType.Function or LuaValueType.Nil))
                throw new EvaluateException($"{path}: transition #{i} 'run' must be a function");
            def.Transitions.Add(tr);
        }
        if (def.Transitions.Count == 0)
            throw new EvaluateException($"{path}: a statemachine returns at least one transition");
        return def;
    }

    private static string MachineNameFromPath(string path)
    {
        var file = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        return file.EndsWith(".statemachine.evt") ? file[..^".statemachine.evt".Length] : file;
    }

    // Install `self.fsm.<name>` on the node: `.state` (read), `:is(s)`, `:fire(evt)`,
    // `:on_exit(state, fn)`, plus the approved sugar — assigning a state name APPENDS
    // an enter-listener fn(from). Multiple subscribers stack; each is owned by the
    // subscribing script for reload cleanup.
    private void InstallMachineSurface(MachineInstance inst)
    {
        var aux = _godotBinder.Aux(inst.Node);
        var fsmRoot = aux["fsm"].Type == LuaValueType.Table ? aux["fsm"].Read<LuaTable>() : new LuaTable();

        var surface = new LuaTable();
        var mt = new LuaTable();
        mt["__index"] = new LuaFunction((c, ct) =>
        {
            var key = c.GetArgument(1);
            if (key.Type != LuaValueType.String) { c.Return(LuaValue.Nil); return new(1); }
            switch (key.Read<string>())
            {
                case "state":
                    c.Return(inst.Current);
                    break;
                case "is":
                    c.Return(new LuaFunction((c2, ct2) =>
                    {
                        var s = c2.GetArgument(c2.ArgumentCount - 1).Read<string>();
                        c2.Return(inst.Current == s);
                        return new(1);
                    }));
                    break;
                case "fire":
                    c.Return(new LuaFunction((c2, ct2) =>
                    {
                        var evt = c2.GetArgument(c2.ArgumentCount - 1).Read<string>();
                        c2.Return(FireMachineEvent(inst, evt));
                        return new(1);
                    }));
                    break;
                case "on_exit":
                    c.Return(new LuaFunction((c2, ct2) =>
                    {
                        var state = c2.GetArgument(c2.ArgumentCount - 2).Read<string>();
                        var fn = c2.GetArgument(c2.ArgumentCount - 1);
                        if (!inst.Def.States.Contains(state))
                            throw new EvaluateException(
                                $"fsm '{inst.Def.Name}': on_exit for unknown state '{state}'");
                        if (fn.Type != LuaValueType.Function)
                            throw new EvaluateException($"fsm '{inst.Def.Name}': on_exit expects a function");
                        AddListener(inst.ExitListeners, state, _hookOwner ?? "<runtime>", fn);
                        return new(0);
                    }));
                    break;
                default:
                    c.Return(LuaValue.Nil);
                    break;
            }
            return new(1);
        });
        mt["__newindex"] = new LuaFunction((c, ct) =>
        {
            var key = c.GetArgument(1).Type == LuaValueType.String ? c.GetArgument(1).Read<string>() : "";
            var val = c.GetArgument(2);
            if (!inst.Def.States.Contains(key))
                throw new EvaluateException(
                    $"fsm '{inst.Def.Name}': cannot subscribe to unknown state '{key}' " +
                    $"(states: {string.Join(", ", inst.Def.States)})");
            if (val.Type != LuaValueType.Function)
                throw new EvaluateException(
                    $"fsm '{inst.Def.Name}'.{key} = <enter listener>: expected a function");
            AddListener(inst.EnterListeners, key, _hookOwner ?? "<runtime>", val);
            return new(0);
        });
        surface.Metatable = mt;

        fsmRoot[inst.Def.Name] = surface;
        aux["fsm"] = fsmRoot;
    }

    private static void AddListener(Dictionary<string, List<(string, LuaValue)>> map,
        string state, string owner, LuaValue fn)
    {
        if (!map.TryGetValue(state, out var list)) map[state] = list = new();
        list.Add((owner, fn));
    }

    // Fire an event NOW: the first `on`-triggered transition applicable from the
    // current state is taken immediately. A fire from inside a listener/action
    // defers one tick (reentrancy guard). Returns whether a transition was taken.
    private bool FireMachineEvent(MachineInstance inst, string evt)
    {
        if (inst.Firing) { inst.DeferredEvents.Add(evt); return false; }
        foreach (var tr in inst.Def.Transitions)
            if (tr.On == evt && (tr.From == inst.Current || (tr.From == "*" && tr.To != inst.Current)))
            {
                TakeTransition(inst, tr);
                return true;
            }
        return false;
    }

    // Take one transition: exit listeners (old state, arg = destination), the
    // transition's own `run` action, then enter listeners (new state, arg = origin).
    private void TakeTransition(MachineInstance inst, TransitionDef tr)
    {
        var from = inst.Current;
        inst.Firing = true;
        try
        {
            inst.Current = tr.To;
            inst.TimeInState = 0;
            if (inst.ExitListeners.TryGetValue(from, out var exits))
                foreach (var (_, fn) in exits.ToArray()) Call(fn, tr.To);
            if (tr.Run.Type == LuaValueType.Function) Call(tr.Run, inst.SelfProxy, from, tr.To);
            if (inst.EnterListeners.TryGetValue(tr.To, out var enters))
                foreach (var (_, fn) in enters.ToArray()) Call(fn, from);
        }
        finally { inst.Firing = false; }
    }

    // Advance the framework's ticking runtimes one physics tick: state machines,
    // then abilities (channel drains, effect timelines, attribute regen).
    public void Tick(double dt)
    {
        TickMachines(dt);
        _abilities.Tick(_globalAbilityStates.Concat(_sceneAbilityStates), dt);
    }

    // Advance every active machine one physics tick: deferred events first, then the
    // declared transitions in order — first applicable `when` guard or expired
    // `after` timer wins. At most ONE transition per machine per tick (no cascades).
    public void TickMachines(double dt)
    {
        foreach (var inst in _globalMachines.Concat(_sceneMachines).ToList())
        {
            inst.TimeInState += dt;

            if (inst.DeferredEvents.Count > 0)
            {
                var events = inst.DeferredEvents.ToList();
                inst.DeferredEvents.Clear();
                bool moved = false;
                foreach (var e in events)
                    if (!moved) moved = FireMachineEvent(inst, e);
                if (moved) continue;
            }

            foreach (var tr in inst.Def.Transitions)
            {
                bool applicable = tr.From == inst.Current
                                  || (tr.From == "*" && tr.To != inst.Current);
                if (!applicable) continue;
                if (tr.When.Type == LuaValueType.Function)
                {
                    var r = CallRet(tr.When, inst.SelfProxy);
                    if (r.Type != LuaValueType.Nil && !(r.Type == LuaValueType.Boolean && !r.Read<bool>()))
                    {
                        TakeTransition(inst, tr);
                        break;
                    }
                }
                else if (tr.After >= 0 && inst.TimeInState >= tr.After)
                {
                    TakeTransition(inst, tr);
                    break;
                }
            }
        }
    }

    // Re-run a changed machine's body on each live instance: fresh transitions, SAME
    // listeners (they belong to behaviors), state kept when still declared, else
    // reset to initial.
    private int RefreshMachines(string path)
    {
        int count = 0;
        foreach (var inst in _globalMachines.Concat(_sceneMachines).ToList())
        {
            if (inst.Def.Path != path) continue;
            var (_, ret, fm) = Run(path, new LoadContext(inst.Container, inst.Node, inst.Params));
            var def = ParseMachineDef(path, fm, ret);
            var renamed = def.Name != inst.Def.Name;
            inst.Def = def;
            if (renamed) InstallMachineSurface(inst);   // republish under the new name
            if (!def.States.Contains(inst.Current))
            {
                inst.Current = def.Initial;
                inst.TimeInState = 0;
            }
            count++;
        }
        return count;
    }

    // Drop every fsm listener a script subscribed on this node (called before the
    // script's body re-runs, so on_load re-subscribes fresh closures, not stale ones).
    private void RemoveMachineListeners(GodotObject node, string ownerPath)
    {
        foreach (var inst in _globalMachines.Concat(_sceneMachines))
        {
            if (inst.Node != node) continue;
            foreach (var list in inst.EnterListeners.Values) list.RemoveAll(l => l.Owner == ownerPath);
            foreach (var list in inst.ExitListeners.Values) list.RemoveAll(l => l.Owner == ownerPath);
        }
    }

    // ---- hot reload + watch targets ------------------------------------------

    // Script-side files the host should watch (all under res://scripts): every
    // loaded script, declared config, and scene file.
    public IEnumerable<string> WatchTargets()
    {
        foreach (var s in _loadedScripts) yield return s;   // .evt + .node.evt
        foreach (var c in _configFiles) yield return c;
        foreach (var sc in _sceneFiles) yield return sc;    // *.scene + manifest
    }

    // Declared asset files (res://-relative, post-glob). They live OUTSIDE
    // res://scripts, so the host stands up separate watchers for their directories.
    public IEnumerable<string> AssetWatchTargets() => _assetFiles;

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

        // Ability / effect definitions re-parse live into the def cache.
        if (path.EndsWith(".ability") || path.EndsWith(".effect"))
            return _abilities.ReloadDef(path);

        // Anything else: a declared asset changed on disk. The scripts watcher hands
        // scripts/-relative paths and the asset watchers res://-relative ones — try both.
        var asset = _assetFiles.Contains(path) ? path
                  : _assetFiles.Contains($"scripts/{path}") ? $"scripts/{path}" : null;
        if (asset is null) return $"noted change to {path} (not a declared asset)";
        return ReloadAsset(asset);
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
            if (p.EndsWith(".node.evt") || p.EndsWith(".behavior.evt"))
            {
                int n = RefreshNodeScript(p);
                messages.Add(n > 0
                    ? $"reloaded behavior {p} ({n} live instance(s) refreshed)"
                    : $"invalidated behavior {p} (no live instances)");
            }
            else if (p.EndsWith(".statemachine.evt"))
            {
                int n = RefreshMachines(p);
                messages.Add(n > 0
                    ? $"reloaded machine {p} ({n} live instance(s): transitions refreshed, state + listeners kept)"
                    : $"invalidated machine {p} (no live instances)");
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
                RemoveMachineListeners(ln.Node, path);   // stale fsm subscriptions die with the old body
                if (StateOf(ln.Node) is { } gas) _abilities.RemoveListeners(gas, path);
                var (env, _, fm) = Run(path, new LoadContext(container, ln.Node, ln.Params));
                ln.Hooks.Clear();
                ln.Configs = fm.Configs;
                ln.DeclaredAttributes = fm.Attributes;
                ln.GrantedAbilities = fm.Abilities;
                ApplyFrontmatterProperties(path, fm, ln.Node, ln.ScenePropKeys);   // edits to properties: apply live
                ApplyGasDeclarations(path, ln, list == _globalNodes ? _globalAbilityStates : _sceneAbilityStates);
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
        _globalMachines.Clear();
        _globalAbilityStates.Clear();
        foreach (var root in _globalLayerRoots)
        {
            _globalRoot.RemoveChild(root);   // free the name slot now, before re-adding
            root.QueueFree();
        }
        _globalLayerRoots.Clear();

        var manifest = SceneFile.Parse(_readScript(ManifestName));
        _globalManifest = manifest;
        InstantiateTree(manifest, _globalRoot, _globalNodes, _globalMachines, _globalLayerRoots);
        foreach (var n in _globalNodes) Fire(n, "on_attach");
        foreach (var n in _globalNodes) Fire(n, "on_load");
        return $"reloaded global manifest {ManifestName} (persistent layer rebuilt)";
    }

    public void Call(LuaValue fn, params LuaValue[] args) => Sync(_lua.CallAsync(fn, args));

    // Call returning the first result (machine guards need the boolean back).
    internal LuaValue CallRet(LuaValue fn, params LuaValue[] args)
    {
        var r = Sync(_lua.CallAsync(fn, args));
        return r.Length > 0 ? r[0] : LuaValue.Nil;
    }

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

            // Record watch targets: the script itself and its configs. (Declared
            // assets record inside BuildAssetsView, post-glob-expansion.)
            _loadedScripts.Add(path);
            foreach (var c in fm.Configs) _configFiles.Add(c);

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

        // Node scripts drive one node, exposed as `self`.
        if (ctx.Self is not null) env["self"] = _godotBinder.WrapInstance(ctx.Self);

        env["config"] = BuildConfigView(fm.Configs);

        // `assets`: the declared engine resources, loaded eagerly (a missing file is
        // a load error, not a runtime nil) and read through a live view so a
        // hot-reloaded asset is what the next access sees.
        if (fm.Assets.Count > 0) env["assets"] = BuildAssetsView(path, fm.Assets);

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

        // `properties:` is likewise node-only — a system has no `self` to apply them to.
        if (ctx.Self is null && fm.Properties.Count > 0)
            throw new EvaluateException(
                $"{path} declares 'properties:' but is not a node script; native properties " +
                "apply to the node a script is attached to");

        // Composition lists are node-only too: a system has no node to attach onto.
        if (ctx.Self is null && (fm.Behaviors.Count > 0 || fm.Machines.Count > 0))
            throw new EvaluateException(
                $"{path} declares 'behaviors:'/'machines:' but is not node-attached; " +
                "composition adds attachments to the node carrying the script");

        // As are GAS declarations — attributes/abilities live on a node.
        if (ctx.Self is null && (fm.Attributes.Count > 0 || fm.Abilities.Count > 0))
            throw new EvaluateException(
                $"{path} declares 'attributes:'/'abilities:' but is not node-attached");

        // Every capability is declared and injected as its own global table: framework
        // services, host-registered extensions, and Godot classes/enums alike. There is
        // no ambient `godot.*` — the Godot library IS the api, module by module.
        foreach (var api in fm.Apis)
        {
            if (api.StartsWith("godot:"))
                throw new EvaluateException(
                    $"{path} declares '{api}': the 'godot:' prefix is gone — declare the class " +
                    $"itself (apis: [{api[6..]}]) and use it as a bare global");
            env[api] = ResolveApi(path, api, ctx);
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
        // last so a binding cannot silently shadow std/config/params/self, a declared
        // api, a language global, or the `require` function itself.
        foreach (var req in fm.Requires)
        {
            if (env[req.Name].Type != LuaValueType.Nil)
                throw new EvaluateException(
                    $"{path}: require binding '{req.Name}' collides with a reserved or declared " +
                    "sandbox name (std/config/params/self, an api, a language global, " +
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

    // The framework-service APIs a script may declare in `apis:`. Single source of truth:
    // the BuildApi switch dispatches on these names and the docs emitter walks them,
    // so a newly-added api (a new switch arm + array entry) self-documents. `apis:` also
    // accepts host-registered extension names and Godot class/enum names (ResolveApi).
    internal static readonly string[] ApiNames = { "input", "world", "scene", "save", "sql" };

    // Resolve one declared `apis:` entry to the table the sandbox injects under that
    // name. Precedence: framework service -> host extension -> Godot class/enum
    // (memoized). Anything else — including the blocked file/resource-IO classes — is
    // a load error, so a typo or an undeclared capability fails at load, not at play.
    private LuaValue ResolveApi(string path, string name, LoadContext ctx)
    {
        if (BlockedApis.Contains(name))
            throw new EvaluateException(
                $"{path} requests '{name}', which is not a script capability: assets load " +
                "through frontmatter 'assets:', persistence through 'save'/'sql'");
        if (ApiNames.Contains(name)) return BuildApi(path, name, ctx);
        if (_hostApis.TryGetValue(name, out var host)) return host;
        if (_godotModules.TryGetValue(name, out var cached)) return cached;
        var resolved = _godotBinder.Resolve(name);
        if (resolved is null || resolved.Value.Type == LuaValueType.Nil)
            throw new EvaluateException(
                $"{path} requests unknown api '{name}' (not a framework api " +
                $"[{string.Join(", ", ApiNames)}], a registered host api, or a Godot class/enum)");
        _godotModules[name] = resolved.Value;
        return resolved.Value;
    }

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
    //   scene.list()       - every switchable scene name under the scripts root
    //                        (sorted, manifest excluded) — feeds scene.change.
    //                        Scenes are framework artifacts, so enumerating them is a
    //                        scene capability (file IO classes stay undeclarable).
    // (`change`, not `goto`: `goto` is a reserved word in Lua 5.2.)
    private LuaValue BuildSceneApi()
    {
        var api = new LuaTable();
        api["list"] = new LuaFunction((c, ct) =>
        {
            var t = new LuaTable();
            var root = ProjectSettings.GlobalizePath("res://scripts");
            int i = 1;
            if (System.IO.Directory.Exists(root))
                foreach (var f in System.IO.Directory
                             .EnumerateFiles(root, "*.scene", System.IO.SearchOption.AllDirectories)
                             .OrderBy(x => x, StringComparer.Ordinal))
                {
                    var rel = System.IO.Path.GetRelativePath(root, f).Replace('\\', '/');
                    if (rel == ManifestName) continue;
                    t[i++] = rel[..^".scene".Length];
                }
            c.Return(t);
            return new(1);
        });
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

    // ---- assets ---------------------------------------------------------------

    // Resolve a script's `assets:` block: expand globs, eagerly load every file into
    // the shared cache (missing = load error naming the script and binding), record
    // watch targets, and hand back a LIVE view — each access re-reads the cache, so
    // a hot-reloaded asset is what the body sees next. A glob binds a sub-table
    // keyed by file stem; the file set is fixed at (re)load time.
    private LuaTable BuildAssetsView(string path, List<AssetSpec> specs)
    {
        var single = new Dictionary<string, string>();
        var globs = new Dictionary<string, List<(string stem, string file)>>();
        if (!_scriptAssets.TryGetValue(path, out var declared))
            declared = _scriptAssets[path] = new HashSet<string>();
        declared.Clear();

        foreach (var spec in specs)
        {
            if (spec.Path.Contains('*'))
            {
                var files = ExpandGlob(spec.Path).ToList();
                if (files.Count == 0)
                    throw new EvaluateException(
                        $"{path}: asset '{spec.Name}' glob '{spec.Path}' matches no files");
                var list = new List<(string, string)>();
                foreach (var f in files)
                {
                    LoadAssetFile(path, spec.Name, f);
                    declared.Add(f); _assetFiles.Add(f);
                    list.Add((System.IO.Path.GetFileNameWithoutExtension(f), f));
                }
                globs[spec.Name] = list;
            }
            else
            {
                LoadAssetFile(path, spec.Name, spec.Path);
                declared.Add(spec.Path); _assetFiles.Add(spec.Path);
                single[spec.Name] = spec.Path;
            }
        }

        var view = new LuaTable();
        var mt = new LuaTable();
        mt["__index"] = new LuaFunction((c, ct) =>
        {
            var key = c.GetArgument(1);
            if (key.Type == LuaValueType.String)
            {
                var k = key.Read<string>();
                if (single.TryGetValue(k, out var p))
                {
                    c.Return(_godotBinder.WrapInstance(_assetCache[p]));
                    return new(1);
                }
                if (globs.TryGetValue(k, out var files))
                {
                    var t = new LuaTable();
                    foreach (var (stem, f) in files) t[stem] = _godotBinder.WrapInstance(_assetCache[f]);
                    c.Return(t);
                    return new(1);
                }
            }
            c.Return(LuaValue.Nil);
            return new(1);
        });
        view.Metatable = mt;
        return view;
    }

    private void LoadAssetFile(string scriptPath, string name, string resRelative)
    {
        if (_assetCache.ContainsKey(resRelative)) return;
        var res = ResourceLoader.Load($"res://{resRelative}");
        if (res is null)
            throw new EvaluateException(
                $"{scriptPath}: asset '{name}' -> '{resRelative}' failed to load " +
                "(missing file, or a resource type that needs a Godot import first)");
        _assetCache[resRelative] = res;
    }

    // Expand a `dir/pattern*.ext` glob (filename position only) against the project
    // filesystem. `.import` sidecars are skipped; results sort for determinism.
    private static IEnumerable<string> ExpandGlob(string resRelative)
    {
        var slash = resRelative.LastIndexOf('/');
        var dir = slash >= 0 ? resRelative[..slash] : "";
        var pattern = slash >= 0 ? resRelative[(slash + 1)..] : resRelative;
        var abs = ProjectSettings.GlobalizePath($"res://{dir}");
        if (!System.IO.Directory.Exists(abs)) yield break;
        foreach (var f in System.IO.Directory.GetFiles(abs, pattern).OrderBy(x => x))
        {
            if (f.EndsWith(".import")) continue;
            var file = System.IO.Path.GetFileName(f);
            yield return dir.Length > 0 ? $"{dir}/{file}" : file;
        }
    }

    // Apply an on-disk change to a declared asset. A `.gdshader` updates IN PLACE —
    // the same Shader instance gets the new code, so every live ShaderMaterial
    // recompiles with no rebuild (even one captured in on_attach). Anything else
    // re-loads with Replace semantics, then every declaring script reloads through
    // the module graph so on_load rebuilds against the fresh resource.
    private string ReloadAsset(string path)
    {
        if (path.EndsWith(".gdshader") && _assetCache.TryGetValue(path, out var cached) && cached is Shader sh)
        {
            var text = Godot.FileAccess.GetFileAsString($"res://{path}");
            if (!string.IsNullOrEmpty(text)) sh.Code = text;
            return $"reloaded shader {path} in place (live materials recompiled)";
        }
        if (_assetCache.ContainsKey(path))
        {
            var fresh = ResourceLoader.Load($"res://{path}", null, ResourceLoader.CacheMode.Replace);
            if (fresh is not null) _assetCache[path] = fresh;
        }
        var consumers = _scriptAssets.Where(kv => kv.Value.Contains(path)).Select(kv => kv.Key).ToList();
        foreach (var c in consumers) ReloadModuleGraph(c);
        return $"reloaded asset {path} ({consumers.Count} declaring script(s) refreshed)";
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
            object? value;
            if (supplied.TryGetValue(p.Name, out var v))
            {
                CheckParamType(path, p, v);
                value = v;
            }
            else if (p.HasDefault)
                value = p.Default!;
            else
                throw new EvaluateException(
                    $"{path}: required param '{p.Name}' ({TypeLabel(p.Type)}) was not supplied by the scene");

            t[p.Name] = p.Type == "dna"
                ? BuildDnaValue(path, p.Name, value)
                : SceneBuilder.TomlToLua(value);
        }
        return t;
    }

    // A `dna` param: 64 bits, written as "0x" + EXACTLY 16 hex digits — strict, so
    // every trait slot is visibly hand-authored (the same hash always produces the
    // same behavior; nothing in the framework generates or derives one). The body
    // reads slots with `params.<name>:trait(i)` (1..16, most-significant first,
    // each 0..15) and the raw string with `:hex()`.
    private static readonly System.Text.RegularExpressions.Regex DnaPattern =
        new("^0x[0-9a-fA-F]{16}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static LuaValue BuildDnaValue(string path, string name, object? v)
    {
        if (v is not string s || !DnaPattern.IsMatch(s))
            throw new EvaluateException(
                $"{path}: param '{name}' (dna) must be \"0x\" + exactly 16 hex digits " +
                "(e.g. \"0xA13F00C2D4E5B677\" — one hand-chosen digit per trait slot); " +
                $"got '{v}'");
        var norm = "0x" + s[2..].ToUpperInvariant();
        var nibbles = new int[16];
        for (int i = 0; i < 16; i++)
            nibbles[i] = System.Convert.ToInt32(s[2 + i].ToString(), 16);

        var t = new LuaTable();
        t["hex"] = new LuaFunction((c, ct) => { c.Return(norm); return new(1); });
        t["trait"] = new LuaFunction((c, ct) =>
        {
            var arg = c.GetArgument(c.ArgumentCount - 1);
            var idx = arg.Type == LuaValueType.Number ? (int)arg.Read<double>() : -1;
            if (idx < 1 || idx > 16)
                throw new EvaluateException($"dna:trait(i): slot {idx} is out of range 1..16");
            c.Return((double)nibbles[idx - 1]);
            return new(1);
        });
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
            "dna" => v is string,                           // format checked in BuildDnaValue
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
