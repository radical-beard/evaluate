using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Godot;
using Lua;
using Lua.Standard;

namespace Evaluate;

// A loaded "system" script: one that `register`s lifecycle hooks with the engine.
public sealed class LoadedSystem
{
    public string Path = "";
    public readonly Dictionary<string, LuaValue> Hooks = new();

    public LuaValue OnStart => Hooks.TryGetValue("on_start", out var v) ? v : LuaValue.Nil;
    public LuaValue OnUpdate => Hooks.TryGetValue("on_update", out var v) ? v : LuaValue.Nil;
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
    private GodotObject? _worldNode;        // scene-tree parent for spawned nodes
    private Persistence? _persistence;      // lazily opened SQLite save store

    private readonly LuaTable _std;
    private readonly LuaTable _godot;     // ambient: godot.<Type> resolves lazily
    private readonly LuaValue _loadFn;
    private readonly Dictionary<string, LuaValue> _requireCache = new();
    private readonly Dictionary<string, LuaTable> _tomlCache = new();
    private readonly Dictionary<string, string> _claimedHooks = new();   // hook -> owning script

    private readonly List<LoadedSystem> _systems = new();
    private readonly HashSet<string> _loadedScripts = new();             // every .evt Run so far
    private readonly HashSet<string> _configFiles = new();               // every declared toml
    private readonly HashSet<string> _assetFiles = new();                // every declared asset

    // Systems discovered/registered in this project (scripts with a register: block).
    public IReadOnlyList<LoadedSystem> Systems => _systems;

    // Input state the host can toggle to simulate the player pressing keys.
    public HashSet<string> Pressed { get; } = new();

    private static readonly string[] SafeGlobals =
        { "pairs", "ipairs", "next", "type", "tostring", "tonumber",
          "error", "assert", "select", "string", "math", "table" };

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

    // The scene-tree node that the `world` API wraps (set by the host).
    public void SetWorld(GodotObject node) => _worldNode = node;

    // ---- public entry points -------------------------------------------------

    // Auto-discovery: load every discovered script that declares a `register:`
    // block as a system. The host supplies the file list (directory scan).
    public void LoadAllSystems(IEnumerable<string> scriptFiles)
    {
        foreach (var file in scriptFiles)
        {
            if (!file.EndsWith(".evt")) continue;
            if (Frontmatter.Parse(_readScript(file)).Register.Count == 0) continue;
            LoadSystem(file);
        }
    }

    public LoadedSystem LoadSystem(string path)
    {
        var (env, _, fm) = Run(path);
        var sys = _systems.Find(s => s.Path == path) ?? new LoadedSystem { Path = path };

        foreach (var name in fm.Register)
        {
            if (!KnownHooks.Contains(name))
                throw new EvaluateException(
                    $"{path} registers unknown hook '{name}' (valid: {string.Join(", ", KnownHooks)})");
            if (env[name].Type != LuaValueType.Function)
                throw new EvaluateException($"{path} registers '{name}' but never defines it");

            // One registration per hook, project-wide — but a script may re-register
            // its own hooks on reload.
            if (_claimedHooks.TryGetValue(name, out var owner) && owner != path)
                throw new EvaluateException(
                    $"hook '{name}' is already registered by '{owner}'; only one registration " +
                    $"per project is allowed (attempted again by '{path}')");
            _claimedHooks[name] = path;

            sys.Hooks[name] = env[name];
        }

        if (!_systems.Contains(sys)) _systems.Add(sys);
        return sys;
    }

    // The lifecycle hooks the host drives, mapped from Godot's Node lifecycle.
    public static readonly string[] KnownHooks =
        { "on_start", "on_update", "on_physics_update", "on_input" };

    // Wrap a live Godot object for Lua (used by the host to pass e.g. InputEvents).
    public LuaValue Wrap(GodotObject o) => _godotBinder.WrapInstance(o);

    // ---- hot reload + watch targets ------------------------------------------

    // Files the host should watch: every loaded script, declared config, and
    // declared asset. (Assets are declared in frontmatter precisely so we know
    // to watch them.)
    public IEnumerable<string> WatchTargets()
    {
        foreach (var s in _loadedScripts) yield return s;
        foreach (var c in _configFiles) yield return c;
        foreach (var a in _assetFiles) yield return a;
    }

