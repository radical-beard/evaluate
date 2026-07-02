using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Lua;

namespace Evaluate;

// GAS-lite: TOML-defined ABILITIES (activatable actions with costs, cooldowns and
// tags) over TOML-defined EFFECTS (attribute modifications), on top of per-node
// ATTRIBUTES declared in frontmatter. Attributes carry stamina semantics natively
// (regen, regen delay, exhaust-until-recover), because that is the shape almost
// every gameplay pool takes (sprint stamina, the boat's water supply, poise).
//
// Definitions are data: `*.ability` / `*.effect` files (scripts/-relative, like
// config TOMLs), hot-reloaded live — a tuned cost or magnitude applies on the next
// tick with no script re-run.

// One declared attribute on a node, e.g.
//   attributes: { water: { base: 100, min: 0, max: 100, regen: 18, regen_delay: 1.0, recover: 25 } }
public sealed class AttributeSpec
{
    public string Name = "";
    public double Base;
    public double Min;
    public double Max;
    public double Regen;         // per second, after RegenDelay since the last spend
    public double RegenDelay;    // seconds
    public double Recover;       // exhaust lifts when Current climbs back to this
}

// Live state of one attribute on one node.
public sealed class AttrState
{
    public AttributeSpec Spec = null!;
    public double Current;
    public double SinceSpend = 1e9;   // seconds since the last spend (gates regen)
    public bool Exhausted;             // drained to Min by a spend; holds `exhausted:<name>`
}

// One `[[effects]]` reference inside an ability.
public sealed class AbilityEffectRef
{
    public string Effect = "";     // *.effect path
    public string Target = "self";
}

// A parsed `*.ability`.
public sealed class AbilityDef
{
    public string Path = "";
    public string Name = "";          // file stem
    public double Cooldown;
    public bool Channeled;             // active until deactivated; cost drains per second
    public string CostAttribute = ""; // empty = free
    public double CostAmount;
    public List<string> Tags = new();
    public List<string> BlockTags = new();
    public List<string> GrantTags = new();
    public List<AbilityEffectRef> Effects = new();
}

// A parsed `*.effect`. `Attribute` may be dotted to target a field:
// "water" (the value), "water.max", "water.regen", "water.regen_delay", "water.recover".
public sealed class EffectDef
{
    public string Path = "";
    public string Attribute = "";
    public string Field = "";         // "" = the value itself
    public string Op = "add";         // add | mul | set
    public double Magnitude;
    public double Duration;            // 0 instant, -1 while-source-active/infinite, >0 seconds
    public double Period;              // >0 = apply Magnitude every Period seconds
}

// One live (non-instant) effect on a node.
public sealed class EffectInstance
{
    public string EffectPath = "";
    public double Remaining;           // seconds; -1 = until removed
    public double PeriodAccum;
    public string? SourceAbility;      // channel that applied it (removed on deactivate)
}

// Everything ability-related on ONE node.
public sealed class NodeAbilityState
{
    public GodotObject Node = null!;
    public readonly Dictionary<string, AttrState> Attrs = new();
    public readonly Dictionary<string, string> Granted = new();          // ability name -> def path
    public readonly Dictionary<string, double> CooldownUntil = new();    // ability name -> clock time
    public readonly HashSet<string> ActiveChannels = new();
    public readonly List<EffectInstance> Effects = new();
    public readonly Dictionary<string, int> Tags = new();                // tag -> count
    public readonly Dictionary<string, List<(string Owner, LuaValue Fn)>> EndedListeners = new();

    public void AddTag(string tag) => Tags[tag] = Tags.TryGetValue(tag, out var n) ? n + 1 : 1;
    public void RemoveTag(string tag)
    {
        if (!Tags.TryGetValue(tag, out var n)) return;
        if (n <= 1) Tags.Remove(tag); else Tags[tag] = n - 1;
    }
    public bool HasTag(string tag) => Tags.ContainsKey(tag);
}

// The ability engine. The Loader owns per-layer NodeAbilityState lists and calls
// Tick; this class owns definitions, application, and the Lua surfaces.
public sealed class AbilityRuntime
{
    private readonly GodotBinder _binder;
    private readonly Func<string, string> _readScript;
    private readonly Action<string> _log;
    private readonly Func<string?> _hookOwner;                  // listener attribution
    private readonly Action<LuaValue, LuaValue[]> _call;        // main-thread Lua call

