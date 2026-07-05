using System;
using System.Threading.Tasks;
using Lua;
using Lua.Standard;
using Tomlyn;

// Each section exercises a path the trim/AOT analyzers flagged (or a VM the
// mobile story depends on) under REAL NativeAOT. Exit 0 = all green.
int failures = 0;
void Check(string name, Func<bool> test)
{
    try
    {
        bool ok = test();
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        if (!ok) failures++;
    }
    catch (Exception e)
    {
        Console.WriteLine($"FAIL  {name}: {e.GetType().Name}: {e.Message}");
        failures++;
    }
}

// 1. The production frontmatter parser (YamlDotNet untyped pipeline).
Check("frontmatter: signature parse", () =>
{
    var fm = Evaluate.Frontmatter.Parse("""
        ---
        config:
         - game.toml
        apis:
         - sim
         - Node3D
        register:
         - on_update
        params:
          speed: 2.5
          name: "colonist"
        assets:
          icon: "art/icon.png"
        ---
        local x = 1
        """);
    return fm.Apis.Count == 2 && fm.Register.Contains("on_update") && fm.Body.Contains("local x = 1");
});

// 2. Lua-CSharp: closures, metatable OOP, string lib — the sandbox's diet.
Check("lua: closures + metatables", () =>
{
    var state = LuaState.Create();
    state.OpenStandardLibraries();   // the sandbox Loader injects these builtins itself
    var results = Task.Run(async () => await state.DoStringAsync("""
        local Animal = {}
        Animal.__index = Animal
        function Animal.new(sound) return setmetatable({ sound = sound }, Animal) end
        function Animal:speak() return "says " .. self.sound end
        local counter = 0
        local function tick() counter = counter + 1 return counter end
        tick(); tick()
        return Animal.new("moo"):speak(), tick()
        """)).GetAwaiter().GetResult();
    return results[0].Read<string>() == "says moo" && results[1].Read<double>() == 3;
});

// 3. Tomlyn document model (scene files).
Check("toml: scene-shaped document", () =>
{
    var doc = Toml.ToModel("""
        description = "smoke"
        [nodes.Player]
        type = "Node3D"
        position = [1.0, 2.0, 3.0]
        [nodes.Player.Camera]
        type = "Camera3D"
        """);
    return doc.ContainsKey("nodes");
});

Console.WriteLine(failures == 0 ? "AOT SMOKE: ALL GREEN" : $"AOT SMOKE: {failures} FAILURE(S)");
return failures;
