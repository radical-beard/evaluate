using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Lua;

namespace Evaluate;

// The NATIVE player controller — the single owner of device input. The loader adds
// one to the global layer implicitly when the manifest declares `controls = "…"`
// (and a bare, mapless one when it doesn't, so `controller` calls never nil out).
// Scripts never see raw input: they subscribe to ACTIONS (`actions` api) that this
// node resolves from the controls TOML + the save-DB overrides, or poll an action's
// live `down/value/vector`. Device state is read once per PHYSICS tick — the
// runtime calls Poll() right before on_physics_update dispatch, so every script
// sees this tick's state and events fire before gameplay hooks run.
//
// Scenario model: exactly one scenario is active at a time (plus the reserved
// "Always" overlay, for e.g. debug toggles). Switching scenarios fires synthetic
// releases for the outgoing scenario's held actions, and bindings physically held
// through the switch stay SUPPRESSED in the incoming scenario until first released
// — so Esc closing a menu cannot instantly re-trigger the pause it was mapped to.
public sealed partial class PlayerController : Node
{
    public const string NodeName = "PlayerController";
    private const int Device = 0;               // pad 0; keyboard/mouse are global

    private ControlsMap _map = new();
    private string _scenario = "";              // "" = none (only Always fires)
    private Node? _possessed;
    private LuaValue _textCapture = LuaValue.Nil;
    private string _textOwner = "";
    private object? _textLifetime;

    // Call a Lua closure on the main thread — injected by the loader.
    internal Action<LuaValue, LuaValue[]> CallLua = (_, _) => { };
    // Call a subscriber's closure UNDER ITS REGISTRATION CONTEXT (owner script +
    // scene-layer lifetime) — injected by the loader. Anything the callback itself
    // registers (another subscription, a text capture) then inherits that context,
    // so it is cleaned up with the right script/layer instead of leaking globally
    // and firing against freed nodes.
    internal Action<LuaValue, LuaValue[], string, object?> DispatchLua =
        (_, _, _, _) => { };
    internal Action<string> Log = _ => { };

    // Device probes — the ONLY reads of raw input. Swappable so the enforcement
    // suite can drive the whole action pipeline deterministically headless.
    internal Func<long, bool> ProbeKey = code => Input.IsPhysicalKeyPressed((Key)code);
    internal Func<long, bool> ProbeButton = code => Input.IsJoyButtonPressed(Device, (JoyButton)code);
    internal Func<long, bool> ProbeMouse = code => Input.IsMouseButtonPressed((MouseButton)code);
    internal Func<long, double> ProbeAxis = code => Input.GetJoyAxis(Device, (JoyAxis)code);

    // (scenario, action) -> live state. States exist for every mapped action of
    // every scenario (cheap), but only active scenarios are polled/dispatched.
    private readonly Dictionary<(string, string), ActionState> _states = new();
    private long _subIds;

    private sealed class ActionState
    {
        public ActionDef Def = null!;
        public bool Down, PrevDown, Stale;
        public double HeldFor;                   // seconds since the press edge
        public double Value, X, Y;
        public readonly List<Subscription> Subs = new();
    }

    private sealed class Subscription
    {
        public long Id;
        public string On = "";                   // press | release | tap | held
        public double After;                     // seconds (tap: max hold; held: min hold)
        public LuaValue Fn;
        public string Owner = "";                // script path, for hot-reload cleanup
        public object? Lifetime;                 // scene layer token; null = global
        public bool HeldFired;
    }

    public string Scenario => _scenario;
    public Node? Possessed => _possessed;
    public ControlsMap Map => _map;

    public void Possess(Node? node) => _possessed = node;

    public void SetScenario(string name)
    {
        if (name.Length > 0 && name != ControlsMap.AlwaysScenario
            && !_map.Scenarios.ContainsKey(name) && _map.Scenarios.Count > 0)
            throw new EvaluateException(
                $"controller.scenario('{name}'): the controls file declares no such scenario " +
                $"(declared: {string.Join(", ", _map.Scenarios.Keys.Where(k => k != ControlsMap.AlwaysScenario))})");
        if (name == _scenario) return;

        // Outgoing: anything held gets a synthetic release (subscribers unwind cleanly).
        foreach (var ((sc, _), st) in _states)
        {
            if (sc != _scenario || sc == ControlsMap.AlwaysScenario || !st.Down) continue;
            st.Down = false; st.PrevDown = false; st.Value = 0; st.X = 0; st.Y = 0;
            FireSubs(st, "release");
        }
        _scenario = name;
        // Incoming: a binding physically held across the switch is stale until released.
        foreach (var ((sc, _), st) in _states)
        {
            if (sc != name) continue;
            st.Down = false; st.PrevDown = false; st.HeldFor = 0;
            st.Stale = RawDown(st.Def);
        }
    }