    private readonly Dictionary<string, AbilityDef> _abilityDefs = new();
    private readonly Dictionary<string, EffectDef> _effectDefs = new();
    private double _clock;

    public AbilityRuntime(GodotBinder binder, Func<string, string> readScript, Action<string> log,
        Func<string?> hookOwner, Action<LuaValue, LuaValue[]> call)
    {
        _binder = binder; _readScript = readScript; _log = log;
        _hookOwner = hookOwner; _call = call;
    }

    // ---- declaration + grant (attach-time) -----------------------------------

    // Declare a script's `attributes:` on its node's state. Re-declaring the same
    // attribute updates the SPEC live (hot tuning) and clamps Current; two scripts
    // declaring the same attribute must agree at any one time — last declarer wins,
    // which for identical frontmatter is a no-op.
    public void DeclareAttributes(string scriptPath, GodotObject node, NodeAbilityState state,
        Dictionary<string, object> declared)
    {
        foreach (var kv in declared)
        {
            if (kv.Value is not Dictionary<string, object> cfg)
                throw new EvaluateException(
                    $"{scriptPath}: attributes: '{kv.Key}' must be a map " +
                    "(base/min/max/regen/regen_delay/recover)");
            foreach (var key in cfg.Keys)
                if (key is not ("base" or "min" or "max" or "regen" or "regen_delay" or "recover"))
                    throw new EvaluateException(
                        $"{scriptPath}: attributes: '{kv.Key}' has unknown key '{key}' " +
                        "(valid: base, min, max, regen, regen_delay, recover)");

            double Num(string key, double fallback) =>
                cfg.TryGetValue(key, out var v) && v is double d ? d : fallback;

            var spec = new AttributeSpec { Name = kv.Key };
            spec.Base = Num("base", 0);
            spec.Min = Num("min", 0);
            spec.Max = Num("max", Math.Max(spec.Base, spec.Min));
            spec.Regen = Num("regen", 0);
            spec.RegenDelay = Num("regen_delay", 0);
            spec.Recover = Num("recover", spec.Min + 0.25 * (spec.Max - spec.Min));
            if (spec.Max < spec.Min)
                throw new EvaluateException($"{scriptPath}: attributes: '{kv.Key}' has max < min");

            if (state.Attrs.TryGetValue(kv.Key, out var existing))
            {
                existing.Spec = spec;   // live re-tune; keep Current
                existing.Current = Math.Clamp(existing.Current, spec.Min, spec.Max);
            }
            else
                state.Attrs[kv.Key] = new AttrState { Spec = spec, Current = Math.Clamp(spec.Base, spec.Min, spec.Max) };
        }
    }

    public void Grant(string scriptPath, NodeAbilityState state, string abilityPath)
    {
        var def = AbilityDefFor(abilityPath);
        if (state.Granted.TryGetValue(def.Name, out var already) && already != abilityPath)
            throw new EvaluateException(
                $"{scriptPath}: ability '{def.Name}' is already granted from '{already}'");
        state.Granted[def.Name] = abilityPath;
    }

    // ---- definitions ----------------------------------------------------------

    public AbilityDef AbilityDefFor(string path)
    {
        if (_abilityDefs.TryGetValue(path, out var def)) return def;
        return _abilityDefs[path] = ParseAbility(path);
    }

    public EffectDef EffectDefFor(string path)
    {
        if (_effectDefs.TryGetValue(path, out var def)) return def;
        return _effectDefs[path] = ParseEffect(path);
    }

    // A changed `*.ability` / `*.effect` re-parses into the cache; live consumers
    // read through it, so new numbers apply on the next tick. Keeps last-good on error.
    public string ReloadDef(string path)
    {
        try
        {
            if (path.EndsWith(".ability")) { if (_abilityDefs.ContainsKey(path)) _abilityDefs[path] = ParseAbility(path); }
            else if (_effectDefs.ContainsKey(path)) _effectDefs[path] = ParseEffect(path);
            return $"reloaded {path} (live)";
        }
        catch (EvaluateException e) { return $"kept last-good {path} ({e.Message})"; }
    }

