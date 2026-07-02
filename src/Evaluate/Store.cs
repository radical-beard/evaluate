using System;
using System.Collections.Generic;
using System.Linq;
using Lua;

namespace Evaluate;

// The global SESSION state store — the framework's answer to "state that outlives
// the node holding it". Values live in the global layer (they survive every scene
// switch, push, and pop) and are NOT persisted to disk: durable saves stay the
// game's job (`save`/`sql`), because slots/profiles are game semantics. Keys are
// dotted strings by convention ("player.health"); `subscribe` takes an exact key
// or a "prefix." (trailing dot) and fires fn(key, new, old) on every set/delete.
// Subscriptions are owner-attributed (hot reload drops a script's stale closures)
// and lifetime-scoped (a scene layer's subscriptions die with the layer).
public sealed class Store
{
    private readonly Dictionary<string, LuaValue> _values = new();
    private readonly List<Sub> _subs = new();
    // Calls a subscriber UNDER ITS REGISTRATION CONTEXT (owner script + scene-layer
    // lifetime), so anything the callback registers inherits the right cleanup scope.
    private readonly Action<LuaValue, LuaValue[], string, object?> _call;

    private sealed class Sub
    {
        public string Pattern = "";     // exact key, or "prefix." (trailing dot)
        public LuaValue Fn;
        public string Owner = "";
        public object? Lifetime;
        public long Id;
    }

    private long _ids;

    public Store(Action<LuaValue, LuaValue[], string, object?> call) => _call = call;

    public LuaValue Get(string key) => _values.TryGetValue(key, out var v) ? v : LuaValue.Nil;

    public bool Has(string key) => _values.ContainsKey(key);

    public void Set(string key, LuaValue value)
    {
        var old = Get(key);
        if (value.Type == LuaValueType.Nil) _values.Remove(key);
        else _values[key] = value;
        Notify(key, value, old);
    }

    public void Delete(string key)
    {
        if (!_values.Remove(key, out var old)) return;
        Notify(key, LuaValue.Nil, old);
    }

    public IEnumerable<string> Keys(string prefix) =>
        _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal);

    public long Subscribe(string pattern, LuaValue fn, string owner, object? lifetime)
    {
        var sub = new Sub { Pattern = pattern, Fn = fn, Owner = owner, Lifetime = lifetime, Id = ++_ids };
        _subs.Add(sub);
        return sub.Id;
    }

    public void Unsubscribe(long id) => _subs.RemoveAll(s => s.Id == id);
    public void RemoveOwnerSubscriptions(string owner) => _subs.RemoveAll(s => s.Owner == owner);
    public void RemoveLifetimeSubscriptions(object lifetime) =>
        _subs.RemoveAll(s => ReferenceEquals(s.Lifetime, lifetime));

    private void Notify(string key, LuaValue value, LuaValue old)
    {
        foreach (var s in _subs.ToArray())
        {
            bool match = s.Pattern.EndsWith('.')
                ? key.StartsWith(s.Pattern, StringComparison.Ordinal)
                : key == s.Pattern;
            if (match) _call(s.Fn, new[] { (LuaValue)key, value, old }, s.Owner, s.Lifetime);
        }
    }
}