    // Swap in a (re)parsed controls map — controls.toml hot reload / a rebind. Live
    // subscriptions carry over by (scenario, action) name; ones whose action no
    // longer exists are kept but inert (logged), so a mid-session edit never throws.
    public void SetMap(ControlsMap map)
    {
        _map = map;
        var old = _states.ToList();
        _states.Clear();
        EnsureStates();
        foreach (var (key, st) in old)
        {
            if (st.Subs.Count == 0) continue;
            if (_states.TryGetValue(key, out var fresh))
                fresh.Subs.AddRange(st.Subs);
            else
            {
                Log($"controls reload: '{key.Item1}.{key.Item2}' no longer exists; " +
                    $"{st.Subs.Count} subscription(s) kept but inert");
                var orphan = new ActionState { Def = new ActionDef { Name = key.Item2 } };
                orphan.Subs.AddRange(st.Subs);
                _states[key] = orphan;
            }
        }
        if (_scenario.Length > 0 && !_map.Scenarios.ContainsKey(_scenario) && _map.Scenarios.Count > 0)
            Log($"controls reload: active scenario '{_scenario}' no longer exists");
    }

    private void EnsureStates()
    {
        foreach (var (scenario, actions) in _map.Scenarios)
            foreach (var def in actions.Values)
                _states[(scenario, def.Name)] = new ActionState { Def = def };
    }

    public bool HasAction(string scenario, string action) =>
        _map.Find(scenario, action) is not null;

    public IEnumerable<string> ScenarioNames => _map.Scenarios.Keys;
    public IEnumerable<string> ActionNames(string scenario) =>
        _map.Scenarios.TryGetValue(scenario, out var a) ? a.Keys : Enumerable.Empty<string>();

    // Live read for the polled surface (neutral when the scenario is inactive —
    // inactive scenarios are not polled, and their last state was cleared).
    public (bool down, double value, double x, double y) State(string scenario, string action) =>
        _states.TryGetValue((scenario, action), out var st)
            ? (st.Down, st.Value, st.X, st.Y)
            : (false, 0, 0, 0);

    public long Subscribe(string scenario, string action, string on, double after,
        LuaValue fn, string owner, object? lifetime)
    {
        if (!_states.TryGetValue((scenario, action), out var st))
            throw new EvaluateException(
                $"actions.{scenario}.{action}.subscribe: no such action " +
                (_map.Scenarios.TryGetValue(scenario, out var acts)
                    ? $"(scenario '{scenario}' declares: {string.Join(", ", acts.Keys)})"
                    : $"(no scenario '{scenario}'; declared: {string.Join(", ", _map.Scenarios.Keys)})"));
        if (on is not ("press" or "release" or "tap" or "held"))
            throw new EvaluateException(
                $"actions.{scenario}.{action}.subscribe: on = '{on}' " +
                "(valid: \"press\", \"release\", \"tap\", \"held\")");
        var sub = new Subscription
        {
            Id = ++_subIds, On = on, After = after, Fn = fn, Owner = owner, Lifetime = lifetime,
        };
        st.Subs.Add(sub);
        return sub.Id;
    }

    public void Unsubscribe(long id)
    {
        foreach (var st in _states.Values) st.Subs.RemoveAll(s => s.Id == id);
    }

    public void RemoveOwnerSubscriptions(string owner)
    {
        foreach (var st in _states.Values) st.Subs.RemoveAll(s => s.Owner == owner);
        if (_textOwner == owner) { _textCapture = LuaValue.Nil; _textOwner = ""; _textLifetime = null; }
    }

    public void RemoveLifetimeSubscriptions(object lifetime)
    {
        foreach (var st in _states.Values) st.Subs.RemoveAll(s => ReferenceEquals(s.Lifetime, lifetime));
        if (ReferenceEquals(_textLifetime, lifetime))
        { _textCapture = LuaValue.Nil; _textOwner = ""; _textLifetime = null; }
    }

    // ---- per-physics-tick poll + dispatch (driven by the runtime) --------------

    public void Poll(double dt)
    {
        PollScenario(ControlsMap.AlwaysScenario, dt);
        if (_scenario.Length > 0 && _scenario != ControlsMap.AlwaysScenario)
            PollScenario(_scenario, dt);
    }

    private void PollScenario(string scenario, double dt)
    {
        if (!_map.Scenarios.TryGetValue(scenario, out var actions)) return;
        foreach (var def in actions.Values)
        {
            var st = _states[(scenario, def.Name)];
            var (down, value, x, y) = Read(def);

            if (st.Stale)
            {
                if (!down) st.Stale = false;
                down = false; value = 0; x = 0; y = 0;
            }

            st.PrevDown = st.Down;
            st.Down = down; st.Value = value; st.X = x; st.Y = y;

            if (st.Down && !st.PrevDown)
            {
                st.HeldFor = 0;
                foreach (var s in st.Subs) s.HeldFired = false;
                FireSubs(st, "press");
            }
            else if (st.Down)
            {
                st.HeldFor += dt;
                foreach (var s in st.Subs.ToArray())
                    if (s.On == "held" && !s.HeldFired && st.HeldFor >= s.After)
                    { s.HeldFired = true; DispatchLua(s.Fn, Array.Empty<LuaValue>(), s.Owner, s.Lifetime); }
            }
            else if (!st.Down && st.PrevDown)
            {
                foreach (var s in st.Subs.ToArray())
                    if (s.On == "tap" && st.HeldFor < s.After)
                        DispatchLua(s.Fn, Array.Empty<LuaValue>(), s.Owner, s.Lifetime);
                FireSubs(st, "release");
                st.HeldFor = 0;
            }
        }
    }