    private AbilityDef ParseAbility(string path)
    {
        var text = _readScript(path);
        if (string.IsNullOrEmpty(text))
            throw new EvaluateException($"ability '{path}' not found");
        var root = FlatModel(text);
        var def = new AbilityDef { Path = path, Name = StemOf(path, ".ability") };

        foreach (var key in root.Keys)
            if (key is not ("cooldown" or "channeled" or "cost" or "tags" or "block_tags" or "grant_tags" or "effects"))
                throw new EvaluateException($"{path}: unknown key '{key}'");

        def.Cooldown = GetNum(root, "cooldown", 0);
        def.Channeled = root.TryGetValue("channeled", out var ch) && ch is bool b && b;
        if (root.TryGetValue("cost", out var cost))
        {
            if (cost is not Dictionary<string, object> cm
                || !cm.TryGetValue("attribute", out var ca) || ca is not string attr
                || !cm.TryGetValue("amount", out var am) || am is not double amount)
                throw new EvaluateException($"{path}: cost = {{ attribute = \"...\", amount = n }}");
            def.CostAttribute = attr;
            def.CostAmount = amount;
        }
        def.Tags = Strings(root, "tags");
        def.BlockTags = Strings(root, "block_tags");
        def.GrantTags = Strings(root, "grant_tags");
        if (root.TryGetValue("effects", out var eff))
        {
            if (eff is not object[] list)
                throw new EvaluateException($"{path}: effects must be [[effects]] blocks");
            foreach (var item in list)
            {
                if (item is not Dictionary<string, object> em
                    || !em.TryGetValue("effect", out var ep) || ep is not string epath)
                    throw new EvaluateException($"{path}: each [[effects]] needs effect = \"<path>.effect\"");
                var target = em.TryGetValue("target", out var tg) && tg is string ts ? ts : "self";
                if (target != "self")
                    throw new EvaluateException($"{path}: [[effects]] target must be \"self\" " +
                        "(apply to others in code: self.abilities:apply(path, node))");
                def.Effects.Add(new AbilityEffectRef { Effect = epath, Target = target });
                EffectDefFor(epath);   // validate + warm eagerly, so a bad path fails at grant
            }
        }
        return def;
    }

    private EffectDef ParseEffect(string path)
    {
        var text = _readScript(path);
        if (string.IsNullOrEmpty(text))
            throw new EvaluateException($"effect '{path}' not found");
        var root = FlatModel(text);
        var def = new EffectDef { Path = path };

        foreach (var key in root.Keys)
            if (key is not ("attribute" or "op" or "magnitude" or "duration" or "period"))
                throw new EvaluateException($"{path}: unknown key '{key}'");

        if (!root.TryGetValue("attribute", out var a) || a is not string attr || attr.Length == 0)
            throw new EvaluateException($"{path}: an effect names its attribute");
        var dot = attr.IndexOf('.');
        def.Attribute = dot < 0 ? attr : attr[..dot];
        def.Field = dot < 0 ? "" : attr[(dot + 1)..];
        if (def.Field is not ("" or "max" or "min" or "regen" or "regen_delay" or "recover"))
            throw new EvaluateException(
                $"{path}: unknown attribute field '{def.Field}' (value, max, min, regen, regen_delay, recover)");
        def.Op = root.TryGetValue("op", out var op) && op is string ops ? ops : "add";
        if (def.Op is not ("add" or "mul" or "set"))
            throw new EvaluateException($"{path}: op must be add, mul or set");
        def.Magnitude = GetNum(root, "magnitude", 0);
        def.Duration = GetNum(root, "duration", 0);
        def.Period = GetNum(root, "period", 0);
        if (def.Period > 0 && def.Duration == 0)
            throw new EvaluateException($"{path}: a periodic effect needs a duration (or -1)");
        return def;
    }

    // A flat key -> value view of a whole TOML document (Toml.Parse's section shape
    // would misread a top-level inline table like `cost = {..}` as a section).
    private static Dictionary<string, object> FlatModel(string text)
    {
        var root = new Dictionary<string, object>();
        foreach (var kv in Toml.Model(text)) root[kv.Key] = Toml.FromToml(kv.Value);
        return root;
    }

    private static double GetNum(Dictionary<string, object> t, string key, double fallback)
        => t.TryGetValue(key, out var v) && v is double d ? d : fallback;

    private static List<string> Strings(Dictionary<string, object> t, string key)
        => t.TryGetValue(key, out var v) && v is object[] arr
            ? arr.OfType<string>().ToList() : new List<string>();

