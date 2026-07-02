using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Evaluate;

// The parsed controls file: scenario -> action -> physical bindings. This is pure
// data — the PlayerController owns the live input state built from it. The file is
// TOML; each section is a SCENARIO (the reserved "Always" scenario is active in
// every scenario; "settings" is tuning, not bindings). Keys are binding tokens,
// values are action names:
//
//   [Menu]
//   Button_a   = "Select"
//   Key_enter  = "Select"
//   [Gameplay]
//   Stick_left = "Move"          # paired axes -> a vector action
//   Key_w      = "Move+y"        # a digital key feeding one vector axis
//   Axis_trigger_right = "Block" # an analog axis (value 0..1; down past threshold)
//   [settings]
//   deadzone = 0.05
//   axis_threshold = 0.3
//
// Token grammar (resolved against the Godot enums at parse time, snake_case of the
// enum name; unknown tokens are load errors — the signature-is-the-contract stance):
//   Button_<JoyButton>   e.g. Button_a, Button_dpad_up, Button_left_stick
//                        (aliases: l3/r3 = left/right stick, lb/rb = shoulders)
//   Key_<Key>            e.g. Key_space, Key_escape, Key_w, Key_kp_enter
//   Axis_<JoyAxis>       e.g. Axis_left_x, Axis_trigger_right
//   Stick_left|right     the paired stick axes as one vector binding
//   Mouse_<MouseButton>  e.g. Mouse_left
public sealed class ControlsMap
{
    public const string AlwaysScenario = "Always";
    public const string SettingsSection = "settings";

    // scenario -> action name -> definition. Ordered dictionaries keep the file's
    // declaration order (stable docs + deterministic dispatch).
    public readonly Dictionary<string, Dictionary<string, ActionDef>> Scenarios = new();
    public double Deadzone = 0.05;        // stick vector magnitude below this reads 0
    public double AxisThreshold = 0.3;    // analog axis counts as "down" past this

    public ActionDef? Find(string scenario, string action) =>
        Scenarios.TryGetValue(scenario, out var acts) && acts.TryGetValue(action, out var a) ? a : null;

    // ---- parsing ---------------------------------------------------------------

    // Parse the controls file, then apply the save-DB overrides (scenario, token,
    // action) on top — an override with an empty action UNBINDS the token.
    public static ControlsMap Parse(string toml,
        IReadOnlyList<(string scenario, string token, string action)>? overrides = null)
    {
        var map = new ControlsMap();
        foreach (var section in Toml.Parse(toml))
        {
            if (section.Key.Length == 0) continue;
            if (section.Key == SettingsSection)
            {
                foreach (var kv in section.Value)
                {
                    if (kv.Key == "deadzone" && kv.Value is double dz) map.Deadzone = dz;
                    else if (kv.Key == "axis_threshold" && kv.Value is double th) map.AxisThreshold = th;
                    else throw new EvaluateException(
                        $"controls [{SettingsSection}]: unknown setting '{kv.Key}' " +
                        "(valid: deadzone, axis_threshold)");
                }
                continue;
            }
            // A binding-less section is still a real scenario — e.g. an empty
            // [Cutscene] that scripts switch to when ALL input should go quiet.
            if (!map.Scenarios.ContainsKey(section.Key))
                map.Scenarios[section.Key] = new Dictionary<string, ActionDef>();
            foreach (var kv in section.Value)
            {
                if (kv.Value is not string target || target.Length == 0)
                    throw new EvaluateException(
                        $"controls [{section.Key}]: '{kv.Key}' must map to an action name string");
                map.Bind(section.Key, kv.Key, target);
            }
        }

        if (overrides is not null)
            foreach (var (scenario, token, action) in overrides)
            {
                map.Unbind(scenario, token);
                if (action.Length > 0) map.Bind(scenario, token, action);
            }

        // Drop actions whose bindings were all overridden away, so a subscription to
        // a fully-unbound action still resolves (the action exists, it just never fires).
        return map;
    }

    private void Bind(string scenario, string token, string target)
    {
        var (actionName, component) = SplitAxisSuffix(target);
        var binding = ParseToken(scenario, token);
        binding.Action = actionName;
        binding.Component = component;

        if (!Scenarios.TryGetValue(scenario, out var actions))
            Scenarios[scenario] = actions = new Dictionary<string, ActionDef>();
        if (!actions.TryGetValue(actionName, out var def))
            actions[actionName] = def = new ActionDef { Name = actionName };

        bool vector = binding.Source == BindingSource.Stick || component != AxisComponent.Whole;
        if (def.Bindings.Count > 0 && def.IsVector != vector)
            throw new EvaluateException(
                $"controls [{scenario}]: '{actionName}' mixes vector and non-vector bindings " +
                "(a Stick_/axis-suffixed action takes only Stick_ or '+x/-x/+y/-y' bindings)");
        def.IsVector = vector;
        def.Bindings.Add(binding);
    }

