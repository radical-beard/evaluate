using System.Collections.Generic;
using Godot;
using Lua;

namespace Evaluate;

// The parsed definition of one `*.statemachine.evt`: the frontmatter's state list
// plus the body's returned, ORDERED transition list. Declarative by construction —
// every state and transition is visible in the file, no hidden runtime states.
public sealed class MachineDef
{
    public string Path = "";
    public string Name = "";
    public List<string> States = new();
    public string Initial = "";
    public List<TransitionDef> Transitions = new();
}

// One transition. Exactly one trigger is set:
//   when  - a guard fn(self) polled every physics tick, in declaration order
//   on    - an event name fired explicitly (self.fsm.<name>:fire("event"))
//   after - seconds in the source state (auto/timed)
// `Run` (`run = fn(self, from, to)`) is an optional action that executes when the
// transition is taken (`do` is a Lua keyword, so the key is `run`). `From` may be
// "*" (any state).
public sealed class TransitionDef
{
    public string From = "";
    public string To = "";
    public LuaValue When = LuaValue.Nil;
    public string? On;
    public double After = -1;
    public LuaValue Run = LuaValue.Nil;
}

// One live machine INSTANCE: a def attached to one node. A node may carry many
// machines (keyed by name); one machine file may drive many nodes.
public sealed class MachineInstance
{
    public MachineDef Def = null!;
    public GodotObject Node = null!;
    public Node? Container;                       // the layer it was attached under (for reload)
    public LuaValue SelfProxy = LuaValue.Nil;     // wrapped node, passed to guards/actions
    public IReadOnlyDictionary<string, object> Params = LoadedNode.EmptyParams;
    public string Current = "";
    public double TimeInState;
    public bool Firing;                            // reentrancy guard: fires inside listeners defer
    public readonly List<string> DeferredEvents = new();
    // state -> listeners appended via the `self.fsm.<machine>.<state> = fn` sugar
    // (enter) and `:on_exit(state, fn)`. Each remembers the script that subscribed,
    // so a hot-reloaded script's stale closures are dropped before it re-subscribes.
    public readonly Dictionary<string, List<(string Owner, LuaValue Fn)>> EnterListeners = new();
    public readonly Dictionary<string, List<(string Owner, LuaValue Fn)>> ExitListeners = new();
}
