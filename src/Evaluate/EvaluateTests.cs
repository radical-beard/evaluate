using System;
using System.Collections.Generic;
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

        // 4: a lifecycle hook may be registered only once project-wide
        Check("a hook can only be registered once per project", DoubleRegisterRejected());

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
