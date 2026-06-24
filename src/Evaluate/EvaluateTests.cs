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

        // 35: SceneWriter round-trips primitives + vectors + nesting (parse -> build ->
        // serialize -> parse yields an equivalent spec; the rebuilt node matches too)
        {
            bool ok = false; string detail = "";
            try
            {
                var (a, b, text) = RoundTrip(
                    "start_scene = \"x\"\n" +
                    "[nodes.Level]\ntype = \"Node3D\"\n" +
                    "[nodes.Level.Enemy]\ntype = \"Node3D\"\nposition = [3, 0, 0]\nvisible = false\n" +
                    "[nodes.Level.Enemy.Weapon]\ntype = \"Node3D\"\nposition = [0, 0, 1]\n");
                var binder = new GodotBinder(Lua.LuaState.Create());
                var c = new Node();
                foreach (var n in b.Nodes) c.AddChild(SceneBuilder.BuildNode(n, binder));
                var enemy = c.GetNodeOrNull<Node3D>("Level/Enemy");
                ok = SpecEquals(a, b) && b.StartScene == "x"
                    && enemy is not null && enemy.Position == new Vector3(3, 0, 0) && !enemy.Visible;
                detail = $"equal={SpecEquals(a, b)}, pos={enemy?.Position}, text=<<{text}>>";
                c.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("SceneWriter round-trips primitives/vectors/nesting", ok, detail);
        }

        // 36: reserved keys survive — script, groups, meta, unique, connections
        {
            bool ok = false; string detail = "";
            try
            {
                var (a, b, _) = RoundTrip(
                    "[nodes.Emitter]\ntype = \"Node\"\nscript = \"e.node.evt\"\nunique = true\n" +
                    "groups = [\"player\", \"alive\"]\nmeta = { mode = \"game\", level = 3 }\n" +
                    "connections = [ { signal = \"renamed\", to = \"Target\", method = \"queue_free\" } ]\n" +
                    "[nodes.Target]\ntype = \"Node\"\n");
                var em = b.Nodes.FirstOrDefault(n => n.Name == "Emitter");
                ok = SpecEquals(a, b) && em is not null && em.Script == "e.node.evt" && em.Unique
                    && em.Groups.Count == 2 && em.Meta.Count == 2 && em.Connections.Count == 1
                    && em.Connections[0].Signal == "renamed" && em.Connections[0].Method == "queue_free";
                detail = $"equal={SpecEquals(a, b)}, script={em?.Script}, conn={em?.Connections.Count}";
            }
            catch (Exception e) { detail = e.Message; }
            Check("SceneWriter round-trips reserved keys (script/groups/meta/unique/connections)", ok, detail);
        }

        // 37: inline sub-resource property round-trips (mesh = { _type = "BoxMesh", size = [..] })
        {
            bool ok = false; string detail = "";
            try
            {
                var (a, b, text) = RoundTrip(
                    "[nodes.M]\ntype = \"MeshInstance3D\"\nmesh = { _type = \"BoxMesh\", size = [2, 3, 4] }\n");
                var binder = new GodotBinder(Lua.LuaState.Create());
                var c = new Node();
                c.AddChild(SceneBuilder.BuildNode(b.Nodes[0], binder));
                var box = c.GetNodeOrNull<MeshInstance3D>("M")?.Mesh as BoxMesh;
                ok = SpecEquals(a, b) && box is not null && box.Size == new Vector3(2, 3, 4)
                    && text.Contains("_type = \"BoxMesh\"");
                detail = $"equal={SpecEquals(a, b)}, size={box?.Size}";
                c.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("SceneWriter round-trips inline _type sub-resource", ok, detail);
        }

        // 38: a res:// resource path round-trips as a string (not expanded inline)
        {
            bool ok = false; string detail = "";
            try
            {
                var (a, b, text) = RoundTrip("[nodes.M]\ntype = \"MeshInstance3D\"\nmesh = \"res://box.tres\"\n");
                ok = SpecEquals(a, b) && b.Nodes[0].Props.TryGetValue("mesh", out var m) && (m as string) == "res://box.tres"
                    && text.Contains("\"res://box.tres\"") && !text.Contains("_type");
                detail = $"equal={SpecEquals(a, b)}, text=<<{text}>>";
            }
            catch (Exception e) { detail = e.Message; }
            Check("SceneWriter round-trips a res:// resource path", ok, detail);
        }

        // 39: spatial transform fidelity — a set Transform3D survives as the clean
        // position/rotation/scale triple and rebuilds to (approximately) the same transform
        {
            bool ok = false; string detail = "";
            try
            {
                var binder = new GodotBinder(Lua.LuaState.Create());
                var src = new Node();
                var n = (Node3D)binder.Instantiate("Node3D")!;
                n.Name = "Mover";
                n.Transform = new Transform3D(Basis.FromEuler(new Vector3(0.1f, 0.2f, 0.3f)), new Vector3(1, 2, 3));
                src.AddChild(n);
                var text = SceneWriter.WriteContainer(src, binder);
                var spec = SceneFile.Parse(text);
                var dst = new Node();
                dst.AddChild(SceneBuilder.BuildNode(spec.Nodes[0], binder));
                var rebuilt = dst.GetNodeOrNull<Node3D>("Mover");
                ok = rebuilt is not null && rebuilt.Transform.Origin.IsEqualApprox(new Vector3(1, 2, 3))
                    && rebuilt.Transform.Basis.GetEuler().IsEqualApprox(new Vector3(0.1f, 0.2f, 0.3f));
                detail = $"origin={rebuilt?.Transform.Origin}, euler={rebuilt?.Transform.Basis.GetEuler()}";
                src.QueueFree(); dst.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("SceneWriter round-trips a spatial transform (pos/rot/scale)", ok, detail);
        }

        // 40: an instance= node emits `instance` and does NOT inline the instanced children
        {
            bool ok = false; string detail = "";
            try
            {
                var subSpec = SceneFile.Parse("[nodes.Inner]\ntype = \"Node3D\"\n");
                IReadOnlyList<NodeSpec> Resolve(string nm) => nm == "sub" ? subSpec.Nodes : new List<NodeSpec>();
                var (a, b, text) = RoundTrip("[nodes.Wrap]\ntype = \"Node3D\"\ninstance = \"sub\"\n", Resolve);
                ok = SpecEquals(a, b) && b.Nodes[0].Instance == "sub" && b.Nodes[0].Children.Count == 0
                    && text.Contains("instance = \"sub\"") && !text.Contains("Inner");
                detail = $"equal={SpecEquals(a, b)}, text=<<{text}>>";
            }
            catch (Exception e) { detail = e.Message; }
            Check("SceneWriter emits instance= without inlining instanced children", ok, detail);
        }

        // 41: minimal emission — a plain node writes only `type`; an author-written
        // value equal to the type default is still preserved
        {
            bool ok = false; string detail = "";
            try
            {
                var (_, plain, _) = RoundTrip("[nodes.P]\ntype = \"Node3D\"\n");
                var (_, zero, _) = RoundTrip("[nodes.P]\ntype = \"Node3D\"\nposition = [0, 0, 0]\n");
                ok = plain.Nodes[0].Props.Count == 0
                    && zero.Nodes[0].Props.ContainsKey("position");
                detail = $"plainProps={plain.Nodes[0].Props.Count}, zeroHasPos={zero.Nodes[0].Props.ContainsKey("position")}";
            }
            catch (Exception e) { detail = e.Message; }
            Check("SceneWriter emits minimal props (default-diff + original-key preserve)", ok, detail);
        }

        // 42: floats emit cleanly (2.2, not a 17-digit float64 expansion)
        {
            bool ok = false; string detail = "";
            try
            {
                var (_, _, text) = RoundTrip("[nodes.P]\ntype = \"Node3D\"\nposition = [2.2, 0, 0]\n");
                ok = text.Contains("[2.2, 0, 0]");
                detail = $"text=<<{text}>>";
            }
            catch (Exception e) { detail = e.Message; }
            Check("SceneWriter emits clean float formatting", ok, detail);
        }

        // 43: a composite-struct property round-trips via a `_type` table (the gap is closed) —
        // set Aabb in C#, serialize, reparse, rebuild, confirm the value survives
        {
            bool ok = false; string detail = "";
            try
            {
                var binder = new GodotBinder(Lua.LuaState.Create());
                var src = new Node();
                var m = (MeshInstance3D)binder.Instantiate("MeshInstance3D")!;
                m.Name = "M";
                m.CustomAabb = new Aabb(new Vector3(1, 2, 3), new Vector3(4, 5, 6));
                src.AddChild(m);
                var text = SceneWriter.WriteContainer(src, binder);
                var spec = SceneFile.Parse(text);
                var dst = new Node();
                dst.AddChild(SceneBuilder.BuildNode(spec.Nodes[0], binder));
                var rebuilt = dst.GetNodeOrNull<MeshInstance3D>("M");
                ok = text.Contains("_type = \"Aabb\"") && rebuilt is not null
                    && rebuilt.CustomAabb.Position.IsEqualApprox(new Vector3(1, 2, 3))
                    && rebuilt.CustomAabb.Size.IsEqualApprox(new Vector3(4, 5, 6));
                detail = $"text=<<{text}>>";
                src.QueueFree(); dst.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("composite struct (Aabb) round-trips via a _type table", ok, detail);
        }

        // 44: a REAL on-disk scene file round-trips (parse -> build -> serialize ->
        // parse) — exercises the writer on actual authored content, not test strings
        {
            bool ok = false; string detail = "";
            try
            {
                var src = Godot.FileAccess.GetFileAsString("res://scripts/global.scene");
                var a = SceneFile.Parse(src);
                var binder = new GodotBinder(Lua.LuaState.Create());
                var container = new Node();
                foreach (var n in a.Nodes) container.AddChild(SceneBuilder.BuildNode(n, binder));
                var text = SceneWriter.WriteContainer(container, binder, a.StartScene);
                var b = SceneFile.Parse(text);
                var player = b.Nodes.FirstOrDefault(n => n.Name == "Player");
                var cam = player?.Children.FirstOrDefault(n => n.Name == "Camera");
                ok = SpecEquals(a, b) && b.StartScene == "menu"
                    && player?.Script == "player.node.evt" && cam is not null
                    && cam.Props.ContainsKey("position");
                detail = $"equal={SpecEquals(a, b)}, start={b.StartScene}";
                container.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("SceneWriter round-trips the real global.scene file", ok, detail);
        }

        // 45: the EDITOR path — build -> PackedScene.Pack -> Instantiate (a .tscn save +
        // reopen) -> SceneWriter must still recover script/groups/meta/unique/connections
        // from the `__evt_*` stash (i.e. Pack preserves node meta across the round-trip)
        {
            bool ok = false; string detail = "";
            try
            {
                var spec = SceneFile.Parse(
                    "[nodes.Emitter]\ntype = \"Node3D\"\nscript = \"e.node.evt\"\nunique = true\n" +
                    "position = [1, 2, 3]\ngroups = [\"player\"]\nmeta = { mode = \"game\" }\n" +
                    "connections = [ { signal = \"renamed\", to = \"Target\", method = \"queue_free\" } ]\n" +
                    "[nodes.Target]\ntype = \"Node\"\n");
                var binder = new GodotBinder(Lua.LuaState.Create());
                var root = new Node { Name = "S" };
                foreach (var n in spec.Nodes) root.AddChild(SceneBuilder.BuildNode(n, binder));
                root.SetMeta("__evt_source", "res://scripts/x.scene");   // plugin's Save targets
                root.SetMeta("__evt_startscene", "menu");
                void Own(Node p) { foreach (var c in p.GetChildren()) { c.Owner = root; Own(c); } }
                Own(root);

                var packed = new PackedScene();
                packed.Pack(root);
                root.Free();
                var reopened = packed.Instantiate();         // == the editor's reopened .tscn

                // The Save path recovers source path + start_scene from the root meta...
                var srcOk = reopened.GetMeta("__evt_source").AsString() == "res://scripts/x.scene"
                    && reopened.GetMeta("__evt_startscene").AsString() == "menu";
                var text = SceneWriter.WriteContainer(reopened, binder, reopened.GetMeta("__evt_startscene").AsString());
                var b = SceneFile.Parse(text);
                var em = b.Nodes.FirstOrDefault(n => n.Name == "Emitter");
                ok = srcOk && b.StartScene == "menu" && em is not null && em.Script == "e.node.evt" && em.Unique
                    && em.Groups.Contains("player") && em.Meta.ContainsKey("mode")
                    && em.Connections.Count == 1 && em.Props.ContainsKey("position");
                detail = $"src={srcOk}, start={b.StartScene}, script={em?.Script}, groups={em?.Groups.Count}, conn={em?.Connections.Count}";
                reopened.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("editor build->pack->reopen preserves the round-trip stash", ok, detail);
        }

        // 46: Rect2 property round-trips via a _type table (Sprite2D.region_rect)
        {
            bool ok = false; string detail = "";
            try
            {
                var binder = new GodotBinder(Lua.LuaState.Create());
                var src = new Node();
                var s = (Sprite2D)binder.Instantiate("Sprite2D")!;
                s.Name = "S"; s.RegionRect = new Rect2(1, 2, 3, 4);
                src.AddChild(s);
                var text = SceneWriter.WriteContainer(src, binder);
                var dst = new Node();
                dst.AddChild(SceneBuilder.BuildNode(SceneFile.Parse(text).Nodes[0], binder));
                var r = dst.GetNodeOrNull<Sprite2D>("S");
                ok = text.Contains("_type = \"Rect2\"") && r is not null && r.RegionRect == new Rect2(1, 2, 3, 4);
                detail = $"rect={r?.RegionRect}";
                src.QueueFree(); dst.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("composite struct (Rect2) round-trips via a _type table", ok, detail);
        }

        // 47: a Transform2D property round-trips via a _type table — a NON-Node2D transform
        // (CanvasLayer) is not decomposed, so this exercises the struct-table write + read
        {
            bool ok = false; string detail = "";
            try
            {
                var binder = new GodotBinder(Lua.LuaState.Create());
                var src = new Node();
                var c = (CanvasLayer)binder.Instantiate("CanvasLayer")!;
                c.Name = "C"; c.Transform = new Transform2D(new Vector2(1, 0), new Vector2(0, 1), new Vector2(5, 6));
                src.AddChild(c);
                var text = SceneWriter.WriteContainer(src, binder);
                var dst = new Node();
                dst.AddChild(SceneBuilder.BuildNode(SceneFile.Parse(text).Nodes[0], binder));
                var c2 = dst.GetNodeOrNull<CanvasLayer>("C");
                ok = text.Contains("_type = \"Transform2D\"") && c2 is not null
                    && c2.Transform.Origin.IsEqualApprox(new Vector2(5, 6));
                detail = $"origin={c2?.Transform.Origin}";
                src.QueueFree(); dst.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("composite struct (Transform2D) round-trips via a _type table", ok, detail);
        }

        // 48: a nested Transform3D `_type` table (Basis sub-table + positional origin) parses + builds
        {
            bool ok = false; string detail = "";
            try
            {
                var spec = SceneFile.Parse(
                    "[nodes.N]\ntype = \"Node3D\"\n" +
                    "transform = { _type = \"Transform3D\", " +
                    "basis = { _type = \"Basis\", row0 = [1,0,0], row1 = [0,1,0], row2 = [0,0,1] }, " +
                    "origin = [5,2,3] }\n");
                var binder = new GodotBinder(Lua.LuaState.Create());
                var dst = new Node();
                dst.AddChild(SceneBuilder.BuildNode(spec.Nodes[0], binder));
                var n = dst.GetNodeOrNull<Node3D>("N");
                ok = n is not null && n.Transform.Origin.IsEqualApprox(new Vector3(5, 2, 3))
                    && n.Transform.Basis.IsEqualApprox(Basis.Identity);
                detail = $"origin={n?.Transform.Origin}";
                dst.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("Transform3D _type table (flat-9 basis) parses + builds", ok, detail);
        }

        // 49: a `/`-keyed property (Control theme override) round-trips as a quoted key
        {
            bool ok = false; string detail = "";
            try
            {
                var binder = new GodotBinder(Lua.LuaState.Create());
                var src = new Node();
                var l = (Label)binder.Instantiate("Label")!;
                l.Name = "L"; l.Set("theme_override_font_sizes/font_size", 24);
                src.AddChild(l);
                var text = SceneWriter.WriteContainer(src, binder);
                var dst = new Node();
                dst.AddChild(SceneBuilder.BuildNode(SceneFile.Parse(text).Nodes[0], binder));
                var l2 = dst.GetNodeOrNull<Label>("L");
                ok = text.Contains("\"theme_override_font_sizes/font_size\"") && l2 is not null
                    && l2.Get("theme_override_font_sizes/font_size").AsInt32() == 24;
                detail = $"text=<<{text}>>";
                src.QueueFree(); dst.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("a /-keyed theme-override property round-trips as a quoted key", ok, detail);
        }

        // 50: a top-level `description` field round-trips as data (replaces comment preservation)
        {
            bool ok = false; string detail = "";
            try
            {
                var (a, b, text) = RoundTrip("description = \"the boot menu\"\n[nodes.Menu]\ntype = \"Node\"\n");
                ok = a.Description == "the boot menu" && b.Description == "the boot menu"
                    && text.Contains("description = \"the boot menu\"") && SpecEquals(a, b);
                detail = $"a={a.Description}, b={b.Description}";
            }
            catch (Exception e) { detail = e.Message; }
            Check("top-level description field round-trips", ok, detail);
        }

        log($"[tests] {passed} passed, {failed} failed");
        return failed;
    }

    // parse -> build under a container -> SceneWriter -> parse again.
    // Returns (originalSpec, roundTrippedSpec, emittedText).
    private static (SceneSpec a, SceneSpec b, string text) RoundTrip(
        string toml, Func<string, IReadOnlyList<NodeSpec>>? resolve = null)
    {
        var a = SceneFile.Parse(toml);
        var binder = new GodotBinder(Lua.LuaState.Create());
        var container = new Node();
        foreach (var n in a.Nodes) container.AddChild(SceneBuilder.BuildNode(n, binder, null, resolve));
        var text = SceneWriter.WriteContainer(container, binder, a.StartScene, a.Description);
        var b = SceneFile.Parse(text);
        container.QueueFree();
        return (a, b, text);
    }

    private static bool SpecEquals(SceneSpec a, SceneSpec b)
        => a.StartScene == b.StartScene && a.Description == b.Description && NodesEqual(a.Nodes, b.Nodes);

    private static bool NodesEqual(List<NodeSpec> a, List<NodeSpec> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var na in a)
        {
            var nb = b.FirstOrDefault(x => x.Name == na.Name);
            if (nb is null || !NodeEquals(na, nb)) return false;
        }
        return true;
    }

    private static bool NodeEquals(NodeSpec a, NodeSpec b)
    {
        if (a.Name != b.Name || a.Type != b.Type || a.Script != b.Script
            || a.Instance != b.Instance || a.Unique != b.Unique) return false;
        if (!SetEquals(a.Groups, b.Groups)) return false;
        if (a.Connections.Count != b.Connections.Count) return false;
        for (int i = 0; i < a.Connections.Count; i++)
            if (a.Connections[i].Signal != b.Connections[i].Signal
                || a.Connections[i].To != b.Connections[i].To
                || a.Connections[i].Method != b.Connections[i].Method) return false;
        return MapEquals(a.Meta, b.Meta) && MapEquals(a.Props, b.Props) && NodesEqual(a.Children, b.Children);
    }

    private static bool SetEquals(List<string> a, List<string> b)
        => a.Count == b.Count && a.All(b.Contains);

    private static bool MapEquals(Dictionary<string, object> a, Dictionary<string, object> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out var v) || !ValEquals(kv.Value, v)) return false;
        return true;
    }

    // The parse object model: bool / double / string / object[] / Dictionary, with
    // numeric tolerance so float-vs-double and emission rounding don't read as drift.
    private static bool ValEquals(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is double da && b is double db)
            return Math.Abs(da - db) <= 1e-5 * Math.Max(1.0, Math.Max(Math.Abs(da), Math.Abs(db)));
        if (a is bool ba && b is bool bb) return ba == bb;
        if (a is string sa && b is string sb) return sa == sb;
        if (a is object[] xa && b is object[] xb)
            return xa.Length == xb.Length && !xa.Where((t, i) => !ValEquals(t, xb[i])).Any();
        if (a is Dictionary<string, object> ma && b is Dictionary<string, object> mb)
            return MapEquals(ma, mb);
        return Equals(a, b);
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