    private void Unbind(string scenario, string token)
    {
        if (!Scenarios.TryGetValue(scenario, out var actions)) return;
        foreach (var def in actions.Values)
            def.Bindings.RemoveAll(b => string.Equals(b.Token, token, StringComparison.OrdinalIgnoreCase));
    }

    private static (string action, AxisComponent component) SplitAxisSuffix(string target)
    {
        foreach (var (suffix, comp) in AxisSuffixes)
            if (target.EndsWith(suffix, StringComparison.Ordinal))
                return (target[..^suffix.Length], comp);
        return (target, AxisComponent.Whole);
    }

    private static readonly (string, AxisComponent)[] AxisSuffixes =
    {
        ("+x", AxisComponent.PlusX), ("-x", AxisComponent.MinusX),
        ("+y", AxisComponent.PlusY), ("-y", AxisComponent.MinusY),
    };

    // Common pad shorthand -> the Godot enum name (kept tiny and unambiguous).
    private static readonly Dictionary<string, string> ButtonAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["l3"] = "LeftStick", ["r3"] = "RightStick",
        ["lb"] = "LeftShoulder", ["rb"] = "RightShoulder",
    };

    private static Binding ParseToken(string scenario, string token)
    {
        Binding Make(BindingSource source, long code) =>
            new() { Token = token, Source = source, Code = code };

        EvaluateException Fail(string kind, Type enumType) => new(
            $"controls [{scenario}]: unknown {kind} token '{token}' " +
            $"(the part after the prefix must be a {enumType.Name} name in snake_case)");

        if (token.StartsWith("Button_", StringComparison.OrdinalIgnoreCase))
        {
            var name = token["Button_".Length..];
            if (ButtonAliases.TryGetValue(name, out var alias)) name = alias;
            return Enum.TryParse<JoyButton>(SnakeToPascal(name), true, out var jb)
                ? Make(BindingSource.JoyButton, (long)jb) : throw Fail("button", typeof(JoyButton));
        }
        if (token.StartsWith("Key_", StringComparison.OrdinalIgnoreCase))
        {
            var name = SnakeToPascal(token["Key_".Length..]);
            if (name.Length > 0 && char.IsDigit(name[0])) name = "Key" + name;   // Key_0 -> Key.Key0
            return Enum.TryParse<Key>(name, true, out var k)
                ? Make(BindingSource.Key, (long)k) : throw Fail("key", typeof(Key));
        }
        if (token.StartsWith("Axis_", StringComparison.OrdinalIgnoreCase))
        {
            return Enum.TryParse<JoyAxis>(SnakeToPascal(token["Axis_".Length..]), true, out var ax)
                ? Make(BindingSource.JoyAxis, (long)ax) : throw Fail("axis", typeof(JoyAxis));
        }
        if (token.StartsWith("Stick_", StringComparison.OrdinalIgnoreCase))
        {
            var side = token["Stick_".Length..].ToLowerInvariant();
            if (side is not ("left" or "right"))
                throw new EvaluateException(
                    $"controls [{scenario}]: unknown stick token '{token}' (Stick_left or Stick_right)");
            return Make(BindingSource.Stick, side == "left" ? 0 : 1);
        }
        if (token.StartsWith("Mouse_", StringComparison.OrdinalIgnoreCase))
        {
            return Enum.TryParse<MouseButton>(SnakeToPascal(token["Mouse_".Length..]), true, out var mb)
                ? Make(BindingSource.MouseButton, (long)mb) : throw Fail("mouse button", typeof(MouseButton));
        }
        throw new EvaluateException(
            $"controls [{scenario}]: unknown binding token '{token}' " +
            "(expected Button_*, Key_*, Axis_*, Stick_left/right, or Mouse_*)");
    }

    internal static string SnakeToPascal(string snake) =>
        string.Concat(snake.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
}

public enum BindingSource { JoyButton, Key, JoyAxis, Stick, MouseButton }

// How a digital/analog binding feeds a VECTOR action (Whole = it IS the value).
public enum AxisComponent { Whole, PlusX, MinusX, PlusY, MinusY }

// One physical source routed to one action.
public sealed class Binding
{
    public string Token = "";          // the original token (overrides key on it)
    public BindingSource Source;
    public long Code;                  // JoyButton / Key / JoyAxis / MouseButton value; Stick: 0 = left, 1 = right
    public string Action = "";
    public AxisComponent Component = AxisComponent.Whole;
}

// One action within one scenario: its bindings and whether it reads as a vector.
public sealed class ActionDef
{
    public string Name = "";
    public bool IsVector;
    public readonly List<Binding> Bindings = new();
}
