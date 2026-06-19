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
