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
                // (child 0 is now the implicit PlayerController — resolve the probe by name)
                var probe = root.GetNodeOrNull("Probe");
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

        // 51: runtime struct properties read back as NAMED-field tables (.size.x, .basis.z),
        // not the codec's positional serialization repr — regression guard for the script API
        {
            bool ok = false; string detail = "";
            try
            {
                var handle = NewLoader().Require("struct_read.evt").Read<LuaTable>();
                var result = handle["result"].Type == LuaValueType.String ? handle["result"].Read<string>() : "";
                ok = result == "true,true,true";
                detail = $"result=\"{result}\" (rect,aabb,basis)";
            }
            catch (Exception e) { detail = e.Message; }
            Check("runtime struct properties are named-field (.size.x / .basis.z)", ok, detail);
        }

        // 52: frontmatter `params:` grammar — a bare scalar is the default (type inferred),
        // a type token alone is required (no default), and `type = default` is both.
        {
            var fm = Frontmatter.Parse(
                "---\nparams:\n  hp: 100\n  faction: \"neutral\"\n  speed: number\n  range: number = 8\n---\nbody\n");
            var byName = fm.Params.ToDictionary(p => p.Name);
            var ok =
                byName["hp"].Type == "number" && byName["hp"].HasDefault && (double)byName["hp"].Default! == 100
                && byName["faction"].Type == "string" && (string)byName["faction"].Default! == "neutral"
                && byName["speed"].Type == "number" && byName["speed"].Required
                && byName["range"].Type == "number" && byName["range"].HasDefault && (double)byName["range"].Default! == 8;
            Check("frontmatter params: parses default/required/type=default forms", ok, $"count={fm.Params.Count}");
        }

        // 53: a scene node's `params = {..}` parses into NodeSpec.Params — a reserved key,
        // neither a child node nor an engine property.
        {
            var spec = SceneFile.Parse(
                "[nodes.Mob]\ntype = \"Node\"\nscript = \"m.node.evt\"\nparams = { hp = 60, faction = \"goblin\" }\n");
            var mob = spec.Nodes[0];
            var ok = mob.Params.Count == 2 && (double)mob.Params["hp"] == 60
                && (string)mob.Params["faction"] == "goblin" && !mob.Props.ContainsKey("params");
            Check("scene node params parses into NodeSpec.Params", ok,
                $"params={mob.Params.Count}, leakedToProps={mob.Props.ContainsKey("params")}");
        }

        // 54: params reach the node script — the scene OVERRIDES some, the script's DEFAULT
        // fills the rest (read via the `params` global, stashed onto the node here).
        {
            bool ok = false; string detail = "";
            try
            {
                var src = new Dictionary<string, string>
                {
                    ["global.scene"] =
                        "[nodes.Mob]\ntype = \"Node\"\nscript = \"mob.node.evt\"\nparams = { hp = 60 }\n",
                    ["mob.node.evt"] =
                        "---\nparams:\n  hp: 100\n  faction: \"neutral\"\nregister:\n - on_attach\n---\n" +
                        "function on_attach() self:set_meta(\"hp\", params.hp) self:set_meta(\"faction\", params.faction) end\n",
                };
                var root = new Node();
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"]));
                var mob = root.GetNodeOrNull("Mob");
                var overridden = mob is not null && mob.GetMeta("hp").AsDouble() == 60;            // scene wins
                var defaulted = mob is not null && mob.GetMeta("faction").AsString() == "neutral"; // default fills
                ok = overridden && defaulted;
                detail = $"hp={mob?.GetMeta("hp").AsDouble()}, faction={mob?.GetMeta("faction").AsString()}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("scene params override defaults; omitted params fall back to defaults", ok, detail);
        }

        // 55: a scene param the script never declared is rejected — the scene cannot inject
        // values past the signature contract (mirrors the sandbox's undeclared-global stance).
        Check("an undeclared scene param is rejected", ParamLoadThrows(
            "[nodes.Mob]\ntype = \"Node\"\nscript = \"m.node.evt\"\nparams = { hp = 1, bogus = 2 }\n",
            "---\nparams:\n  hp: 100\nregister:\n - on_attach\n---\nfunction on_attach() end\n"));

        // 56: a declared param with no default is REQUIRED — the scene omitting it is an error.
        Check("a required param the scene omits is rejected", ParamLoadThrows(
            "[nodes.Mob]\ntype = \"Node\"\nscript = \"m.node.evt\"\n",
            "---\nparams:\n  speed: number\nregister:\n - on_attach\n---\nfunction on_attach() end\n"));

        // 57: a scene value whose type disagrees with the declared type is rejected.
        Check("a wrong-typed scene param is rejected", ParamLoadThrows(
            "[nodes.Mob]\ntype = \"Node\"\nscript = \"m.node.evt\"\nparams = { hp = \"lots\" }\n",
            "---\nparams:\n  hp: number\nregister:\n - on_attach\n---\nfunction on_attach() end\n"));

        // 58: scene params round-trip through SceneBuilder/SceneWriter (stashed as __evt_params).
        {
            bool ok = false; string detail = "";
            try
            {
                var (a, b, text) = RoundTrip(
                    "[nodes.Mob]\ntype = \"Node\"\nscript = \"m.node.evt\"\nparams = { hp = 60, faction = \"goblin\" }\n");
                var mob = b.Nodes.FirstOrDefault(n => n.Name == "Mob");
                ok = text.Contains("params = {") && mob is not null && mob.Params.Count == 2
                    && (double)mob.Params["hp"] == 60 && (string)mob.Params["faction"] == "goblin" && SpecEquals(a, b);
                detail = $"text=<<{text}>>";
            }
            catch (Exception e) { detail = e.Message; }
            Check("scene params round-trip through SceneWriter", ok, detail);
        }

        // 59: `params:` is a node-script concept — a SYSTEM script declaring it is rejected
        // (a system has no scene instance to be parameterized).
        {
            var src = new Dictionary<string, string>
            {
                ["sys.evt"] = "---\nparams:\n  hp: 100\nregister:\n - on_start\n---\nfunction on_start() end\n",
            };
            var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
            Check("a system script declaring params is rejected", Throws(() => loader.LoadSystem("sys.evt")));
        }

        // 60: frontmatter `require:` (list form) binds a module to a sandbox local, so the
        // body uses it with no `local x = require(...)` line, and the binding is the
        // returns-narrowed handle (the dependency's private field is not reachable).
        {
            bool ok = false; string detail = "";
            try
            {
                var handle = NewLoader().Require("require_binding.evt").Read<LuaTable>();
                var result = handle["result"].Type == LuaValueType.String ? handle["result"].Read<string>() : "";
                var narrowed = handle["narrowed"].Type == LuaValueType.Boolean && handle["narrowed"].Read<bool>();
                ok = result == "hello world" && narrowed;
                detail = $"result=\"{result}\", narrowed={narrowed}";
            }
            catch (Exception e) { detail = e.Message; }
            Check("frontmatter require: binds a module to a sandbox local (narrowed)", ok, detail);
        }

        // 61: the `params:`-style MAP form of `require:` resolves identically to the list form.
        {
            bool ok = false; string detail = "";
            try
            {
                var handle = NewLoader().Require("require_binding_map.evt").Read<LuaTable>();
                var result = handle["result"].Type == LuaValueType.String ? handle["result"].Read<string>() : "";
                ok = result == "hello map";
                detail = $"result=\"{result}\"";
            }
            catch (Exception e) { detail = e.Message; }
            Check("frontmatter require: accepts the map form too", ok, detail);
        }

        // 62: a require binding may not shadow a reserved/declared sandbox name (here `std`).
        Check("a require binding colliding with a reserved name is rejected",
            Throws(() => { NewLoader().Require("require_collision.evt"); }));

        // 63: a require cycle (a -> b -> a) is rejected with a clear error, not a stack overflow.
        {
            var src = new Dictionary<string, string>
            {
                ["cyc_a.evt"] = "---\nrequire:\n - b: \"cyc_b.evt\"\n---\nreturn {}\n",
                ["cyc_b.evt"] = "---\nrequire:\n - a: \"cyc_a.evt\"\n---\nreturn {}\n",
            };
            var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
            Check("a require cycle is rejected, not stack-overflowed",
                Throws(() => loader.Require("cyc_a.evt")));
        }

        // 64: a self-require (a script requiring itself) is rejected as a cycle.
        {
            var src = new Dictionary<string, string>
            {
                ["selfreq.evt"] = "---\nrequire:\n - me: \"selfreq.evt\"\n---\nreturn {}\n",
            };
            var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
            Check("a self-require is rejected as a cycle", Throws(() => loader.Require("selfreq.evt")));
        }

        // 65: changing a REQUIRED module reloads its consumer (two-way dependency reload).
        // The system captured the module's v1 value; editing only the module must refresh
        // the system's live hook so a subsequent call sees v2.
        {
            var sink = new List<string>();
            var src = new Dictionary<string, string>
            {
                ["dep_lib.evt"] = "---\nreturns:\n - msg\n---\nreturn { msg = \"v1\" }\n",
                ["dep_sys.evt"] = "---\nrequire:\n - lib: \"dep_lib.evt\"\nregister:\n - on_start\n---\n"
                                + "function on_start() print(lib.msg) end\n",
            };
            var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", sink.Add);
            var sys = loader.LoadSystem("dep_sys.evt");
            loader.Call(sys.OnStart);
            var v1 = sink.Contains("v1");

            src["dep_lib.evt"] = "---\nreturns:\n - msg\n---\nreturn { msg = \"v2\" }\n";
            loader.ReloadOnChange("dep_lib.evt");   // dependency change must cascade to the consumer
            loader.Call(sys.OnStart);               // same system instance, binding refreshed to v2
            var v2 = sink.Contains("v2");

            Check("changing a required module reloads its consumer", v1 && v2, $"v1={v1}, v2={v2}");
        }

        // 66: the cascade is transitive — module -> module -> system. Editing the leaf
        // module must rebuild the middle module AND refresh the system that requires it.
        {
            var sink = new List<string>();
            var src = new Dictionary<string, string>
            {
                ["leaf.evt"] = "---\nreturns:\n - v\n---\nreturn { v = \"a\" }\n",
                ["mid.evt"] = "---\nrequire:\n - leaf: \"leaf.evt\"\nreturns:\n - v\n---\n"
                            + "return { v = leaf.v .. \"-mid\" }\n",
                ["top.evt"] = "---\nrequire:\n - mid: \"mid.evt\"\nregister:\n - on_start\n---\n"
                            + "function on_start() print(mid.v) end\n",
            };
            var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", sink.Add);
            var sys = loader.LoadSystem("top.evt");
            loader.Call(sys.OnStart);
            var before = sink.Contains("a-mid");

            src["leaf.evt"] = "---\nreturns:\n - v\n---\nreturn { v = \"b\" }\n";
            loader.ReloadOnChange("leaf.evt");      // must transit leaf -> mid -> top
            loader.Call(sys.OnStart);
            var after = sink.Contains("b-mid");

            Check("a require change cascades transitively (leaf -> mid -> system)",
                before && after, $"before={before}, after={after}");
        }

        // 67: the ambient `godot` table is GONE — a body sees nil (apis-as-modules)
        {
            var src = new Dictionary<string, string>
            {
                ["amb.evt"] = "---\nreturns:\n - r\n---\nreturn { r = godot == nil }\n",
            };
            var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
            var r = loader.Require("amb.evt").Read<LuaTable>()["r"];
            Check("ambient godot table is gone", r.Type == LuaValueType.Boolean && r.Read<bool>());
        }

        // 68: a Godot class table is unreachable UNDECLARED, injected when DECLARED
        {
            bool gated, granted = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["undeclared_class.evt"] = "---\n---\nlocal n = Node3D.new()\nreturn {}\n",
                ["declared_class.evt"] =
                    "---\napis:\n - Node3D\nreturns:\n - ok\n---\n" +
                    "local n = Node3D.new()\nlocal ok = n ~= nil\nn:free()\nreturn { ok = ok }\n",
            };
            Loader L() => new(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
            gated = Throws(() => L().Require("undeclared_class.evt"));
            try
            {
                granted = L().Require("declared_class.evt").Read<LuaTable>()["ok"].Read<bool>();
            }
            catch (Exception e) { detail = e.Message; }
            Check("Godot class tables are gated by apis: declaration", gated && granted,
                $"gated={gated}, granted={granted} {detail}");
        }

        // 69: a Godot ENUM declared in apis: injects its value table
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["enum_api.evt"] = "---\napis:\n - Side\nreturns:\n - r\n---\nreturn { r = Side.Bottom }\n",
            };
            try
            {
                var r = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { })
                    .Require("enum_api.evt").Read<LuaTable>()["r"];
                ok = r.Type == LuaValueType.Number && r.Read<double>() == (double)(long)Side.Bottom;
                detail = $"Side.Bottom={r}";
            }
            catch (Exception e) { detail = e.Message; }
            Check("a declared Godot enum injects its value table", ok, detail);
        }

        // 70: the legacy `godot:` prefix is rejected with a migration hint
        Check("a 'godot:'-prefixed api is rejected", Throws(() =>
        {
            var src = new Dictionary<string, string>
            {
                ["prefixed.evt"] = "---\napis:\n - \"godot:Node3D\"\n---\nreturn {}\n",
            };
            new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { }).Require("prefixed.evt");
        }));

        // 71: imperative resource/file IO classes are NOT declarable capabilities
        Check("ResourceLoader is blocked from apis:", Throws(() =>
        {
            var src = new Dictionary<string, string>
            {
                ["blocked.evt"] = "---\napis:\n - ResourceLoader\n---\nreturn {}\n",
            };
            new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { }).Require("blocked.evt");
        }));

        // 72: an unknown api name is a load error (typo-proofing), not a silent nil
        Check("an unknown api name errors at load", Throws(() =>
        {
            var src = new Dictionary<string, string>
            {
                ["unknown.evt"] = "---\napis:\n - definitely_not_a_thing\n---\nreturn {}\n",
            };
            new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { }).Require("unknown.evt");
        }));

        // 73: a host-registered C# api is callable when declared, absent when not
        {
            bool ok = false, gated = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["uses_host.evt"] =
                    "---\napis:\n - mathex\nreturns:\n - r\n---\n" +
                    "return { r = string.format(\"%d-%s\", mathex.Add(2, 3), mathex.Tag()) }\n",
                ["no_host.evt"] = "---\nreturns:\n - r\n---\nreturn { r = mathex == nil }\n",
            };
            try
            {
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                loader.RegisterApi("mathex", new HostMath());
                var r = loader.Require("uses_host.evt").Read<LuaTable>()["r"];
                ok = r.Type == LuaValueType.String && r.Read<string>() == "5-host";
                gated = loader.Require("no_host.evt").Read<LuaTable>()["r"].Read<bool>();
                detail = $"r={r}, gated={gated}";
            }
            catch (Exception e) { detail = e.Message; }
            Check("a host api is callable when declared, absent when not", ok && gated, detail);
        }

        // 74: RegisterApi rejects reserved names, Godot-class collisions, and late registration
        {
            Loader L() => new(_ => "", _ => { });
            var reserved = Throws(() => L().RegisterApi("scene", new HostMath()));
            var collides = Throws(() => L().RegisterApi("Timer", new HostMath()));
            var late = Throws(() =>
            {
                var src = new Dictionary<string, string> { ["m.evt"] = "return {}\n" };
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                loader.Require("m.evt");
                loader.RegisterApi("mathex", new HostMath());
            });
            Check("RegisterApi rejects reserved/colliding/late registrations",
                reserved && collides && late, $"reserved={reserved}, collides={collides}, late={late}");
        }

        // 75: a declared asset loads eagerly and is injected under its bound name
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["uses_asset.evt"] =
                    "---\nassets:\n  tint: \"tests/assets/tint.gdshader\"\nreturns:\n - r\n---\n" +
                    "return { r = assets.tint:get_class() }\n",
            };
            try
            {
                var r = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { })
                    .Require("uses_asset.evt").Read<LuaTable>()["r"];
                ok = r.Type == LuaValueType.String && r.Read<string>() == "Shader";
                detail = $"r={r}";
            }
            catch (Exception e) { detail = e.Message; }
            Check("a declared asset loads and injects as assets.<name>", ok, detail);
        }

        // 76: a missing asset file is a LOAD error naming the binding, not a runtime nil
        Check("a missing asset file errors at load", Throws(() =>
        {
            var src = new Dictionary<string, string>
            {
                ["missing_asset.evt"] =
                    "---\nassets:\n  rig: \"tests/assets/nope.fbx\"\n---\nreturn {}\n",
            };
            new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { }).Require("missing_asset.evt");
        }));

        // 77: the pre-0.10 bare-list `assets:` form is rejected with a migration hint
        Check("legacy bare-path assets: entries are rejected", Throws(() =>
        {
            var src = new Dictionary<string, string>
            {
                ["legacy_assets.evt"] = "---\nassets:\n - \"models/x.fbx\"\n---\nreturn {}\n",
            };
            new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { }).Require("legacy_assets.evt");
        }));

        // 78: a glob binds a stem-keyed table of loaded resources
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["glob_asset.evt"] =
                    "---\nassets:\n  pack: \"tests/assets/glob_*.gdshader\"\nreturns:\n - r\n---\n" +
                    "return { r = assets.pack.glob_a ~= nil and assets.pack.glob_b ~= nil " +
                    "and assets.pack.glob_a:get_class() == \"Shader\" }\n",
            };
            try
            {
                var r = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { })
                    .Require("glob_asset.evt").Read<LuaTable>()["r"];
                ok = r.Type == LuaValueType.Boolean && r.Read<bool>();
                detail = $"r={r}";
            }
            catch (Exception e) { detail = e.Message; }
            Check("an asset glob binds a stem-keyed resource table", ok, detail);
        }

        // 79: a .gdshader edit hot-reloads IN PLACE — the same Shader instance (even one
        // captured at load) carries the new code, so live materials recompile for free
        {
            bool ok = false; string detail = "";
            var abs = ProjectSettings.GlobalizePath("res://tests/assets/hot.gdshader");
            var original = System.IO.File.ReadAllText(abs);
            try
            {
                var src = new Dictionary<string, string>
                {
                    ["hot_asset.evt"] =
                        "---\nassets:\n  hot: \"tests/assets/hot.gdshader\"\nreturns:\n - sh\n---\n" +
                        "return { sh = assets.hot }\n",
                };
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                var handle = loader.Require("hot_asset.evt").Read<LuaTable>();
                var shader = (Shader)handle["sh"].Read<GodotInstanceProxy>().Target;
                var v1 = shader.Code.Contains("0.1");

                System.IO.File.WriteAllText(abs, original.Replace("0.1", "0.9"));
                loader.ReloadOnChange("tests/assets/hot.gdshader");
                var v2 = shader.Code.Contains("0.9");   // SAME instance, new code

                ok = v1 && v2;
                detail = $"v1={v1}, v2={v2}";
            }
            catch (Exception e) { detail = e.Message; }
            finally { System.IO.File.WriteAllText(abs, original); }
            Check("a .gdshader edit hot-reloads the same Shader instance in place", ok, detail);
        }

        // 80: frontmatter `properties:` applies native engine state at attach, BEFORE on_attach
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["global.scene"] = "[nodes.P]\ntype = \"Node3D\"\nscript = \"props.node.evt\"\n",
                ["props.node.evt"] =
                    "---\nproperties:\n  position: [1, 2, 3]\n  visible: false\nregister:\n - on_attach\n---\n" +
                    "function on_attach() self:set_meta(\"attach_x\", self.position.x) end\n",
            };
            try
            {
                var root = new Node();
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"]));
                var p = root.GetNodeOrNull<Node3D>("P");
                ok = p is not null && p.Position == new Vector3(1, 2, 3) && !p.Visible
                    && p.GetMeta("attach_x").AsDouble() == 1;    // visible to on_attach already
                detail = $"pos={p?.Position}, visible={p?.Visible}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("properties: applies native props at attach (before on_attach)", ok, detail);
        }

        // 81: the scene's own property keys WIN over the script's `properties:`
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["global.scene"] =
                    "[nodes.P]\ntype = \"Node3D\"\nscript = \"props2.node.evt\"\nposition = [9, 9, 9]\n",
                ["props2.node.evt"] =
                    "---\nproperties:\n  position: [1, 2, 3]\n  visible: false\nregister:\n - on_attach\n---\n" +
                    "function on_attach() end\n",
            };
            try
            {
                var root = new Node();
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"]));
                var p = root.GetNodeOrNull<Node3D>("P");
                ok = p is not null && p.Position == new Vector3(9, 9, 9) && !p.Visible;
                detail = $"pos={p?.Position} (scene should win), visible={p?.Visible} (script should apply)";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("scene properties override the script's properties:", ok, detail);
        }

        // 82: an edited `properties:` block re-applies on script hot reload
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["global.scene"] = "[nodes.P]\ntype = \"Node3D\"\nscript = \"props3.node.evt\"\n",
                ["props3.node.evt"] =
                    "---\nproperties:\n  position: [1, 2, 3]\nregister:\n - on_attach\n---\nfunction on_attach() end\n",
            };
            try
            {
                var root = new Node();
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"]));
                var p = root.GetNodeOrNull<Node3D>("P");
                var before = p is not null && p.Position == new Vector3(1, 2, 3);
                src["props3.node.evt"] = src["props3.node.evt"].Replace("[1, 2, 3]", "[4, 5, 6]");
                loader.ReloadOnChange("props3.node.evt");
                var after = p is not null && p.Position == new Vector3(4, 5, 6);
                ok = before && after;
                detail = $"before={before}, after={after} (pos={p?.Position})";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("properties: re-applies on script hot reload", ok, detail);
        }

        // 83: properties: is node-only, and an unknown property name fails loudly
        {
            var onSystem = Throws(() =>
            {
                var src = new Dictionary<string, string>
                {
                    ["sysprops.evt"] =
                        "---\nproperties:\n  visible: false\nregister:\n - on_start\n---\nfunction on_start() end\n",
                };
                new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { }).LoadSystem("sysprops.evt");
            });
            var unknown = ParamLoadThrows(
                "[nodes.P]\ntype = \"Node3D\"\nscript = \"m.node.evt\"\n",
                "---\nproperties:\n  bogus_prop: 1\nregister:\n - on_attach\n---\nfunction on_attach() end\n");
            Check("properties: rejected on systems and for unknown property names",
                onSystem && unknown, $"onSystem={onSystem}, unknown={unknown}");
        }

        // 84: multiple behaviors on one node — script= first, then behaviors= in list
        // order; per-attachment params flow; all hooks fire
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["global.scene"] =
                    "[nodes.N]\ntype = \"Node\"\nscript = \"a.node.evt\"\n" +
                    "behaviors = [\"b.behavior.evt\", { script = \"c.behavior.evt\", params = { tag = \"cc\" } }]\n",
                ["a.node.evt"] = "---\nregister:\n - on_attach\n---\n" +
                    "function on_attach() self:set_meta(\"order\", self:get_meta(\"order\", \"\") .. \"a\") end\n",
                ["b.behavior.evt"] = "---\nregister:\n - on_attach\n---\n" +
                    "function on_attach() self:set_meta(\"order\", self:get_meta(\"order\", \"\") .. \"b\") end\n",
                ["c.behavior.evt"] = "---\nparams:\n  tag: string\nregister:\n - on_attach\n---\n" +
                    "function on_attach() self:set_meta(\"order\", self:get_meta(\"order\", \"\") .. \"c\") " +
                    "self:set_meta(\"tag\", params.tag) end\n",
            };
            try
            {
                var root = new Node();
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"]));
                var node = root.GetNodeOrNull("N");
                ok = node is not null && node.GetMeta("order").AsString() == "abc"
                    && node.GetMeta("tag").AsString() == "cc";
                detail = $"order={node?.GetMeta("order")}, tag={node?.GetMeta("tag")}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("multiple behaviors attach in order with per-attachment params", ok, detail);
        }

        // 85: one behavior file drives many nodes, each with its own params
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["global.scene"] =
                    "[nodes.X]\ntype = \"Node\"\nbehaviors = [{ script = \"shared.behavior.evt\", params = { id = \"x\" } }]\n" +
                    "[nodes.Y]\ntype = \"Node\"\nbehaviors = [{ script = \"shared.behavior.evt\", params = { id = \"y\" } }]\n",
                ["shared.behavior.evt"] = "---\nparams:\n  id: string\nregister:\n - on_attach\n---\n" +
                    "function on_attach() self:set_meta(\"id\", params.id) end\n",
            };
            try
            {
                var root = new Node();
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"]));
                ok = root.GetNodeOrNull("X")?.GetMeta("id").AsString() == "x"
                    && root.GetNodeOrNull("Y")?.GetMeta("id").AsString() == "y";
                detail = $"x={root.GetNodeOrNull("X")?.GetMeta("id")}, y={root.GetNodeOrNull("Y")?.GetMeta("id")}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("one behavior file drives many nodes with per-node params", ok, detail);
        }

        // 86: frontmatter composition — a behavior's `behaviors:` list joins the node;
        // a non-behavior extension in an attachment list is rejected
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["global.scene"] = "[nodes.N]\ntype = \"Node\"\nbehaviors = [\"outer.behavior.evt\"]\n",
                ["outer.behavior.evt"] = "---\nbehaviors:\n - inner.behavior.evt\nregister:\n - on_attach\n---\n" +
                    "function on_attach() self:set_meta(\"outer\", true) end\n",
                ["inner.behavior.evt"] = "---\nregister:\n - on_attach\n---\n" +
                    "function on_attach() self:set_meta(\"inner\", true) end\n",
            };
            try
            {
                var root = new Node();
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"]));
                var node = root.GetNodeOrNull("N");
                var composed = node is not null && node.HasMeta("outer") && node.HasMeta("inner");
                var badExt = ParamLoadThrows(
                    "[nodes.N]\ntype = \"Node\"\nbehaviors = [\"m.evt\"]\n",
                    "---\nregister:\n - on_attach\n---\nfunction on_attach() end\n");
                ok = composed && badExt;
                detail = $"composed={composed}, badExt={badExt}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("frontmatter behaviors: composes onto the node; wrong extension rejected", ok, detail);
        }

        // 87: a statemachine attaches, starts at initial, and takes `when`-guarded and
        // `after`-timed transitions from the physics tick
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["global.scene"] =
                    "[nodes.N]\ntype = \"Node\"\nbehaviors = [\"probe.behavior.evt\"]\nmachines = [\"m.statemachine.evt\"]\n",
                ["probe.behavior.evt"] = "---\nregister:\n - on_update\n---\n" +
                    "function on_update(dt) self:set_meta(\"st\", self.fsm.stance.state) end\n",
                ["m.statemachine.evt"] =
                    "---\nname: stance\nstates: [idle, alert]\ninitial: idle\n---\n" +
                    "return {\n" +
                    "  { from = \"idle\",  to = \"alert\", when = function(self) return self:get_meta(\"threat\", false) end },\n" +
                    "  { from = \"alert\", to = \"idle\",  after = 0.5 },\n" +
                    "}\n",
            };
            try
            {
                var root = new Node();
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"]));
                var node = root.GetNodeOrNull("N")!;
                LuaValue Probe() { var ln = loader.ActiveNodes.First(x => x.Path == "probe.behavior.evt"); return ln.Hooks["on_update"]; }
                loader.Call(Probe(), 0.0);
                var initial = node.GetMeta("st").AsString() == "idle";
                node.SetMeta("threat", true);
                loader.TickMachines(1.0 / 60);
                loader.Call(Probe(), 0.0);
                var guarded = node.GetMeta("st").AsString() == "alert";
                node.SetMeta("threat", false);            // so alert doesn't re-trigger after the timer
                loader.TickMachines(0.3);
                loader.Call(Probe(), 0.0);
                var held = node.GetMeta("st").AsString() == "alert";     // 0.3s < 0.5s
                loader.TickMachines(0.3);
                loader.Call(Probe(), 0.0);
                var timed = node.GetMeta("st").AsString() == "idle";     // 0.6s >= 0.5s
                ok = initial && guarded && held && timed;
                detail = $"initial={initial}, guarded={guarded}, held={held}, timed={timed}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("statemachine: initial + when-guard + after-timer transitions", ok, detail);
        }

        // 88: `on` events fire immediately, and the `self.fsm.<m>.<state> = fn` sugar
        // appends enter listeners that receive the origin state
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["global.scene"] =
                    "[nodes.N]\ntype = \"Node\"\nbehaviors = [\"sub.behavior.evt\"]\nmachines = [\"evm.statemachine.evt\"]\n",
                ["sub.behavior.evt"] = "---\nregister:\n - on_attach\n - on_load\n---\n" +
                    "function on_attach()\n" +
                    "  self.fsm.gate.open = function(from) self:set_meta(\"heard_from\", from) end\n" +
                    "  self.fsm.gate:on_exit(\"closed\", function(to) self:set_meta(\"exited_to\", to) end)\n" +
                    "end\n" +
                    "function on_load()\n" +
                    "  self:set_meta(\"fired\", self.fsm.gate:fire(\"knock\"))\n" +
                    "  self:set_meta(\"now\", self.fsm.gate.state)\n" +
                    "  self:set_meta(\"isopen\", self.fsm.gate:is(\"open\"))\n" +
                    "end\n",
                ["evm.statemachine.evt"] =
                    "---\nname: gate\nstates: [closed, open]\n---\n" +
                    "return { { from = \"closed\", to = \"open\", on = \"knock\",\n" +
                    "           run = function(self, from, to) self:set_meta(\"acted\", from .. \">\" .. to) end } }\n",
            };
            try
            {
                var root = new Node();
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"]));
                var node = root.GetNodeOrNull("N")!;
                ok = node.GetMeta("fired").AsBool()
                    && node.GetMeta("now").AsString() == "open"
                    && node.GetMeta("isopen").AsBool()
                    && node.GetMeta("heard_from").AsString() == "closed"
                    && node.GetMeta("exited_to").AsString() == "open"
                    && node.GetMeta("acted").AsString() == "closed>open";
                detail = $"fired={node.GetMeta("fired")}, now={node.GetMeta("now")}, " +
                         $"heard={node.GetMeta("heard_from")}, exited={node.GetMeta("exited_to")}, acted={node.GetMeta("acted")}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("statemachine: fire() + do action + enter/exit subscriptions", ok, detail);
        }

        // 89: a machine hot reload refreshes transitions but KEEPS state and listeners;
        // a behavior hot reload DROPS its stale listeners (no double-fire)
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["global.scene"] =
                    "[nodes.N]\ntype = \"Node\"\nbehaviors = [\"cnt.behavior.evt\"]\nmachines = [\"rm.statemachine.evt\"]\n",
                ["cnt.behavior.evt"] = "---\nregister:\n - on_load\n---\n" +
                    "function on_load()\n" +
                    "  self.fsm.sw.on_state = function() self:set_meta(\"n\", self:get_meta(\"n\", 0) + 1) end\n" +
                    "end\n",
                ["rm.statemachine.evt"] =
                    "---\nname: sw\nstates: [off_state, on_state]\n---\n" +
                    "return {\n" +
                    "  { from = \"off_state\", to = \"on_state\", on = \"toggle\" },\n" +
                    "  { from = \"on_state\", to = \"off_state\", on = \"toggle\" },\n" +
                    "}\n",
            };
            try
            {
                var root = new Node();
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"]));
                var node = root.GetNodeOrNull("N")!;

                // toggle -> on_state: listener fires once
                FireToggle(loader, src, node);
                var once = node.GetMeta("n", 0).AsInt32() == 1;

                // machine reload: state (on_state) + listener survive
                src["rm.statemachine.evt"] = src["rm.statemachine.evt"].Replace("name: sw", "name: sw # v2");
                loader.ReloadOnChange("rm.statemachine.evt");
                FireToggle(loader, src, node);   // -> off_state (state was kept, so toggle flips off)
                FireToggle(loader, src, node);   // -> on_state: kept listener fires again
                var kept = node.GetMeta("n", 0).AsInt32() == 2;

                // behavior reload: old listener dropped, on_load re-subscribes ONE fresh one
                loader.ReloadOnChange("cnt.behavior.evt");
                FireToggle(loader, src, node);   // -> off_state
                FireToggle(loader, src, node);   // -> on_state: exactly one increment
                var single = node.GetMeta("n", 0).AsInt32() == 3;

                ok = once && kept && single;
                detail = $"once={once}, kept={kept}, single={single} (n={node.GetMeta("n", 0).AsInt32()})";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("hot reload: machine keeps state+listeners; behavior drops stale listeners", ok, detail);
        }

        // 90: behaviors/machines lists round-trip through SceneWriter
        {
            bool ok = false; string detail = "";
            try
            {
                var (a, b, text) = RoundTrip(
                    "[nodes.Skiff]\ntype = \"Node3D\"\n" +
                    "behaviors = [\"hull.behavior.evt\", { script = \"cannons.behavior.evt\", params = { count = 2 } }]\n" +
                    "machines = [\"sail.statemachine.evt\"]\n");
                var skiff = b.Nodes.FirstOrDefault(n => n.Name == "Skiff");
                ok = SpecEquals(a, b) && skiff is not null
                    && skiff.Behaviors.Count == 2 && skiff.Behaviors[0].Script == "hull.behavior.evt"
                    && skiff.Behaviors[1].Params.Count == 1 && (double)skiff.Behaviors[1].Params["count"] == 2
                    && skiff.Machines.Count == 1 && skiff.Machines[0].Script == "sail.statemachine.evt"
                    && text.Contains("behaviors = [") && text.Contains("machines = [");
                detail = $"equal={SpecEquals(a, b)}, text=<<{text}>>";
            }
            catch (Exception e) { detail = e.Message; }
            Check("behaviors/machines round-trip through SceneWriter", ok, detail);
        }

        // 91: machine signature enforcement — no states, unknown transition state,
        // register: on a machine, duplicate machine name on one node
        {
            bool noStates = MachineLoadThrows("---\nname: m\n---\nreturn { { from = \"a\", to = \"b\", after = 1 } }\n");
            bool badState = MachineLoadThrows(
                "---\nname: m\nstates: [a, b]\n---\nreturn { { from = \"a\", to = \"zzz\", after = 1 } }\n");
            bool registers = MachineLoadThrows(
                "---\nname: m\nstates: [a]\nregister:\n - on_update\n---\nreturn { { from = \"a\", to = \"a\", after = 1 } }\n");
            bool twoTriggers = MachineLoadThrows(
                "---\nname: m\nstates: [a, b]\n---\nreturn { { from = \"a\", to = \"b\", after = 1, on = \"x\" } }\n");
            Check("machine signature is enforced (states/refs/register/triggers)",
                noStates && badState && registers && twoTriggers,
                $"noStates={noStates}, badState={badState}, registers={registers}, twoTriggers={twoTriggers}");
        }

        // 92: attributes + an instant-cost ability — declare, read, spend, cooldown, regen
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["global.scene"] = "[nodes.N]\ntype = \"Node\"\nbehaviors = [\"gas.behavior.evt\"]\n",
                ["gas.behavior.evt"] =
                    "---\nattributes:\n  water: { base: 100, min: 0, max: 100, regen: 20, regen_delay: 0.5 }\n" +
                    "abilities:\n - spray.ability\nregister:\n - on_attach\n - on_update\n---\n" +
                    "function on_attach()\n" +
                    "  self:set_meta(\"w0\", self.attributes.water)\n" +
                    "  self:set_meta(\"ok1\", self.abilities:activate(\"spray\"))\n" +
                    "  self:set_meta(\"w1\", self.attributes.water)\n" +
                    "  self:set_meta(\"ok2\", self.abilities:activate(\"spray\"))\n" +
                    "end\n" +
                    "function on_update(dt)\n" +
                    "  self:set_meta(\"w\", self.attributes.water)\n" +
                    "  self:set_meta(\"can\", self.abilities:can_activate(\"spray\"))\n" +
                    "end\n",
                ["spray.ability"] = "cooldown = 1.0\ncost = { attribute = \"water\", amount = 30.0 }\n",
            };
            try
            {
                var root = new Node();
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"]));
                var node = root.GetNodeOrNull("N")!;
                var declared = node.GetMeta("w0").AsDouble() == 100;
                var spent = node.GetMeta("ok1").AsBool() && node.GetMeta("w1").AsDouble() == 70;
                var cooled = !node.GetMeta("ok2").AsBool();
                loader.Tick(1.1);   // cooldown expires; regen kicks in after the 0.5s delay
                var probe = loader.ActiveNodes.First(x => x.Path == "gas.behavior.evt").Hooks["on_update"];
                loader.Call(probe, 0.0);
                var regened = Math.Abs(node.GetMeta("w").AsDouble() - 92) < 1e-6;   // 70 + 20*1.1
                var canAgain = node.GetMeta("can").AsBool();
                ok = declared && spent && cooled && regened && canAgain;
                detail = $"declared={declared}, spent={spent}, cooled={cooled}, " +
                         $"regened={regened} (w={node.GetMeta("w").AsDouble()}), canAgain={canAgain}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("attributes + instant ability: cost, cooldown, delayed regen", ok, detail);
        }

        // 93: a channeled ability drains per second, holds grant_tags, auto-deactivates
        // on exhaust (tag until recover), then recovers
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["global.scene"] = "[nodes.N]\ntype = \"Node\"\nbehaviors = [\"sprint.behavior.evt\"]\n",
                ["sprint.behavior.evt"] =
                    "---\nattributes:\n  water: { base: 10, min: 0, max: 100, regen: 50, regen_delay: 0.2, recover: 25 }\n" +
                    "abilities:\n - sprint.ability\nregister:\n - on_attach\n - on_update\n---\n" +
                    "function on_attach()\n" +
                    "  self.abilities:on_ended(\"sprint\", function(name, reason) self:set_meta(\"why\", reason) end)\n" +
                    "  self:set_meta(\"started\", self.abilities:activate(\"sprint\"))\n" +
                    "end\n" +
                    "function on_update(dt)\n" +
                    "  self:set_meta(\"w\", self.attributes.water)\n" +
                    "  self:set_meta(\"active\", self.abilities:is_active(\"sprint\"))\n" +
                    "  self:set_meta(\"tag\", self.abilities:has_tag(\"sprinting\"))\n" +
                    "  self:set_meta(\"exh\", self.abilities:has_tag(\"exhausted:water\"))\n" +
                    "  self:set_meta(\"can\", self.abilities:can_activate(\"sprint\"))\n" +
                    "end\n",
                ["sprint.ability"] =
                    "channeled = true\ncost = { attribute = \"water\", amount = 10.0 }\ngrant_tags = [\"sprinting\"]\n",
            };
            try
            {
                var root = new Node();
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"]));
                var node = root.GetNodeOrNull("N")!;
                var probe = loader.ActiveNodes.First(x => x.Path == "sprint.behavior.evt").Hooks["on_update"];
                void Snap() => loader.Call(probe, 0.0);

                var started = node.GetMeta("started").AsBool();
                loader.Tick(0.5); Snap();
                var draining = Math.Abs(node.GetMeta("w").AsDouble() - 5) < 1e-6
                    && node.GetMeta("active").AsBool() && node.GetMeta("tag").AsBool();
                loader.Tick(0.5); Snap();
                var exhausted = !node.GetMeta("active").AsBool() && !node.GetMeta("tag").AsBool()
                    && node.GetMeta("exh").AsBool() && !node.GetMeta("can").AsBool()
                    && node.GetMeta("why").AsString() == "exhausted";
                loader.Tick(0.3); Snap();
                var stillLocked = node.GetMeta("exh").AsBool();                  // 15 < recover 25
                loader.Tick(0.3); Snap();
                var recovered = !node.GetMeta("exh").AsBool() && node.GetMeta("can").AsBool();
                ok = started && draining && exhausted && stillLocked && recovered;
                detail = $"started={started}, draining={draining}, exhausted={exhausted}, " +
                         $"stillLocked={stillLocked}, recovered={recovered}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("channeled ability: drain, tags, exhaust lockout, recovery", ok, detail);
        }

        // 94: effects — instant mutation, while-active modifier via a channel, periodic dot
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["global.scene"] = "[nodes.N]\ntype = \"Node\"\nbehaviors = [\"fx.behavior.evt\"]\n",
                ["fx.behavior.evt"] =
                    "---\nattributes:\n  hp: { base: 50, min: 0, max: 100 }\n  thrust: { base: 0, min: 0, max: 100 }\n" +
                    "abilities:\n - boost.ability\nregister:\n - on_attach\n - on_update\n---\n" +
                    "function on_attach()\n" +
                    "  self.abilities:apply(\"heal.effect\")\n" +
                    "  self:set_meta(\"hp_healed\", self.attributes.hp)\n" +
                    "  self.abilities:activate(\"boost\")\n" +
                    "  self:set_meta(\"thrust_on\", self.attributes.thrust)\n" +
                    "  self.abilities:deactivate(\"boost\")\n" +
                    "  self:set_meta(\"thrust_off\", self.attributes.thrust)\n" +
                    "  self.abilities:apply(\"dot.effect\")\n" +
                    "end\n" +
                    "function on_update(dt) self:set_meta(\"hp\", self.attributes.hp) end\n",
                ["boost.ability"] = "channeled = true\n[[effects]]\neffect = \"boost.effect\"\n",
                ["boost.effect"] = "attribute = \"thrust\"\nop = \"add\"\nmagnitude = 30.0\nduration = -1\n",
                ["heal.effect"] = "attribute = \"hp\"\nop = \"add\"\nmagnitude = 20.0\nduration = 0\n",
                ["dot.effect"] = "attribute = \"hp\"\nop = \"add\"\nmagnitude = -5.0\nduration = 2.1\nperiod = 1.0\n",
            };
            try
            {
                var root = new Node();
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"]));
                var node = root.GetNodeOrNull("N")!;
                var probe = loader.ActiveNodes.First(x => x.Path == "fx.behavior.evt").Hooks["on_update"];
                var healed = node.GetMeta("hp_healed").AsDouble() == 70;
                var modifier = node.GetMeta("thrust_on").AsDouble() == 30
                    && node.GetMeta("thrust_off").AsDouble() == 0;
                loader.Tick(1.0); loader.Tick(1.0); loader.Tick(0.2);
                loader.Call(probe, 0.0);
                var dotted = node.GetMeta("hp").AsDouble() == 60;   // two -5 ticks, then expiry
                ok = healed && modifier && dotted;
                detail = $"healed={healed}, modifier={modifier}, dotted={dotted} (hp={node.GetMeta("hp").AsDouble()})";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("effects: instant, while-active modifier, periodic with expiry", ok, detail);
        }

        // 95: an .ability edit hot-reloads live — the next activation uses the new cost
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["global.scene"] = "[nodes.N]\ntype = \"Node\"\nbehaviors = [\"hotgas.behavior.evt\"]\n",
                ["hotgas.behavior.evt"] =
                    "---\nattributes:\n  water: { base: 100, min: 0, max: 100 }\n" +
                    "abilities:\n - zap.ability\nregister:\n - on_attach\n - on_update\n---\n" +
                    "function on_attach() end\n" +
                    "function on_update(dt)\n" +
                    "  self.abilities:activate(\"zap\")\n" +
                    "  self:set_meta(\"w\", self.attributes.water)\n" +
                    "end\n",
                ["zap.ability"] = "cost = { attribute = \"water\", amount = 10.0 }\n",
            };
            try
            {
                var root = new Node();
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"]));
                var node = root.GetNodeOrNull("N")!;
                var probe = loader.ActiveNodes.First(x => x.Path == "hotgas.behavior.evt").Hooks["on_update"];
                loader.Call(probe, 0.0);
                var v1 = node.GetMeta("w").AsDouble() == 90;                  // -10
                src["zap.ability"] = "cost = { attribute = \"water\", amount = 50.0 }\n";
                loader.ReloadOnChange("zap.ability");
                loader.Call(probe, 0.0);
                var v2 = node.GetMeta("w").AsDouble() == 40;                  // -50, live
                ok = v1 && v2;
                detail = $"v1={v1}, v2={v2} (w={node.GetMeta("w").AsDouble()})";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("an .ability edit hot-reloads live (next activation uses new cost)", ok, detail);
        }

        // 96: GAS contract enforcement — unknown ability key, bad effect field,
        // attributes on a system, apply to an attribute-less target
        {
            bool badKey = GasLoadThrows(new()
            {
                ["b.behavior.evt"] = "---\nabilities:\n - x.ability\nregister:\n - on_attach\n---\nfunction on_attach() end\n",
                ["x.ability"] = "bogus = 1\n",
            });
            bool badField = GasLoadThrows(new()
            {
                ["b.behavior.evt"] =
                    "---\nattributes:\n  hp: { base: 1 }\nregister:\n - on_attach\n---\n" +
                    "function on_attach() self.abilities:apply(\"y.effect\") end\n",
                ["y.effect"] = "attribute = \"hp.bogus\"\nmagnitude = 1.0\n",
            });
            bool onSystem = Throws(() =>
            {
                var src = new Dictionary<string, string>
                {
                    ["s.evt"] = "---\nattributes:\n  hp: { base: 1 }\nregister:\n - on_start\n---\nfunction on_start() end\n",
                };
                new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { }).LoadSystem("s.evt");
            });
            bool noAttrs = GasLoadThrows(new()
            {
                ["b.behavior.evt"] =
                    "---\nregister:\n - on_attach\n---\n" +
                    "function on_attach() self.abilities:apply(\"z.effect\") end\n",
                ["z.effect"] = "attribute = \"hp\"\nmagnitude = 1.0\n",
            });
            Check("GAS contract enforced (keys/fields/system/attribute-less apply)",
                badKey && badField && onSystem && noAttrs,
                $"badKey={badKey}, badField={badField}, onSystem={onSystem}, noAttrs={noAttrs}");
        }

        // 96b: scene.list() enumerates switchable scenes (manifest excluded, sorted)
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["lister.evt"] =
                    "---\napis:\n - scene\nreturns:\n - r\n---\n" +
                    "local names = {}\nfor _, n in ipairs(scene.list()) do names[n] = true end\n" +
                    "return { r = (names[\"menu\"] == true) and (names[\"level1\"] == true) " +
                    "and (names[\"global\"] == nil) }\n",
            };
            try
            {
                var r = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { })
                    .Require("lister.evt").Read<LuaTable>()["r"];
                ok = r.Type == LuaValueType.Boolean && r.Read<bool>();
                detail = $"r={r}";
            }
            catch (Exception e) { detail = e.Message; }
            Check("scene.list() enumerates scenes (manifest excluded)", ok, detail);
        }

        // 96c: an engine NULL object marshals to Lua nil (a find_child miss must be
        // `== nil`, not a truthy proxy that explodes on first use)
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["global.scene"] = "[nodes.N]\ntype = \"Node\"\nscript = \"nullprobe.node.evt\"\n",
                ["nullprobe.node.evt"] =
                    "---\nregister:\n - on_attach\n---\n" +
                    "function on_attach()\n" +
                    "  self:set_meta(\"missing_is_nil\", self:find_child(\"NoSuchChild\", false, false) == nil)\n" +
                    "  self:set_meta(\"gnon_is_nil\", self:get_node_or_null(\"NoSuchPath\") == nil)\n" +
                    "end\n",
            };
            try
            {
                var root = new Node();
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"]));
                var n = root.GetNodeOrNull("N")!;
                ok = n.GetMeta("missing_is_nil").AsBool() && n.GetMeta("gnon_is_nil").AsBool();
                detail = $"find_child={n.GetMeta("missing_is_nil")}, get_node_or_null={n.GetMeta("gnon_is_nil")}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("an engine NULL object marshals to Lua nil", ok, detail);
        }

        // 97: the `dna` param type — hand-authored 16-hex-digit hash in, traits out
        {
            bool ok = false; string detail = "";
            var src = new Dictionary<string, string>
            {
                ["global.scene"] =
                    "[nodes.Grunt]\ntype = \"Node\"\nbehaviors = [{ script = \"dna.behavior.evt\", " +
                    "params = { dna = \"0xa13f00c2d4e5b677\" } }]\n",
                ["dna.behavior.evt"] =
                    "---\nparams:\n  dna: dna\nregister:\n - on_attach\n---\n" +
                    "function on_attach()\n" +
                    "  self:set_meta(\"t1\", params.dna:trait(1))\n" +
                    "  self:set_meta(\"t2\", params.dna:trait(2))\n" +
                    "  self:set_meta(\"t16\", params.dna:trait(16))\n" +
                    "  self:set_meta(\"hex\", params.dna:hex())\n" +
                    "end\n",
            };
            try
            {
                var root = new Node();
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"]));
                var g = root.GetNodeOrNull("Grunt")!;
                ok = g.GetMeta("t1").AsDouble() == 10        // 0xA, most-significant first
                    && g.GetMeta("t2").AsDouble() == 1
                    && g.GetMeta("t16").AsDouble() == 7
                    && g.GetMeta("hex").AsString() == "0xA13F00C2D4E5B677";   // normalized upper
                detail = $"t1={g.GetMeta("t1")}, t2={g.GetMeta("t2")}, t16={g.GetMeta("t16")}, hex={g.GetMeta("hex")}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("dna param: 16 nibble trait slots, MSB-first, normalized hex", ok, detail);
        }

        // 98: malformed dna values are rejected (wrong length / no 0x / non-hex / wrong type)
        {
            bool Reject(string dnaToml) => ParamLoadThrows(
                $"[nodes.G]\ntype = \"Node\"\nscript = \"m.node.evt\"\nparams = {{ dna = {dnaToml} }}\n",
                "---\nparams:\n  dna: dna\nregister:\n - on_attach\n---\nfunction on_attach() end\n");
            var tooShort = Reject("\"0xA13F\"");
            var noPrefix = Reject("\"A13F00C2D4E5B677A\"");
            var nonHex = Reject("\"0xZZZF00C2D4E5B677\"");
            var wrongType = Reject("42");
            var missing = ParamLoadThrows(
                "[nodes.G]\ntype = \"Node\"\nscript = \"m.node.evt\"\n",
                "---\nparams:\n  dna: dna\nregister:\n - on_attach\n---\nfunction on_attach() end\n");
            Check("malformed / missing dna params are rejected",
                tooShort && noPrefix && nonHex && wrongType && missing,
                $"short={tooShort}, noPrefix={noPrefix}, nonHex={nonHex}, wrongType={wrongType}, missing={missing}");
        }

        // ---- 0.11: native input (controls TOML -> actions), spawner, stack, store ----

        // Shared in-memory project for the controller tests: a manifest declaring a
        // controls file, one global node that subscribes, and two scenes (s1 carries
        // a [player] spawner + scenario, s2 is bare).
        Dictionary<string, string> ControllerSrc() => new()
        {
            ["global.scene"] = """
                controls = "c.toml"

                [nodes.Sub]
                type = "Node"
                script = "sub.node.evt"
                """,
            ["c.toml"] = """
                [Game]
                Key_space = "Jump"
                Key_escape = "Pause"
                Stick_left = "Move"
                Key_d = "Move+x"
                Key_a = "Move-x"

                [Menu]
                Key_escape = "Back"

                [Cutscene]

                [settings]
                deadzone = 0.1
                """,
            ["sub.node.evt"] = """
                ---
                apis:
                 - actions
                register:
                 - on_load
                ---
                function on_load()
                  actions.Game.Jump.subscribe{ on = "press", run = function() print("jump:press") end }
                  actions.Game.Jump.subscribe{ on = "release", run = function() print("jump:release") end }
                  actions.Game.Jump.subscribe{ on = "tap", after = 0.2, run = function() print("jump:tap") end }
                  actions.Game.Jump.subscribe{ on = "held", after = 0.2, run = function() print("jump:held") end }
                  actions.Menu.Back.subscribe{ on = "press", run = function() print("back:press") end }
                  actions.Game.Pause.subscribe{ on = "press", run = function() print("pause:press") end }
                end
                """,
            ["hero.scene"] = """
                [nodes.Hero]
                type = "Node3D"
                behaviors = ["hero.behavior.evt"]
                """,
            ["hero.behavior.evt"] = """
                ---
                apis:
                 - scene
                register:
                 - on_attach
                ---
                function on_attach()
                  local c = scene.context()
                  print(string.format("hero@%s entry=%s reason=%s",
                    tostring(scene.current()), tostring(c.entry), tostring(c.reason)))
                end
                """,
            ["spawn.evt"] = """
                ---
                returns:
                 - spawn
                ---
                local M = {}
                function M.spawn(ctx)
                  if ctx.entry == "east" then return { x = 8, y = 0, z = 0, facing = 1.5 } end
                  return { x = 1, y = 2, z = 3 }
                end
                return M
                """,
            ["s1.scene"] = """
                scenario = "Game"
                player = { node = "hero", spawn = "spawn.evt" }

                [nodes.World]
                type = "Node"
                """,
            ["s2.scene"] = """
                [nodes.Empty]
                type = "Node"
                """,
        };

        (Loader loader, Node root, List<string> sink) NewControllerLoader(Dictionary<string, string> src)
        {
            var sink = new List<string>();
            var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", sink.Add);
            var root = new Node();
            loader.SetGlobalRoot(root);
            new Persistence().ClearOverrides();     // isolate from prior runs' rebinds
            loader.LoadGlobalLayerFromFile();
            return (loader, root, sink);
        }

        // 99: controls TOML parses scenarios, tokens, vector routing, settings
        {
            bool ok = false; string detail = "";
            try
            {
                var map = ControlsMap.Parse(ControllerSrc()["c.toml"]);
                var jump = map.Find("Game", "Jump");
                var move = map.Find("Game", "Move");
                // 3 scenarios: Game, Menu, and the binding-LESS Cutscene (an empty
                // section is a real, switchable input-silence scenario).
                ok = map.Scenarios.Count == 3 && map.Scenarios.ContainsKey("Cutscene")
                     && jump is { IsVector: false, Bindings.Count: 1 }
                     && jump.Bindings[0].Source == BindingSource.Key
                     && jump.Bindings[0].Code == (long)Key.Space
                     && move is { IsVector: true, Bindings.Count: 3 }
                     && move.Bindings[1].Component == AxisComponent.PlusX
                     && System.Math.Abs(map.Deadzone - 0.1) < 1e-9;
                detail = $"scenarios={map.Scenarios.Count}, move bindings={move?.Bindings.Count}";
            }
            catch (Exception e) { detail = e.Message; }
            Check("controls TOML parses scenarios/tokens/vectors/settings", ok, detail);
        }

        // 100: controls contract violations fail at parse
        {
            var badToken = Throws(() => ControlsMap.Parse("[G]\nPedal_x = \"A\"\n"));
            var badKey = Throws(() => ControlsMap.Parse("[G]\nKey_floop = \"A\"\n"));
            var badValue = Throws(() => ControlsMap.Parse("[G]\nKey_a = 3\n"));
            var mixed = Throws(() => ControlsMap.Parse("[G]\nStick_left = \"M\"\nKey_a = \"M\"\n"));
            var badSetting = Throws(() => ControlsMap.Parse("[settings]\nturbo = 1\n"));
            Check("controls contract violations fail at parse",
                badToken && badKey && badValue && mixed && badSetting,
                $"token={badToken}, key={badKey}, value={badValue}, mixed={mixed}, setting={badSetting}");
        }

        // 101: raw input classes are not declarable capabilities
        {
            bool Blocked(string api) => Throws(() =>
            {
                var src = new Dictionary<string, string>
                {
                    ["probe.evt"] = $"---\napis:\n - {api}\n---\nreturn {{}}\n",
                };
                new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { }).Require("probe.evt");
            });
            var input = Blocked("Input");
            var evKey = Blocked("InputEventKey");
            var key = Blocked("Key");
            var joy = Blocked("JoyButton");
            var oldSvc = Blocked("input");   // the pre-0.11 service name resolves to nothing
            Check("raw input (Input/InputEvent*/Key/JoyButton/input) is undeclarable",
                input && evKey && key && joy && oldSvc,
                $"Input={input}, InputEventKey={evKey}, Key={key}, JoyButton={joy}, input={oldSvc}");
        }

        // 102: on_input is no longer a lifecycle hook (system + node)
        {
            var sys = Throws(() =>
            {
                var src = new Dictionary<string, string>
                {
                    ["s.evt"] = "---\nregister:\n - on_input\n---\nfunction on_input(e) end\n",
                };
                new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { }).LoadSystem("s.evt");
            });
            var node = GasLoadThrows(new Dictionary<string, string>
            {
                ["layer.scene"] = "[nodes.N]\ntype = \"Node\"\nscript = \"b.behavior.evt\"\n",
                ["b.behavior.evt"] = "---\nregister:\n - on_input\n---\nfunction on_input(e) end\n",
            });
            Check("on_input is no longer a registrable hook", sys && node, $"sys={sys}, node={node}");
        }

        // 103: actions dispatch — press, held(after), tap-vs-held, release, polling
        {
            bool ok = false; string detail = "";
            try
            {
                var (loader, root, sink) = NewControllerLoader(ControllerSrc());
                var pc = loader.Controller!;
                pc.SetScenario("Game");
                var down = new HashSet<long>();
                pc.ProbeKey = code => down.Contains(code);
                pc.ProbeButton = _ => false; pc.ProbeMouse = _ => false; pc.ProbeAxis = _ => 0;

                down.Add((long)Key.Space);
                loader.PollController(1.0 / 60);                    // press edge
                var pressed = sink.Contains("jump:press") && !sink.Contains("jump:held");
                var polled = pc.State("Game", "Jump").down;
                down.Remove((long)Key.Space);
                loader.PollController(1.0 / 60);                    // quick release -> tap
                var tapped = sink.Contains("jump:tap") && sink.Contains("jump:release");

                sink.Clear();
                down.Add((long)Key.Space);
                loader.PollController(1.0 / 60);                    // press again
                for (int i = 0; i < 20; i++) loader.PollController(1.0 / 60);   // hold ~0.33 s
                var held = sink.Contains("jump:held");
                var heldOnce = sink.FindAll(s => s == "jump:held").Count == 1;
                down.Remove((long)Key.Space);
                loader.PollController(1.0 / 60);                    // long release -> NO tap
                var noTap = !sink.Contains("jump:tap") && sink.Contains("jump:release");

                // vector polling: D key contributes +x
                down.Add((long)Key.D);
                loader.PollController(1.0 / 60);
                var (vd, _, vx, _) = pc.State("Game", "Move");
                var vector = vd && System.Math.Abs(vx - 1) < 1e-9;

                ok = pressed && polled && tapped && held && heldOnce && noTap && vector;
                detail = $"press={pressed}, poll={polled}, tap={tapped}, held={held}(x1={heldOnce}), " +
                         $"noTap={noTap}, vector={vector}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("actions dispatch: press/tap/held/release + polled state", ok, detail);
        }

        // 104: scenario gating — inactive scenarios are silent; a switch fires a
        // synthetic release and suppresses bindings held across it
        {
            bool ok = false; string detail = "";
            try
            {
                var (loader, root, sink) = NewControllerLoader(ControllerSrc());
                var pc = loader.Controller!;
                var down = new HashSet<long>();
                pc.ProbeKey = code => down.Contains(code);
                pc.ProbeButton = _ => false; pc.ProbeMouse = _ => false; pc.ProbeAxis = _ => 0;

                pc.SetScenario("Game");
                down.Add((long)Key.Escape);
                loader.PollController(1.0 / 60);
                var pauseFired = sink.Contains("pause:press") && !sink.Contains("back:press");

                pc.SetScenario("Menu");                       // Esc still physically held
                loader.PollController(1.0 / 60);
                loader.PollController(1.0 / 60);
                var suppressed = !sink.Contains("back:press");  // stale until released once
                down.Remove((long)Key.Escape);
                loader.PollController(1.0 / 60);
                down.Add((long)Key.Escape);
                loader.PollController(1.0 / 60);
                var backAfterRelease = sink.Contains("back:press");

                // Jump (Game) must not fire while Menu is active
                sink.Clear();
                down.Add((long)Key.Space);
                loader.PollController(1.0 / 60);
                var gated = !sink.Contains("jump:press");

                ok = pauseFired && suppressed && backAfterRelease && gated;
                detail = $"pause={pauseFired}, suppressed={suppressed}, " +
                         $"backAfterRelease={backAfterRelease}, gated={gated}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("scenario gating + stale-binding suppression on switch", ok, detail);
        }

        // 105: unknown scenario/action and a missing run fn are load-time errors
        {
            bool Fails(string body) => Throws(() =>
            {
                var src = ControllerSrc();
                src["bad.evt"] = "---\napis:\n - actions\nregister:\n - on_start\n---\n" +
                                 "function on_start() " + body + " end\n";
                var sink = new List<string>();
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", sink.Add);
                var root = new Node();
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayerFromFile();
                var sys = loader.LoadSystem("bad.evt");
                loader.Call(sys.OnStart);
                root.QueueFree();
            });
            var scenario = Fails("local x = actions.Nope.Jump");
            var action = Fails("local x = actions.Game.Nope");
            var run = Fails("actions.Game.Jump.subscribe{ on = \"press\" }");
            var onKind = Fails("actions.Game.Jump.subscribe{ on = \"wiggle\", run = function() end }");
            Check("actions: unknown scenario/action + bad subscribe specs error",
                scenario && action && run && onKind,
                $"scenario={scenario}, action={action}, run={run}, on={onKind}");
        }

        // 106: rebinds — a save-DB override remaps over the TOML, and reset restores
        {
            bool ok = false; string detail = "";
            try
            {
                var persistence = new Persistence();
                persistence.ClearOverrides();
                persistence.SetOverride("Game", "Key_space", "Pause");   // space now pauses
                var map = ControlsMap.Parse(ControllerSrc()["c.toml"], persistence.Overrides());
                var jumpUnbound = map.Find("Game", "Jump")!.Bindings.Count == 0;
                var pauseHasSpace = map.Find("Game", "Pause")!.Bindings
                    .Exists(b => b.Code == (long)Key.Space);
                persistence.ClearOverrides();
                var back = ControlsMap.Parse(ControllerSrc()["c.toml"], persistence.Overrides());
                var restored = back.Find("Game", "Jump")!.Bindings.Count == 1;
                ok = jumpUnbound && pauseHasSpace && restored;
                detail = $"unbound={jumpUnbound}, remapped={pauseHasSpace}, restored={restored}";
            }
            catch (Exception e) { detail = e.Message; }
            Check("control overrides remap over the TOML and reset cleanly", ok, detail);
        }

        // 107: the [player] spawner — fragment spawned, spawn(ctx) places it, the
        // controller possesses it, and scene.context() carries the caller's entry
        {
            bool ok = false; string detail = "";
            try
            {
                var (loader, root, sink) = NewControllerLoader(ControllerSrc());
                loader.GotoScene("s1");
                var hero = loader.Controller!.Possessed;
                var placed = hero is Node3D n3 && n3.Position.IsEqualApprox(new Vector3(1, 2, 3));
                var announced = sink.Exists(s => s.Contains("hero@s1 entry=nil reason=change"));
                var scenarioSet = loader.Controller.Scenario == "Game";

                var ctx = new LuaTable(); ctx["entry"] = "east";
                loader.GotoScene("s1", ctx);
                var hero2 = loader.Controller.Possessed;
                var placed2 = hero2 is Node3D m3 && m3.Position.IsEqualApprox(new Vector3(8, 0, 0))
                              && System.Math.Abs(m3.Rotation.Y - 1.5f) < 1e-3;
                var announced2 = sink.Exists(s => s.Contains("hero@s1 entry=east reason=change"));
                ok = placed && announced && scenarioSet && placed2 && announced2;
                detail = $"placed={placed}, announced={announced}, scenario={scenarioSet}, " +
                         $"entry placed={placed2}, entry ctx={announced2}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("[player] spawner: fragment + spawn(ctx) + possession + context", ok, detail);
        }

        // 108: spawner contract — multi-root fragments and a missing spawn export fail
        {
            var multiRoot = Throws(() =>
            {
                var src = ControllerSrc();
                src["hero.scene"] = "[nodes.A]\ntype = \"Node3D\"\n[nodes.B]\ntype = \"Node3D\"\n";
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                var root = new Node();
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayerFromFile();
                try { loader.GotoScene("s1"); } finally { root.QueueFree(); }
            });
            var noExport = Throws(() =>
            {
                var src = ControllerSrc();
                src["spawn.evt"] = "---\nreturns:\n - spawn\n---\nreturn { spawn = 5 }\n";
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                var root = new Node();
                loader.SetGlobalRoot(root);
                loader.LoadGlobalLayerFromFile();
                try { loader.GotoScene("s1"); } finally { root.QueueFree(); }
            });
            Check("[player] contract: single root + spawn(ctx) export enforced",
                multiRoot && noExport, $"multiRoot={multiRoot}, noExport={noExport}");
        }

        // 109: the scene stack — push freezes the layer below (no hooks, on_pause),
        // pop thaws it (on_resume) and restores scenario + possession
        {
            bool ok = false; string detail = "";
            try
            {
                var src = ControllerSrc();
                src["under.behavior.evt"] = """
                    ---
                    register:
                     - on_update
                     - on_pause
                     - on_resume
                    ---
                    function on_update(dt) print("under:update") end
                    function on_pause() print("under:pause") end
                    function on_resume() print("under:resume") end
                    """;
                src["s1.scene"] = """
                    scenario = "Game"
                    player = { node = "hero", spawn = "spawn.evt" }

                    [nodes.World]
                    type = "Node"
                    script = "under.behavior.evt"
                    """;
                src["pause.scene"] = """
                    scenario = "Menu"

                    [nodes.PauseUi]
                    type = "Node"
                    """;
                var (loader, root, sink) = NewControllerLoader(src);
                loader.GotoScene("s1");
                var hero = loader.Controller!.Possessed;

                loader.PushScene("pause");
                var paused = sink.Contains("under:pause");
                var stackTop = loader.ActiveScene == "pause";
                var menuScenario = loader.Controller.Scenario == "Menu";
                sink.Clear();
                foreach (var sys in loader.ActiveNodes)                  // frozen: no under:update
                    if (sys.Hooks.TryGetValue("on_update", out var fn)) loader.Call(fn, 0.016);
                var frozen = !sink.Contains("under:update");

                loader.PopScene();
                var resumed = sink.Contains("under:resume");
                var backTop = loader.ActiveScene == "s1";
                var scenarioBack = loader.Controller.Scenario == "Game";
                var possessedBack = ReferenceEquals(loader.Controller.Possessed, hero);

                var popSole = Throws(() => loader.PopScene());

                loader.PushScene("pause");
                loader.GotoScene("s2");                                   // change clears the stack
                var cleared = loader.ActiveScene == "s2" && Throws(() => loader.PopScene());

                ok = paused && stackTop && menuScenario && frozen && resumed && backTop
                     && scenarioBack && possessedBack && popSole && cleared;
                detail = $"paused={paused}, top={stackTop}, menu={menuScenario}, frozen={frozen}, " +
                         $"resumed={resumed}, backTop={backTop}, scenario={scenarioBack}, " +
                         $"possessed={possessedBack}, popSole={popSole}, cleared={cleared}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("scene stack: push freezes, pop restores scenario/possession", ok, detail);
        }

        // 110: store — set/get/default/keys(prefix)/subscribe(exact + prefix + cancel)
        {
            bool ok = false; string detail = "";
            try
            {
                var src = new Dictionary<string, string>
                {
                    ["st.evt"] = """
                        ---
                        apis:
                         - store
                        returns:
                         - run
                        ---
                        local M = {}
                        function M.run()
                          local log = {}
                          store.set("player.hp", 5)
                          store.set("player.stamina", 3)
                          store.set("world.day", 2)
                          local h1 = store.subscribe("player.hp", function(k, new, old)
                            log[#log + 1] = string.format("exact %s %s->%s", k, tostring(old), tostring(new))
                          end)
                          local h2 = store.subscribe("player.", function(k) log[#log + 1] = "prefix " .. k end)
                          store.set("player.hp", 4)
                          h1.cancel()
                          store.set("player.hp", 3)
                          local keys = store.keys("player.")
                          return string.format("hp=%d|day=%d|miss=%d|keys=%d|%s",
                            store.get("player.hp"), store.get("world.day"), store.get("nope", -1),
                            #keys, table.concat(log, ";"))
                        end
                        return M
                        """,
                };
                var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
                var run = loader.Require("st.evt").Read<LuaTable>()["run"];
                var result = loader.CallRet(run).Read<string>();
                ok = result == "hp=3|day=2|miss=-1|keys=2|exact player.hp 5->4;prefix player.hp;prefix player.hp";
                detail = result;
            }
            catch (Exception e) { detail = e.Message; }
            Check("store: set/get/keys + exact/prefix subscribe + cancel", ok, detail);
        }

        // 111: a scene layer's subscriptions (actions + store) die with the layer
        {
            bool ok = false; string detail = "";
            try
            {
                var src = ControllerSrc();
                src["watcher.behavior.evt"] = """
                    ---
                    apis:
                     - store
                     - actions
                    register:
                     - on_load
                    ---
                    function on_load()
                      store.subscribe("sig", function(k, v) print("scene-sub sees " .. tostring(v)) end)
                      actions.Game.Jump.subscribe{ on = "press", run = function() print("scene jump") end }
                    end
                    """;
                src["s2.scene"] = """
                    [nodes.Watcher]
                    type = "Node"
                    script = "watcher.behavior.evt"
                    """;
                src["setter.evt"] = """
                    ---
                    apis:
                     - store
                    returns:
                     - set
                    ---
                    local M = {}
                    function M.set(v) store.set("sig", v) end
                    return M
                    """;
                var (loader, root, sink) = NewControllerLoader(src);
                var set = loader.Require("setter.evt").Read<LuaTable>()["set"];
                loader.GotoScene("s2");
                loader.Call(set, 1);
                var live = sink.Contains("scene-sub sees 1");
                loader.GotoScene("s1");                      // s2's layer torn down
                sink.Clear();
                loader.Call(set, 2);
                var storeDead = !sink.Exists(s => s.Contains("scene-sub"));
                var pc = loader.Controller!;
                pc.ProbeKey = _ => true; pc.ProbeButton = _ => false;
                pc.ProbeMouse = _ => false; pc.ProbeAxis = _ => 0;
                loader.PollController(1.0 / 60);
                var actionDead = !sink.Contains("scene jump");
                ok = live && storeDead && actionDead;
                detail = $"live={live}, storeDead={storeDead}, actionDead={actionDead}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("scene-layer subscriptions die when the layer is torn down", ok, detail);
        }

        // 112: a scene-file hot reload rebuilds the top layer in place, preserving
        // the player's live transform (no yank back to the spawn point)
        {
            bool ok = false; string detail = "";
            try
            {
                var (loader, root, sink) = NewControllerLoader(ControllerSrc());
                loader.GotoScene("s1");
                var hero = (Node3D)loader.Controller!.Possessed!;
                hero.Position = new Vector3(9, 9, 9);
                loader.ReloadOnChange("s1.scene");
                var fresh = loader.Controller.Possessed;
                var newNode = !ReferenceEquals(fresh, hero);
                var kept = fresh is Node3D n3 && n3.Position.IsEqualApprox(new Vector3(9, 9, 9));
                ok = newNode && kept;
                detail = $"newNode={newNode}, kept={kept}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("active-scene rebuild preserves the player transform", ok, detail);
        }

        // 113: scene-level header fields round-trip through SceneWriter
        {
            bool ok = false; string detail = "";
            try
            {
                var text = """
                    start_scene = "menu"
                    controls = "c.toml"
                    scenario = "Game"
                    player = { node = "hero", spawn = "spawn.evt" }

                    [nodes.N]
                    type = "Node"
                    """;
                var emitted = SceneWriter.Emit(SceneFile.Parse(text));
                var back = SceneFile.Parse(emitted);
                ok = back.StartScene == "menu" && back.Controls == "c.toml"
                     && back.Scenario == "Game"
                     && back.Player is { Node: "hero", Spawn: "spawn.evt" };
                detail = ok ? "round-tripped" : emitted.Replace('\n', ';');
            }
            catch (Exception e) { detail = e.Message; }
            Check("controls/scenario/[player] survive a SceneWriter round-trip", ok, detail);
        }

        log($"[tests] {passed} passed, {failed} failed");
        return failed;
    }

    // Build a one-node layer attaching b.behavior.evt (plus the given extra files) and
    // report whether the load throws — the GAS analogue of ParamLoadThrows.
    private static bool GasLoadThrows(Dictionary<string, string> src)
    {
        src["global.scene"] = "[nodes.N]\ntype = \"Node\"\nbehaviors = [\"b.behavior.evt\"]\n";
        var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
        var root = new Node();
        loader.SetGlobalRoot(root);
        var threw = Throws(() => loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"])));
        root.QueueFree();
        return threw;
    }

    // Fire the test machine's "toggle" event through the fsm surface (via a scratch
    // behavior hook so the call goes through Lua like real gameplay code would).
    private static void FireToggle(Loader loader, Dictionary<string, string> src, Node node)
    {
        src["__fire.evt"] = "---\nreturns:\n - go\n---\nreturn { go = function(n) n.fsm.sw:fire(\"toggle\") end }\n";
        var go = loader.Require("__fire.evt").Read<LuaTable>()["go"];
        loader.Call(go, loader.Wrap(node));
    }

    // Attach one machine (given source) to a fresh node and report whether it throws.
    private static bool MachineLoadThrows(string machineSource)
    {
        var src = new Dictionary<string, string>
        {
            ["global.scene"] = "[nodes.N]\ntype = \"Node\"\nmachines = [\"t.statemachine.evt\"]\n",
            ["t.statemachine.evt"] = machineSource,
        };
        var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
        var root = new Node();
        loader.SetGlobalRoot(root);
        var threw = Throws(() => loader.LoadGlobalLayer(SceneFile.Parse(src["global.scene"])));
        root.QueueFree();
        return threw;
    }

    // A tiny host api used by the RegisterApi cases: one instance method, one static.
    private sealed class HostMath
    {
        public double Add(double a, double b) => a + b;
        public static string Tag() => "host";
    }

    // Build a one-node global layer from (sceneToml, nodeScriptSource) and report whether
    // attaching the node script throws — i.e. the params contract is enforced at attach.
    private static bool ParamLoadThrows(string sceneToml, string nodeScript)
    {
        var src = new Dictionary<string, string> { ["m.node.evt"] = nodeScript };
        var loader = new Loader(n => src.TryGetValue(n, out var s) ? s : "", _ => { });
        var root = new Node();
        loader.SetGlobalRoot(root);
        var threw = Throws(() => loader.LoadGlobalLayer(SceneFile.Parse(sceneToml)));
        root.QueueFree();
        return threw;
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
        if (!AttachEquals(a.Behaviors, b.Behaviors) || !AttachEquals(a.Machines, b.Machines)) return false;
        if (a.Connections.Count != b.Connections.Count) return false;
        for (int i = 0; i < a.Connections.Count; i++)
            if (a.Connections[i].Signal != b.Connections[i].Signal
                || a.Connections[i].To != b.Connections[i].To
                || a.Connections[i].Method != b.Connections[i].Method) return false;
        return MapEquals(a.Meta, b.Meta) && MapEquals(a.Props, b.Props)
            && MapEquals(a.Params, b.Params) && NodesEqual(a.Children, b.Children);
    }

    private static bool SetEquals(List<string> a, List<string> b)
        => a.Count == b.Count && a.All(b.Contains);

    private static bool AttachEquals(List<AttachSpec> a, List<AttachSpec> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Script != b[i].Script || !MapEquals(a[i].Params, b[i].Params)) return false;
        return true;
    }

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