    // Apply a live change to `path`. Returns a short description of what happened.
    // Engine entities are preserved; a changed system re-runs its body to refresh
    // its hook closures, but on_start is NOT re-invoked (no re-spawn).
    public string ReloadOnChange(string path)
    {
        _tomlCache.Remove(path);
        _requireCache.Remove(path);

        var sys = _systems.Find(s => s.Path == path);
        if (sys != null)
        {
            LoadSystem(path);                     // re-run body, refresh hook closures
            return $"reloaded system {path} (hooks refreshed, entities preserved)";
        }
        if (path.EndsWith(".toml")) return $"invalidated config {path} (re-read on next use)";
        if (path.EndsWith(".evt")) return $"invalidated module {path} (re-required on next use)";
        return $"noted change to asset {path} (would re-import)";
    }

    public void Call(LuaValue fn, params LuaValue[] args) => Sync(_lua.CallAsync(fn, args));

    // ---- sandbox + require ----------------------------------------------------

    private (LuaTable env, LuaValue ret, Frontmatter fm) Run(string path)
    {
        var fm = Frontmatter.Parse(_readScript(path));

        // Record watch targets: the script itself, its configs, its assets.
        _loadedScripts.Add(path);
        foreach (var c in fm.Configs) _configFiles.Add(c);
        foreach (var a in fm.Assets) _assetFiles.Add(a);

        var env = BuildSandbox(path, fm);

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
    private LuaTable BuildSandbox(string path, Frontmatter fm)
    {
        var env = new LuaTable();
        foreach (var name in SafeGlobals) env[name] = _lua.Environment[name];
        env["print"] = _lua.Environment["print"];
        env["std"] = _std;
        env["godot"] = _godot;        // ambient: Godot types available by default

        var config = new LuaTable();
        foreach (var file in fm.Configs)
            foreach (var kv in LoadToml(file))
                config[kv.Key] = kv.Value;
        env["config"] = config;

        foreach (var api in fm.Apis)
        {
            if (api.StartsWith("godot:")) continue;     // godot is ambient; declaration optional
            env[api] = BuildApi(path, api);
        }

        env["require"] = new LuaFunction((ctx, ct) =>
        {
            ctx.Return(Require(ctx.GetArgument<string>(0)));
            return new(1);
        });

        return env;
    }

    public LuaValue Require(string path)
    {
        if (_requireCache.TryGetValue(path, out var cached)) return cached;
        var (_, ret, fm) = Run(path);
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

    private LuaValue BuildApi(string path, string name) => name switch
    {
        "input" => BuildInputApi(),
        "world" => BuildWorldApi(path),
        "save" => BuildSaveApi(),
        _ => throw new EvaluateException($"{path} requests unknown api '{name}'"),
    };

    // `save` persists runtime/player data to SQLite (~/.local/share/evaluate/).
    // save.set(key, v) / save.get(key[, default]) / save.delete(key).
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

    // `world` is the live scene-tree node that spawned game objects parent to —
    // a real Godot node exposed through the binder (world:add_child(node), …).
    // Game objects ARE Godot nodes; there is no separate entity system.
    private LuaValue BuildWorldApi(string path)
    {
        if (_worldNode is null)
            throw new EvaluateException($"{path} requests 'world' but no world node is set");
        return _godotBinder.WrapInstance(_worldNode);
    }

    // ---- helpers --------------------------------------------------------------

    private LuaTable LoadToml(string file)
    {
        if (_tomlCache.TryGetValue(file, out var cached)) return cached;

        var parsed = Toml.Parse(_readScript(file));
        var root = new LuaTable();
        foreach (var section in parsed)
        {
            if (section.Key.Length == 0) continue;
            var t = new LuaTable();
            foreach (var kv in section.Value) t[kv.Key] = TomlToLua(kv.Value);
            root[section.Key] = t;
        }
        _tomlCache[file] = root;
        return root;
    }

    private static LuaValue TomlToLua(object v) => v switch
    {
        bool b => b,
        double d => d,
        string s => s,
        object[] arr => ArrayToLua(arr),
        _ => LuaValue.Nil,
    };

    private static LuaValue ArrayToLua(object[] arr)
    {
        var list = new LuaTable();
        for (int i = 0; i < arr.Length; i++) list[i + 1] = TomlToLua(arr[i]);   // 1-based, recursive
        return list;
    }

    private static T Sync<T>(ValueTask<T> vt) =>
        vt.IsCompleted ? vt.Result : vt.AsTask().GetAwaiter().GetResult();
}

// Evaluate-layer error (capability violation, contract breach, etc.).
public sealed class EvaluateException : Exception
{
    public EvaluateException(string message) : base(message) { }
}
