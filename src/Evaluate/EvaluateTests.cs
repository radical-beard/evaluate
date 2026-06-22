using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Lua;

namespace Evaluate;

// Enforcement regression suite. Run with: godot --headless -- --test
// Returns the number of failures (process exit code).
public static class EvaluateTests
{
    public static int Run(Action<string> log)
    {
        int passed = 0, failed = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) { passed++; log($"  PASS  {name}"); }
            else { failed++; log($"  FAIL  {name}  {detail}"); }
        }

        static string Read(string n) => Godot.FileAccess.GetFileAsString($"res://tests/{n}");
        static Loader NewLoader() => new(Read, _ => { });

        log("[tests] Evaluate enforcement suite");

        // 1: undeclared global is unreachable
        Check("sandbox blocks an undeclared global (os)",
            Throws(() => { NewLoader().Require("forbidden_global.evt"); }));

        // 2: undeclared engine API is unreachable
        Check("sandbox blocks an undeclared api (input)",
            Throws(() => { NewLoader().Require("undeclared_api.evt"); }));

        // 3: returns contract requires the declared accessors to exist
        Check("returns contract rejects a missing accessor",
            Throws(() => { NewLoader().Require("missing_accessor.evt"); }));

        // 4: a lifecycle hook may be registered only once per scene namespace
        // (system_a/system_b are both global, so they share the global namespace)
        Check("a hook can only be registered once per scene", DoubleRegisterRejected());

        // 5 (positive control): read-only property exposes getter, hides setter
        {
            bool ok = false; string detail = "";
            try
            {
                var handle = NewLoader().Require("readonly_module.evt").Read<LuaTable>();
                var hasGet = handle["get_hp"].Type == LuaValueType.Function;
                var noSet = handle["set_hp"].Type == LuaValueType.Nil;
                ok = hasGet && noSet;
                detail = $"get_hp={handle["get_hp"].Type}, set_hp={handle["set_hp"].Type}";
            }
            catch (Exception e) { detail = e.Message; }
            Check("read-only property exposes getter, hides setter", ok, detail);
        }

        // 6: hot reload refreshes a system's behavior, preserving its identity
        {
            var sink = new List<string>();
            var src = new Dictionary<string, string>
            {
                ["greeter.evt"] = "---\nregister:\n - on_start\n---\nfunction on_start() print('hello v1') end\n",
            };
            var loader = new Loader(
                n => src.TryGetValue(n, out var s) ? s : "", sink.Add);

            var sys = loader.LoadSystem("greeter.evt");
            loader.Call(sys.OnStart);
            var v1 = sink.Contains("hello v1");

            src["greeter.evt"] = src["greeter.evt"].Replace("hello v1", "hello v2");
            loader.ReloadOnChange("greeter.evt");
            loader.Call(sys.OnStart);                 // same system instance, refreshed hook
            var v2 = sink.Contains("hello v2");

            Check("hot reload refreshes a system in place", v1 && v2, $"v1={v1}, v2={v2}");
        }

        // 7 (positive control): metatable-based OOP works in the sandbox
        // (setmetatable + __index inheritance + method dispatch + raw*/getmetatable).
        {
            bool ok = false; string detail = "";
            try
            {
                var handle = NewLoader().Require("metatable_oop.evt").Read<LuaTable>();
                var result = handle["result"].Type == LuaValueType.String ? handle["result"].Read<string>() : "";
                ok = result == "Rex says woof | Rex has 4 legs | raw=true | gm=true";
                detail = $"result=\"{result}\"";
            }
            catch (Exception e) { detail = e.Message; }
            Check("metatable OOP (setmetatable + inheritance + raw*) works in the sandbox", ok, detail);
        }

        // 8: the SAME hook in DIFFERENT scenes is allowed (the core new feature)
        Check("the same hook may be registered in different scenes", !Throws(() =>
        {
            var loader = NewLoader();
            loader.LoadSystem("scene_a.evt");   // scenes: [a]
            loader.LoadSystem("scene_b.evt");   // scenes: [b]
        }));

        // 9: the same hook in the SAME scene is still rejected
        Check("the same hook in the same scene is rejected", Throws(() =>
        {
            var loader = NewLoader();
            loader.LoadSystem("scene_a.evt");
            loader.LoadSystem("scene_a_dup.evt");   // scenes: [a] again
        }));

        // 10: scene.change switches the active system set; a global system
        // stays active across scenes, scene-scoped ones only while active.
        {
            var root = new Node();
            var loader = NewLoader();
            loader.SetGlobalRoot(root);
            loader.LoadSystem("global_update.evt");   // global
            loader.LoadSystem("scene_a.evt");         // scenes: [a]
            loader.LoadSystem("scene_b.evt");         // scenes: [b]

            bool Active(string path) => loader.ActiveSystems.Any(s => s.Path == path);

            loader.GotoScene("a");
            var inA = Active("global_update.evt") && Active("scene_a.evt") && !Active("scene_b.evt");
            loader.GotoScene("b");
            var inB = Active("global_update.evt") && Active("scene_b.evt") && !Active("scene_a.evt");

            Check("scene switch gates hooks; global stays active", inA && inB, $"inA={inA}, inB={inB}");
            root.QueueFree();
        }

        // 11: a node script runs with `self` bound to its node
        {
            bool ok = false; string detail = "";
            try
            {
                var root = new Node();
                var loader = NewLoader();
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayerFromFile();     // global.scene -> Probe + self_node.node.evt
                var probe = root.GetChildCount() > 0 ? root.GetChild(0) : null;
                ok = probe is not null && probe.HasMeta("ready") && probe.GetMeta("ready").AsBool();
                detail = probe is null ? "no node instantiated" : $"meta ready={probe.HasMeta("ready")}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("a node script runs with self bound to its node", ok, detail);
        }

        // 12: SceneFile parses the keyed/nested [nodes.X] schema into a tree
        {
            var spec = SceneFile.Parse(
                "start_scene = \"x\"\n" +
                "[nodes.Player]\ntype = \"Node3D\"\nscript = \"p.node.evt\"\nposition = [1, 2, 3]\n" +
                "[nodes.Player.Camera]\ntype = \"Camera3D\"\n");
            var player = spec.Nodes.Count > 0 ? spec.Nodes[0] : null;
            var camera = player is { Children.Count: > 0 } ? player.Children[0] : null;
            var ok = spec.StartScene == "x" && spec.Nodes.Count == 1
                && player!.Name == "Player" && player.Type == "Node3D"
                && player.Script == "p.node.evt" && player.Props.ContainsKey("position")
                && camera is not null && camera.Name == "Camera" && camera.Type == "Camera3D";
            Check("scene file parses keyed/nested [nodes.X] into a tree", ok,
                $"roots={spec.Nodes.Count}, children={(player?.Children.Count ?? -1)}");
        }

        // 13: SceneBuilder builds the real Godot node tree (editor-import core)
        {
            bool ok = false; string detail = "";
            try
            {
                var spec = SceneFile.Parse(
                    "[nodes.Player]\ntype = \"Node3D\"\nposition = [1, 2, 3]\n" +
                    "[nodes.Player.Camera]\ntype = \"Camera3D\"\n");
                var binder = new GodotBinder(Lua.LuaState.Create());
                var packed = SceneBuilder.BuildPackedScene(spec, binder);
                var root = packed.Instantiate();
                var player = root.GetNodeOrNull("Player");
                var camera = root.GetNodeOrNull("Player/Camera");
                ok = player is Node3D && camera is Camera3D
                    && ((Node3D)player).Position == new Vector3(1, 2, 3);
                detail = $"player={player?.GetType().Name}, camera={camera?.GetType().Name}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("SceneBuilder builds a PackedScene node tree", ok, detail);
        }

        // 14: nested nodes resolve uniquely BY PATH (the scene.find guarantee),
        // even when the same child name appears under two different parents.
        {
            bool ok = false; string detail = "";
            try
            {
                var spec = SceneFile.Parse(
                    "[nodes.A]\ntype = \"Node3D\"\n[nodes.A.Item]\ntype = \"Node2D\"\n" +
                    "[nodes.B]\ntype = \"Node3D\"\n[nodes.B.Item]\ntype = \"Node3D\"\n");
                var binder = new GodotBinder(Lua.LuaState.Create());
                var container = new Node();
                foreach (var n in spec.Nodes) container.AddChild(SceneBuilder.BuildNode(n, binder));
                var aItem = container.GetNodeOrNull("A/Item");
                var bItem = container.GetNodeOrNull("B/Item");
                ok = aItem is Node2D && bItem is Node3D;   // same name, different paths, correct nodes
                detail = $"A/Item={aItem?.GetType().Name}, B/Item={bItem?.GetType().Name}";
                container.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("nested nodes resolve uniquely by path", ok, detail);
        }

        // 15: malformed TOML surfaces as a clean EvaluateException, NOT a raw
        // TomlException (so callers can handle it and the game degrades, not crashes)
        {
            bool ok = false; string detail = "no throw";
            try { SceneFile.Parse("this is not = = toml {{{"); }
            catch (EvaluateException) { ok = true; }
            catch (Exception e) { detail = $"wrong type: {e.GetType().Name}"; }
            Check("malformed TOML throws a clean EvaluateException", ok, detail);
        }

        // 16: a node name with a Godot-reserved char is rejected at parse time
        // (it would otherwise be silently sanitized and break path-based find)
        Check("reserved-char node name is rejected", Throws(() =>
            SceneFile.Parse("[nodes.\"a/b\"]\ntype = \"Node3D\"\n")));

        // 17: a sub-table is always a child (even keyed 'script'); a node with no
        // type is rejected with a clear error
        {
            var spec = SceneFile.Parse("[nodes.Root]\ntype = \"Node3D\"\n[nodes.Root.script]\ntype = \"Node2D\"\n");
            var child = spec.Nodes.Count == 1 && spec.Nodes[0].Children.Count == 1 ? spec.Nodes[0].Children[0] : null;
            var subTableIsChild = child is not null && child.Name == "script" && child.Type == "Node2D";
            var missingTypeRejected = Throws(() => SceneFile.Parse("[nodes.X]\nposition = [1, 2, 3]\n"));
            Check("sub-table is a child (even keyed 'script'); missing type rejected",
                subTableIsChild && missingTypeRejected, $"child={child?.Type}, missingRejected={missingTypeRejected}");
        }

        // 18: nested config tables marshal to a Lua table, not nil (no silent loss)
        {
            var nested = new Dictionary<string, object> { ["a"] = (double)1, ["b"] = "x" };
            var lua = SceneBuilder.TomlToLua(nested);
            var ok = lua.Type == LuaValueType.Table
                && lua.Read<LuaTable>()["a"].Read<double>() == 1
                && lua.Read<LuaTable>()["b"].Read<string>() == "x";
            Check("nested table marshals to a Lua table (not nil)", ok, $"type={lua.Type}");
        }

        // 19: a .toml edit hot-reloads LIVE — config is a live view over the TOML
        // cache, not a frozen snapshot, so a value read in a hook reflects the latest
        // saved file with no script re-run (the bare-toml hot-reload that was missing).
        {
            var sink = new List<string>();
            var src = new Dictionary<string, string>
            {
                ["tuner.evt"] =
                    "---\nconfig:\n - t.toml\nregister:\n - on_start\n---\n" +
                    "function on_start() print('label=' .. config.tune.label) end\n",
                ["t.toml"] = "[tune]\nlabel = \"one\"\n",
            };
            var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", sink.Add);
            var sys = loader.LoadSystem("tuner.evt");
            loader.Call(sys.OnStart);
            var v1 = sink.Contains("label=one");

            src["t.toml"] = "[tune]\nlabel = \"two\"\n";
            loader.ReloadOnChange("t.toml");
            loader.Call(sys.OnStart);                  // SAME closure, no re-run
            var v2 = sink.Contains("label=two");

            Check("a .toml edit hot-reloads live (config is a live view)", v1 && v2, $"v1={v1}, v2={v2}");
        }

        // 20: global.scene hot-reloads the PERSISTENT layer in place — a manifest
        // edit rebuilds the nodes (same names freed + recreated) and applies the
        // change. Was previously "restart to apply persistent-node changes".
        {
            var root = new Node();
            var src = new Dictionary<string, string>
            {
                ["global.scene"] = "[nodes.Hero]\ntype = \"Node3D\"\nposition = [1, 0, 0]\n",
            };
            var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
            loader.SetGlobalRoot(root);
            loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"]));
            var p1 = root.GetNodeOrNull("Hero") is Node3D h1 && h1.Position == new Vector3(1, 0, 0);

            src["global.scene"] = "[nodes.Hero]\ntype = \"Node3D\"\nposition = [2, 0, 0]\n";
            loader.ReloadOnChange("global.scene");
            var p2 = root.GetNodeOrNull("Hero") is Node3D h2 && h2.Position == new Vector3(2, 0, 0);
            var single = root.GetChildren().Count(c => c.Name == "Hero") <= 1;   // old freed, no dup name

            Check("global.scene hot-reloads the persistent layer in place",
                p1 && p2 && single, $"p1={p1}, p2={p2}, single={single}");
            root.QueueFree();
        }

        // 21: an ACTIVE scene's .scene file hot-reloads — the active scene is rebuilt
        // in place and the same-named container keeps its name (detach-before-readd).
        {
            var root = new Node();
            var src = new Dictionary<string, string>
            {
                ["lvl.scene"] = "[nodes.Box]\ntype = \"Node3D\"\nposition = [5, 0, 0]\n",
            };
            var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
            loader.SetGlobalRoot(root);
            loader.GotoScene("lvl");
            var s1 = root.GetNodeOrNull("lvl/Box") is Node3D b1 && b1.Position == new Vector3(5, 0, 0);

            src["lvl.scene"] = "[nodes.Box]\ntype = \"Node3D\"\nposition = [6, 0, 0]\n";
            loader.ReloadOnChange("lvl.scene");
            var s2 = root.GetNodeOrNull("lvl/Box") is Node3D b2 && b2.Position == new Vector3(6, 0, 0);

            Check("an active scene's .scene file hot-reloads in place", s1 && s2, $"s1={s1}, s2={s2}");
            root.QueueFree();
        }

        // 22: scene node `meta` + `groups` are parsed and applied (A1)
        {
            bool ok = false; string detail = "";
            try
            {
                var spec = SceneFile.Parse(
                    "[nodes.Hero]\ntype = \"Node3D\"\ngroups = [\"player\", \"alive\"]\n" +
                    "meta = { mode = \"game\", level = 3 }\n");
                var n = spec.Nodes[0];
                var parsed = n.Groups.Count == 2 && n.Meta.Count == 2 && (string)n.Meta["mode"] == "game";
                var binder = new GodotBinder(Lua.LuaState.Create());
                var container = new Node();
                container.AddChild(SceneBuilder.BuildNode(n, binder));
                var hero = container.GetNodeOrNull("Hero");
                ok = parsed && hero is not null
                    && hero.IsInGroup("player") && hero.IsInGroup("alive")
                    && hero.HasMeta("mode") && hero.GetMeta("mode").AsString() == "game"
                    && hero.HasMeta("level") && hero.GetMeta("level").AsInt32() == 3;
                detail = $"parsed={parsed}, player={hero?.IsInGroup("player")}, mode={(hero?.HasMeta("mode") == true ? hero.GetMeta("mode").AsString() : "-")}";
                container.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("scene node meta + groups are applied", ok, detail);
        }

        // 23: a sub-table keyed `meta` is metadata, NOT a child; `groups` must be an array
        Check("malformed groups (non-array) is rejected", Throws(() =>
            SceneFile.Parse("[nodes.X]\ntype = \"Node3D\"\ngroups = \"player\"\n")));

        // 24: a global system may register + fire on_quit (A4 — persist-on-exit hook)
        {
            var sink = new List<string>();
            var src = new Dictionary<string, string>
            {
                ["saver.evt"] = "---\nregister:\n - on_quit\n---\nfunction on_quit() print('flushed') end\n",
            };
            var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", sink.Add);
            var sys = loader.LoadSystem("saver.evt");
            loader.Call(sys.Hooks["on_quit"]);
            Check("a global system can register + fire on_quit", sink.Contains("flushed"));
        }

        // 25: on_quit is global-only — a scene-scoped on_quit is rejected
        Check("scene-scoped on_quit is rejected", Throws(() =>
        {
            var src = new Dictionary<string, string>
            {
                ["bad_quit.evt"] = "---\nscenes:\n - a\nregister:\n - on_quit\n---\nfunction on_quit() end\n",
            };
            new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { }).LoadSystem("bad_quit.evt");
        }));

        // 26: the sql capability core — exec/query/params/async+flush (A5)
        {
            bool ok = false; string detail = "";
            try
            {
                var sql = new Sql(_ => { });
                var no = System.Array.Empty<object?>();
                sql.ExecStmt("DROP TABLE IF EXISTS _t", no, true);
                sql.ExecStmt("CREATE TABLE _t(id INTEGER PRIMARY KEY, name TEXT, n REAL)", no, true);
                var ins = sql.ExecStmt("INSERT INTO _t(name, n) VALUES(@p1, @p2)", new object?[] { "alice", 3.5 }, true);
                sql.ExecStmt("INSERT INTO _t(name, n) VALUES(@p1, @p2)", new object?[] { "bob", 7.0 }, false);  // async
                sql.Flush();
                var rows = sql.QueryStmt("SELECT name, n FROM _t ORDER BY id", no);
                var cnt = sql.QueryStmt("SELECT COUNT(*) AS c FROM _t WHERE n > @p1", new object?[] { 5.0 });
                ok = ins.changes == 1 && ins.lastId == 1
                    && rows.Count == 2 && (string)rows[0]["name"]! == "alice" && (string)rows[1]["name"]! == "bob"
                    && Convert.ToInt64(cnt[0]["c"]) == 1;
                detail = $"ins=({ins.changes},{ins.lastId}), rows={rows.Count}, cnt={(cnt.Count > 0 ? cnt[0]["c"] : "-")}";
                sql.ExecStmt("DROP TABLE _t", no, true);
                sql.Dispose();
            }
            catch (Exception e) { detail = e.Message; }
            Check("sql exec/query/params/async+flush round-trip", ok, detail);
        }

        // 27: the sql capability round-trips through Lua (apis: sql)
        {
            bool ok = false; string detail = "";
            try
            {
                var handle = NewLoader().Require("sql_probe.evt").Read<LuaTable>();
                var result = handle["result"].Type == LuaValueType.String ? handle["result"].Read<string>() : "";
                ok = result == "zed:2";
                detail = $"result=\"{result}\"";
            }
            catch (Exception e) { detail = e.Message; }
            Check("sql capability round-trips through Lua", ok, detail);
        }

        // 28: inline sub-resource property — `mesh = { _type = "BoxMesh", size = [..] }` (A2)
        {
            bool ok = false; string detail = "";
            try
            {
                var spec = SceneFile.Parse(
                    "[nodes.M]\ntype = \"MeshInstance3D\"\nmesh = { _type = \"BoxMesh\", size = [2, 3, 4] }\n");
                var binder = new GodotBinder(Lua.LuaState.Create());
                var container = new Node();
                container.AddChild(SceneBuilder.BuildNode(spec.Nodes[0], binder));
                var box = container.GetNodeOrNull<MeshInstance3D>("M")?.Mesh as BoxMesh;
                ok = box is not null && box.Size == new Vector3(2, 3, 4);
                detail = $"size={box?.Size}";
                container.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("inline sub-resource property builds (A2)", ok, detail);
        }

        // 29: resource-by-path — a res:// string on a Resource property loads it (A2)
        {
            bool ok = false; string detail = "";
            try
            {
                var spec = SceneFile.Parse("[nodes.M]\ntype = \"MeshInstance3D\"\nmesh = \"res://box.tres\"\n");
                var binder = new GodotBinder(Lua.LuaState.Create());
                var container = new Node();
                container.AddChild(SceneBuilder.BuildNode(spec.Nodes[0], binder));
                var box = container.GetNodeOrNull<MeshInstance3D>("M")?.Mesh as BoxMesh;
                ok = box is not null && box.Size == new Vector3(4, 5, 6);
                detail = $"size={box?.Size}";
                container.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("resource-by-path property loads (A2)", ok, detail);
        }

        // 30: `instance = "scene"` builds the sub-scene's roots as children (A3)
        {
            bool ok = false; string detail = "";
            try
            {
                var subSpec = SceneFile.Parse("[nodes.Inner]\ntype = \"Node3D\"\n[nodes.Inner2]\ntype = \"Node2D\"\n");
                var spec = SceneFile.Parse("[nodes.Wrap]\ntype = \"Node3D\"\ninstance = \"sub\"\n");
                var binder = new GodotBinder(Lua.LuaState.Create());
                IReadOnlyList<NodeSpec> Resolve(string n) => n == "sub" ? subSpec.Nodes : new List<NodeSpec>();
                var container = new Node();
                container.AddChild(SceneBuilder.BuildNode(spec.Nodes[0], binder, null, Resolve));
                ok = container.GetNodeOrNull("Wrap/Inner") is Node3D && container.GetNodeOrNull("Wrap/Inner2") is Node2D;
                detail = $"inner={container.GetNodeOrNull("Wrap/Inner")?.GetType().Name}";
                container.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("instance builds sub-scene roots as children (A3)", ok, detail);
        }

        // 31: `unique = true` (owner + %Name) and declarative `connections` wire up (A3)
        {
            bool ok = false; string detail = "";
            try
            {
                var root = new Node();
                var loader = NewLoader();
                loader.SetGlobalRoot(root);
                loader.GotoScene("a3");                       // res://tests/a3.scene
                var container = root.GetNodeOrNull("a3");
                var emitter = container?.GetNodeOrNull("Emitter");
                var target = container?.GetNodeOrNull("Target");
                var uniqueOk = emitter is not null && emitter.UniqueNameInOwner && emitter.Owner == container
                    && target?.GetNodeOrNull("%Emitter") == emitter;
                var connOk = emitter is not null && emitter.GetSignalConnectionList("renamed").Count == 1;
                ok = uniqueOk && connOk;
                detail = $"unique={uniqueOk}, conn={connOk}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("scene unique-name + declarative connection wire up (A3)", ok, detail);
        }

        // 32: an instance= cycle is rejected with a clean error (NOT an uncatchable SOF) (A3)
        Check("instance cycle is rejected, not recursed", Throws(() =>
        {
            var spec = SceneFile.Parse("[nodes.Loop]\ntype = \"Node3D\"\ninstance = \"self\"\n");
            var binder = new GodotBinder(Lua.LuaState.Create());
            IReadOnlyList<NodeSpec> Resolve(string n) => spec.Nodes;   // any name -> the self-instancing node
            SceneBuilder.BuildNode(spec.Nodes[0], binder, null, Resolve);
        }));

        // 33: on_load runs on first load AND re-runs on hot reload (script + declared config) (0.5.2)
        {
            var sink = new List<string>();
            var src = new Dictionary<string, string>
            {
                ["global.scene"] = "[nodes.Probe]\ntype = \"Node\"\nscript = \"loader.node.evt\"\n",
                ["loader.node.evt"] = "---\nconfig:\n - p.toml\nregister:\n - on_load\n---\nfunction on_load() print('load=' .. config.p.n) end\n",
                ["p.toml"] = "[p]\nn = \"one\"\n",
            };
            var root = new Node();
            var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", sink.Add);
            loader.SetGlobalRoot(root);
            loader.LoadGlobalLayerFromFile();                                   // first load -> on_load (n=one)
            var first = sink.Contains("load=one");
            src["loader.node.evt"] = src["loader.node.evt"].Replace("'load='", "'reload='");
            loader.ReloadOnChange("loader.node.evt");                           // script reload -> on_load re-fires
            var onScript = sink.Contains("reload=one");
            src["p.toml"] = "[p]\nn = \"two\"\n";
            loader.ReloadOnChange("p.toml");                                    // config reload -> on_load (n=two)
            var onConfig = sink.Contains("reload=two");
            Check("on_load fires on first load + script reload + config reload",
                first && onScript && onConfig, $"first={first}, script={onScript}, config={onConfig}");
            root.QueueFree();
        }

        // 34: system on_load (FireSystemsLoad) + on_unload-before-reload + focus/pause register (0.5.2)
        {
            bool ok = false; string detail = "";
            try
            {
                var sink = new List<string>();
                var src = new Dictionary<string, string>
                {
                    ["sys.evt"] = "---\nregister:\n - on_load\n - on_unload\n - on_focus_out\n - on_pause\n---\n" +
                                  "function on_load() print('sysload') end\nfunction on_unload() print('sysunload') end\n" +
                                  "function on_focus_out() end\nfunction on_pause() end\n",
                };
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", sink.Add);
                loader.LoadSystem("sys.evt");          // registering focus/pause must not throw
                loader.FireSystemsLoad();              // system first on_load
                var sysLoad = sink.Contains("sysload");
                sink.Clear();
                loader.ReloadOnChange("sys.evt");      // reload -> on_unload THEN on_load
                var order = sink.Count == 2 && sink[0] == "sysunload" && sink[1] == "sysload";
                ok = sysLoad && order;
                detail = $"load={sysLoad}, order={order} [{string.Join(",", sink)}]";
            }
            catch (Exception e) { detail = e.Message; }
            Check("system on_load + on_unload-before-reload + focus/pause registration", ok, detail);
        }

        log($"[tests] {passed} passed, {failed} failed");
        return failed;
    }

    private static bool DoubleRegisterRejected()
    {
        var loader = new Loader(
            n => Godot.FileAccess.GetFileAsString($"res://tests/{n}"), _ => { });
        loader.LoadSystem("system_a.evt");
        return Throws(() => { loader.LoadSystem("system_b.evt"); });
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; }
        catch { return true; }
    }
}
