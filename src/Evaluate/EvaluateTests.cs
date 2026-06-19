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
                loader.LoadGlobalLayerFromFile();     // global.scene.toml -> Probe + self_node.node.evt
                var probe = root.GetChildCount() > 0 ? root.GetChild(0) : null;
                ok = probe is not null && probe.HasMeta("ready") && probe.GetMeta("ready").AsBool();
                detail = probe is null ? "no node instantiated" : $"meta ready={probe.HasMeta("ready")}";
                root.QueueFree();
            }
            catch (Exception e) { detail = e.Message; }
            Check("a node script runs with self bound to its node", ok, detail);
        }

        // 12: SceneFile parses [[node]] blocks (name/type/parent/script/props)
        {
            var spec = SceneFile.Parse(
                "start_scene = \"x\"\n[[node]]\nname = \"A\"\ntype = \"Node3D\"\n" +
                "[[node]]\nname = \"B\"\ntype = \"Node2D\"\nparent = \"A\"\n" +
                "script = \"b.node.evt\"\nposition = [1.0, 2.0]\n");
            var ok = spec.StartScene == "x" && spec.Nodes.Count == 2
                && spec.Nodes[0].Name == "A" && spec.Nodes[0].Type == "Node3D"
                && spec.Nodes[1].Parent == "A" && spec.Nodes[1].Script == "b.node.evt"
                && spec.Nodes[1].Props.ContainsKey("position");
            Check("scene file parses [[node]] blocks with parent/script/props", ok,
                $"start={spec.StartScene}, nodes={spec.Nodes.Count}");
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