    private static string StemOf(string path, string ext)
    {
        var file = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        return file.EndsWith(ext) ? file[..^ext.Length] : file;
    }

    // ---- reads (modifier-aware) ------------------------------------------------

    // An attribute FIELD's effective value: the stored value plus every live
    // non-periodic modifier effect targeting it ((v + Σadd) * Πmul, `set` wins last).
    public double Value(NodeAbilityState st, string attr, string field = "")
    {
        if (!st.Attrs.TryGetValue(attr, out var a)) return 0;
        double v = field switch
        {
            "" => a.Current,
            "max" => a.Spec.Max,
            "min" => a.Spec.Min,
            "regen" => a.Spec.Regen,
            "regen_delay" => a.Spec.RegenDelay,
            "recover" => a.Spec.Recover,
            _ => 0,
        };
        double add = 0, mul = 1; double? set = null;
        foreach (var e in st.Effects)
        {
            var def = EffectDefFor(e.EffectPath);
            if (def.Attribute != attr || def.Field != field || def.Period > 0) continue;
            switch (def.Op)
            {
                case "add": add += def.Magnitude; break;
                case "mul": mul *= def.Magnitude; break;
                case "set": set = def.Magnitude; break;
            }
        }
        v = set ?? (v + add) * mul;
        return field == "" ? Math.Clamp(v, Value(st, attr, "min"), Value(st, attr, "max")) : v;
    }

    // ---- spend / activate / deactivate ------------------------------------------

    private bool CanPay(NodeAbilityState st, string attr, double amount)
        => attr.Length == 0
           || (st.Attrs.TryGetValue(attr, out var a) && !a.Exhausted && a.Current - amount >= a.Spec.Min - 1e-9);

    // Drain `amount`; hitting Min exhausts the attribute (tag `exhausted:<attr>`)
    // until Current regens back to Recover.
    private void Spend(NodeAbilityState st, string attr, double amount)
    {
        if (attr.Length == 0 || !st.Attrs.TryGetValue(attr, out var a)) return;
        a.Current = Math.Max(a.Spec.Min, a.Current - amount);
        a.SinceSpend = 0;
        if (a.Current <= a.Spec.Min + 1e-9 && !a.Exhausted)
        {
            a.Exhausted = true;
            st.AddTag($"exhausted:{attr}");
        }
    }

    public bool CanActivate(NodeAbilityState st, string name)
    {
        if (!st.Granted.TryGetValue(name, out var path)) return false;
        var def = AbilityDefFor(path);
        if (st.ActiveChannels.Contains(name)) return false;
        if (st.CooldownUntil.TryGetValue(name, out var until) && _clock < until) return false;
        if (def.BlockTags.Any(st.HasTag)) return false;
        if (def.CostAttribute.Length > 0)
        {
            if (!st.Attrs.ContainsKey(def.CostAttribute)) return false;
            if (def.Channeled)
            {
                var a = st.Attrs[def.CostAttribute];
                if (a.Exhausted || a.Current <= a.Spec.Min + 1e-9) return false;
            }
            else if (!CanPay(st, def.CostAttribute, def.CostAmount)) return false;
        }
        return true;
    }

    public bool Activate(NodeAbilityState st, string name)
    {
        if (!CanActivate(st, name)) return false;
        var def = AbilityDefFor(st.Granted[name]);
        if (!def.Channeled && def.CostAttribute.Length > 0) Spend(st, def.CostAttribute, def.CostAmount);
        if (def.Cooldown > 0) st.CooldownUntil[name] = _clock + def.Cooldown;
        if (def.Channeled)
        {
            st.ActiveChannels.Add(name);
            foreach (var tag in def.GrantTags) st.AddTag(tag);
        }
        foreach (var er in def.Effects)
            ApplyEffect(st, er.Effect, def.Channeled ? name : null);
        return true;
    }

    public void Deactivate(NodeAbilityState st, string name, string reason = "deactivated")
    {
        if (!st.ActiveChannels.Remove(name)) return;
        var def = AbilityDefFor(st.Granted[name]);
        foreach (var tag in def.GrantTags) st.RemoveTag(tag);
        // while-active (-1 duration) effects this channel applied end with it
        st.Effects.RemoveAll(e => e.SourceAbility == name && EffectDefFor(e.EffectPath).Duration < 0);
        if (st.EndedListeners.TryGetValue(name, out var listeners))
            foreach (var (_, fn) in listeners.ToArray()) _call(fn, new LuaValue[] { name, reason });
    }

