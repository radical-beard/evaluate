# CLAUDE.md

Guidance for Claude Code when working in the **Evaluate** repo. Read `README.md` for the full
architecture; this file covers how to build/test and the rules that are easy to violate.

## What this is

A data-driven, moddable game framework that runs **inside Godot** (Godot is the host; Evaluate
has no `main`). Engine-side is .NET/C# (`net10.0`, Godot 4.6-mono). Gameplay is authored in
`.evt` scripts: YAML frontmatter declares a capability-scoped *signature*, a custom loader
sandboxes each script to exactly what it declares, and a Lua body runs the behavior. Scripts
are auto-discovered and hot-reloaded.

## Build / test / run

    dotnet build dev/Dev.csproj                       # build the library + demo harness
    godot --headless --path dev -- --test             # enforcement suite (must stay green)
    godot --headless --path dev                        # run the demo game
    godot --headless --path dev -- --emit-api downloads/spec   # regenerate the API spec

Every behavior change needs a matching case in the enforcement suite (`EvaluateTests.cs`);
the suite is the project's contract, not an afterthought.

## STANDING RULE: keep the plugins and API references in sync — always

The scripting contract (frontmatter keys, capability `apis:`, lifecycle hooks, the `std.*` /
`godot.*` / capability surface, scene grammar) is mirrored in several downstream artifacts.
**Whenever you change that contract, update every mirror in the SAME change — without waiting to
be asked.** Treat these as part of "done", the way tests are:

1. **Regenerate the API spec** (generated — never hand-edit):
   `godot --headless --path dev -- --emit-api downloads/spec`. Commit the resulting
   `downloads/spec/**` (json + `luacats/*.lua` + markdown).
2. **Update the agent skill** (hand-authored — edit manually):
   `downloads/skill/evaluate-scripting/` — `SKILL.md`, `reference/*.md`, and `examples/` as
   relevant.
3. **Update the editor plugins** under `editors/`:
   - `editors/vscode/` — the frontmatter-key fallback in `src/spec.ts`, hover/validation in
     `src/frontmatter.ts`, then bump `package.json` `version` + add a `CHANGELOG.md` entry.
   - `editors/nvim/` — the Neovim plugin (filetype, syntax, frontmatter completion, LuaCATS
     wiring); keep it at parity with the VS Code extension.
   - `dev/addons/evaluate_scene/` — the Godot editor addon; only relevant to **scene-format**
     (`.scene` / `SceneFile` / `SceneWriter`) changes.
4. **Update `README.md` and `CHANGELOG.md`** (root) to describe the change.

If you are unsure whether a change touches the contract, assume it does and check the mirrors.

## Single sources of truth (so the mirrors stay honest)

- **Frontmatter keys** — `src/Evaluate/EvaluateDocs.cs` (`FrontmatterDoc().Keys` + grammars)
  drives the generated spec; `editors/vscode/src/spec.ts` (`frontmatterKeys`) is the bundled
  fallback. Both must list the same keys.
- **Capability apis** — `Loader.ApiNames` + the `BuildApi` switch (add an arm + array entry
  together; the docs emitter walks these live).
- **Lifecycle hooks** — `Loader.SystemHooks` / `Loader.NodeHooks`.
- **Safe language globals** — `Loader.SafeGlobals`.
The `--emit-api` emitter reads these live (engine ClassDB + reflection + the tables the loader
builds), so regenerating is what keeps the spec exact — don't hand-write API surface.

## Conventions

- The signature **is** the contract: the sandbox exposes only what frontmatter declares.
  Reject undeclared/ill-typed input at load time with a clear error, not deep in a hook.
- `src/Evaluate/Loader.cs` contains one intentional NUL byte (`ns + "\0" + name`, a hook-claim
  key separator) — `grep` sees the file as binary; use `grep -a`. It is not corruption.
</content>