    private void FireSubs(ActionState st, string on)
    {
        foreach (var s in st.Subs.ToArray())
            if (s.On == on) DispatchLua(s.Fn, Array.Empty<LuaValue>(), s.Owner, s.Lifetime);
    }

    // ---- raw device reads -------------------------------------------------------

    private (bool down, double value, double x, double y) Read(ActionDef def)
    {
        if (def.IsVector)
        {
            double x = 0, y = 0;
            foreach (var b in def.Bindings)
            {
                if (b.Source == BindingSource.Stick)
                {
                    var sx = ProbeAxis((long)(b.Code == 0 ? JoyAxis.LeftX : JoyAxis.RightX));
                    var sy = -ProbeAxis((long)(b.Code == 0 ? JoyAxis.LeftY : JoyAxis.RightY));
                    if (Math.Sqrt(sx * sx + sy * sy) >= _map.Deadzone)
                    {
                        if (Math.Abs(sx) > Math.Abs(x)) x = sx;
                        if (Math.Abs(sy) > Math.Abs(y)) y = sy;
                    }
                    continue;
                }
                var v = b.Source == BindingSource.JoyAxis
                    ? Math.Abs(ProbeAxis(b.Code))
                    : (RawDigital(b) ? 1.0 : 0.0);
                switch (b.Component)
                {
                    case AxisComponent.PlusX: x += v; break;
                    case AxisComponent.MinusX: x -= v; break;
                    case AxisComponent.PlusY: y += v; break;
                    case AxisComponent.MinusY: y -= v; break;
                }
            }
            x = Math.Clamp(x, -1, 1); y = Math.Clamp(y, -1, 1);
            var mag = Math.Sqrt(x * x + y * y);
            if (mag < _map.Deadzone) { x = 0; y = 0; mag = 0; }
            return (mag > 0, Math.Min(mag, 1), x, y);
        }

        double value2 = 0; bool down = false;
        foreach (var b in def.Bindings)
        {
            if (b.Source == BindingSource.JoyAxis)
            {
                var v = ProbeAxis(b.Code);
                if (Math.Abs(v) > Math.Abs(value2)) value2 = v;
                down |= Math.Abs(v) >= _map.AxisThreshold;
            }
            else if (RawDigital(b)) { down = true; value2 = Math.Max(value2, 1); }
        }
        return (down, Math.Abs(value2), 0, 0);
    }

    private bool RawDown(ActionDef def) => Read(def).down;

    private bool RawDigital(Binding b) => b.Source switch
    {
        BindingSource.JoyButton => ProbeButton(b.Code),
        BindingSource.Key => !CaptureSuppresses((Key)b.Code) && ProbeKey(b.Code),
        BindingSource.MouseButton => ProbeMouse(b.Code),
        _ => false,
    };

    // ---- text capture (menus typing into a search box, etc.) --------------------

    public void CaptureText(LuaValue fn, string owner, object? lifetime)
    {
        _textCapture = fn;
        _textOwner = fn.Type == LuaValueType.Nil ? "" : owner;
        _textLifetime = fn.Type == LuaValueType.Nil ? null : lifetime;
    }

    public bool Capturing => _textCapture.Type == LuaValueType.Function;

    // While capture is active, keys that TYPE (printable unicode) stop firing
    // mapped actions — they are text now. Arrows/enter/escape stay actions.
    private bool CaptureSuppresses(Key key) => Capturing && IsPrintable(key);

    private static bool IsPrintable(Key key) =>
        key is >= Key.A and <= Key.Z
            or >= Key.Key0 and <= Key.Key9
            or Key.Space or Key.Comma or Key.Period or Key.Slash or Key.Semicolon
            or Key.Apostrophe or Key.Bracketleft or Key.Bracketright or Key.Backslash
            or Key.Minus or Key.Equal or Key.Quoteleft;

    public override void _Input(InputEvent @event)
    {
        if (!Capturing || @event is not InputEventKey k || !k.Pressed || k.Echo) return;
        if (k.Keycode == Key.Backspace)
            DispatchLua(_textCapture, new LuaValue[] { "backspace" }, _textOwner, _textLifetime);
        else if (k.Unicode is >= 32 and <= 126)
            DispatchLua(_textCapture, new LuaValue[] { "char", char.ConvertFromUtf32((int)k.Unicode) },
                _textOwner, _textLifetime);
    }

    // ---- output -----------------------------------------------------------------

    public void Rumble(double strength, double seconds)
    {
        var s = (float)Math.Clamp(strength, 0, 1);
        Input.StartJoyVibration(Device, s, s, (float)Math.Max(0, seconds));
    }
}