    // Apply one effect file to a node's state. Instant effects mutate the stored
    // field now; duration effects attach as live instances (modifiers / periodics).
    public void ApplyEffect(NodeAbilityState st, string effectPath, string? sourceAbility = null)
    {
        var def = EffectDefFor(effectPath);
        if (!st.Attrs.ContainsKey(def.Attribute))
            throw new EvaluateException(
                $"{effectPath}: target node has no attribute '{def.Attribute}' " +
                "(declare it in a behavior's attributes: block)");
        if (def.Duration == 0)
        {
            MutateField(st, def, def.Magnitude);
            return;
        }
        st.Effects.Add(new EffectInstance
        {
            EffectPath = effectPath,
            Remaining = def.Duration,     // -1 = until removed
            SourceAbility = sourceAbility,
        });
    }

    // Permanently mutate a stored field (instant effects and periodic ticks).
    private void MutateField(NodeAbilityState st, EffectDef def, double magnitude)
    {
        var a = st.Attrs[def.Attribute];
        double Apply(double v) => def.Op switch
        {
            "add" => v + magnitude, "mul" => v * magnitude, _ => magnitude,
        };
        switch (def.Field)
        {
            case "": a.Current = Math.Clamp(Apply(a.Current), a.Spec.Min, a.Spec.Max); break;
            case "max": a.Spec.Max = Apply(a.Spec.Max); break;
            case "min": a.Spec.Min = Apply(a.Spec.Min); break;
            case "regen": a.Spec.Regen = Apply(a.Spec.Regen); break;
            case "regen_delay": a.Spec.RegenDelay = Apply(a.Spec.RegenDelay); break;
            case "recover": a.Spec.Recover = Apply(a.Spec.Recover); break;
        }
    }

    // ---- tick -------------------------------------------------------------------

    public void Tick(IEnumerable<NodeAbilityState> states, double dt)
    {
        _clock += dt;
        foreach (var st in states)
        {
            // age first, drains second: a drain this tick zeroes SinceSpend, so regen
            // (which checks it below) can never fight an active drain
            foreach (var a in st.Attrs.Values) a.SinceSpend += dt;

            // channels drain per second; an unpayable tick auto-deactivates (exhaust)
            foreach (var name in st.ActiveChannels.ToArray())
            {
                var def = AbilityDefFor(st.Granted[name]);
                if (def.CostAttribute.Length == 0) continue;
                Spend(st, def.CostAttribute, def.CostAmount * dt);
                var a = st.Attrs[def.CostAttribute];
                if (a.Exhausted) Deactivate(st, name, "exhausted");
            }

            // live effects: expiry + periodic application
            for (int i = st.Effects.Count - 1; i >= 0; i--)
            {
                var e = st.Effects[i];
                var def = EffectDefFor(e.EffectPath);
                if (def.Period > 0)
                {
                    e.PeriodAccum += dt;
                    while (e.PeriodAccum >= def.Period)
                    {
                        e.PeriodAccum -= def.Period;
                        MutateField(st, def, def.Magnitude);
                    }
                }
                if (e.Remaining >= 0)
                {
                    e.Remaining -= dt;
                    if (e.Remaining <= 0) st.Effects.RemoveAt(i);
                }
            }

            // regen + exhaust recovery
            foreach (var a in st.Attrs.Values)
            {
                var max = Value(st, a.Spec.Name, "max");
                var regen = Value(st, a.Spec.Name, "regen");
                var delay = Value(st, a.Spec.Name, "regen_delay");
                if (regen > 0 && a.SinceSpend >= delay && a.Current < max)
                    a.Current = Math.Min(max, a.Current + regen * dt);
                if (a.Exhausted && a.Current >= Value(st, a.Spec.Name, "recover"))
                {
                    a.Exhausted = false;
                    st.RemoveTag($"exhausted:{a.Spec.Name}");
                }
            }
        }
    }

    // ---- Lua surfaces ------------------------------------------------------------

