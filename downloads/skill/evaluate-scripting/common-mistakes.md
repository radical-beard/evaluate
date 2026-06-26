# Common mistakes (and fixes)

These are the failure modes the framework's own enforcement suite checks for. Most are the
sandbox doing its job: if you use something you didn't declare, the script errors instead of
silently misbehaving.

## 1. Using a withheld global (`os`, `io`, `pcall`)

```lua
return os.clock()        -- ERROR: `os` is not in the sandbox (attempt to index nil 'os')
```

**Fix:** these are removed on purpose. Use `save`/`sql` for persistence; let errors
propagate instead of `pcall`. There is no time/IO global — get time from `godot.Time`
(e.g. `godot.Time.GetTicksMsec()`).

## 2. Using a capability you didn't declare

```lua
-- frontmatter declares no apis:
return input.is_down("right")   -- ERROR: `input` is nil — not declared in apis:
```

**Fix:** add it to frontmatter:

```
---
apis:
 - input
---
```

`godot` and `std` are ambient (never need declaring); `input`, `world`, `scene`, `save`,
`sql` always do.

## 3. Declaring a `returns` property without its accessors

```
---
returns:
 - position: get set vec3
---
local M = {}
return M                 -- ERROR at require time: no get_position / set_position exposed
```

**Fix:** expose the accessor functions the contract names:

```lua
local M, pos = {}, std.vec3(0,0,0)
function M.get_position() return pos end
function M.set_position(v) pos = v end
return M
```

Read-only? Declare `- position: get vec3` and expose only `get_position` (the setter stays
hidden from callers).

## 4. PascalCase on an instance method

```lua
local n = godot.Node3D.new()
n:GetChildCount()        -- does nothing useful: instance members are engine snake_case
```

**Fix:** `n:get_child_count()`. (Constructors/enums/statics *are* PascalCase:
`godot.Node3D.new()`, `godot.OS.GetName()` — see api-discovery.md.)

## 5. Mutating a struct in place without assigning it back

```lua
self.position.x = self.position.x + 1     -- reads a COPY; the node doesn't move
```

**Fix:** read, mutate, assign back:

```lua
local p = self.position
p.x = p.x + 1
self.position = p
```

## 6. A `register:` hook with no function (or vice versa)

A name in `register:` with no matching global function is silently never called; a function
not listed in `register:` is also never wired. Keep them in sync — every hook you implement
must be both `register:`-ed and defined as `function <name>(...) end`.

## 7. Scene-file slip-ups

- **Vector property written as a table:** `position = {x=0,y=1,z=0}` is read as a *child
  node* (and then errors: "no type"). Use an array: `position = [0, 1, 0]`.
- **Reserved characters in a node name:** `.  :  @  /  %  "` are rejected — Godot would
  rewrite the name and break `scene.find` path lookup.
- **Missing `type`:** every node needs `type = "SomeGodotClass"`.

## 8. Spawning from a node script

A `*.node.evt` acts on `self`; it should not create the world around it. Create nodes from a
**system** script (`world:add_child(...)` / `scene.add(...)`) or declare them in a `.scene`
file.