    // `self.attributes`: read a name for its (modifier-aware, clamped) value; assign
    // to set the stored value (clamped). `self.attributes.water = 0` is a legal
    // hard-drain; it does NOT exhaust (only spends do).
    public LuaTable BuildAttributesSurface(NodeAbilityState st)
    {
        var t = new LuaTable();
        var mt = new LuaTable();
        mt["__index"] = new LuaFunction((c, ct) =>
        {
            var key = c.GetArgument(1);
            if (key.Type == LuaValueType.String)
            {
                var k = key.Read<string>();
                if (k == "max" || k == "has")
                {
                    var field = k;
                    c.Return(new LuaFunction((c2, ct2) =>
                    {
                        var attr = c2.GetArgument(c2.ArgumentCount - 1).Read<string>();
                        if (field == "has") c2.Return(st.Attrs.ContainsKey(attr));
                        else c2.Return(Value(st, attr, "max"));
                        return new(1);
                    }));
                    return new(1);
                }
                if (st.Attrs.ContainsKey(k)) { c.Return(Value(st, k)); return new(1); }
            }
            c.Return(LuaValue.Nil);
            return new(1);
        });
        mt["__newindex"] = new LuaFunction((c, ct) =>
        {
            var k = c.GetArgument(1).Read<string>();
            if (!st.Attrs.TryGetValue(k, out var a))
                throw new EvaluateException($"attributes: node declares no attribute '{k}'");
            var v = c.GetArgument(2).Read<double>();
            a.Current = Math.Clamp(v, a.Spec.Min, Value(st, k, "max"));
            return new(0);
        });
        t.Metatable = mt;
        return t;
    }

    // `self.abilities`: grant/activate/deactivate/is_active/can_activate/cooldown/
    // has_tag/apply/on_ended. Dot- and colon-call tolerant (args read from the tail).
    public LuaTable BuildAbilitiesSurface(NodeAbilityState st, Func<GodotObject, NodeAbilityState?> stateOf)
    {
        var t = new LuaTable();

        LuaValue Fn(Func<LuaFunctionExecutionContext, LuaValue> body) =>
            new LuaFunction((c, ct) => { c.Return(body(c)); return new(1); });
        string Arg(LuaFunctionExecutionContext c, int fromEnd = 1) =>
            c.GetArgument(c.ArgumentCount - fromEnd).Read<string>();

        t["grant"] = Fn(c => { Grant("<runtime>", st, Arg(c)); return LuaValue.Nil; });
        t["activate"] = Fn(c => Activate(st, Arg(c)));
        t["deactivate"] = Fn(c => { Deactivate(st, Arg(c)); return LuaValue.Nil; });
        t["is_active"] = Fn(c => st.ActiveChannels.Contains(Arg(c)));
        t["can_activate"] = Fn(c => CanActivate(st, Arg(c)));
        t["cooldown"] = Fn(c =>
            st.CooldownUntil.TryGetValue(Arg(c), out var until) ? Math.Max(0, until - _clock) : 0);
        t["has_tag"] = Fn(c => st.HasTag(Arg(c)));
        t["on_ended"] = Fn(c =>
        {
            var name = Arg(c, 2);
            var fn = c.GetArgument(c.ArgumentCount - 1);
            if (fn.Type != LuaValueType.Function)
                throw new EvaluateException("abilities:on_ended(name, fn) expects a function");
            if (!st.EndedListeners.TryGetValue(name, out var list)) st.EndedListeners[name] = list = new();
            list.Add((_hookOwner() ?? "<runtime>", fn));
            return LuaValue.Nil;
        });
        // apply(effect_path [, target_node]) — no target = self
        t["apply"] = Fn(c =>
        {
            var last = c.GetArgument(c.ArgumentCount - 1);
            if (last.TryRead<GodotInstanceProxy>(out var proxy))
            {
                var target = stateOf(proxy.Target)
                    ?? throw new EvaluateException(
                        "abilities:apply target has no attributes (no behavior declared any)");
                ApplyEffect(target, Arg(c, 2));
            }
            else
                ApplyEffect(st, Arg(c));
            return LuaValue.Nil;
        });
        return t;
    }

    // Drop a reloading script's ended-listeners (mirrors the fsm listener cleanup).
    public void RemoveListeners(NodeAbilityState st, string ownerPath)
    {
        foreach (var list in st.EndedListeners.Values) list.RemoveAll(l => l.Owner == ownerPath);
    }
}
