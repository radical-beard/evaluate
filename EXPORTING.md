# Exporting Evaluate games to Android & iOS

Evaluate is kept **trim/AOT-clean** so games built on it can ship through
Godot 4.6-mono's mobile pipelines: **Android runs .NET on Mono**, **iOS runs
NativeAOT** (still marked experimental upstream — test on hardware early).
The `mobile-smoke` workflow exports the dev harness for both on every push.

## What the library already handles for you

- **Analyzer-clean:** `Evaluate.csproj` sets `IsAotCompatible`; CI fails on any
  new `IL*` warning. The two deliberate reflection islands (the Godot statics
  fallback in `GodotBinder`, YamlDotNet frontmatter parsing) are suppressed
  with written justifications and covered by the points below.
- **Trimming roots:** an `ILLink.Descriptors.xml` embedded in the assembly
  preserves `Evaluate`, `Lua`, `YamlDotNet`, and `Tomlyn` when a consuming
  game enables trimming. This is load-bearing: `tools/AotSmoke` demonstrably
  fails without it and passes with it.
- **NativeAOT proof:** `dotnet publish tools/AotSmoke -c Release` builds a real
  native binary running the production frontmatter parser, the Lua VM
  (closures + metatables), and Tomlyn. Run it after touching anything
  reflection-adjacent.
- **Hot reload auto-off:** `EvaluateRuntime.HotReload` defaults to
  `OS.HasFeature("editor")` — exported bundles (read-only on iOS) never spin
  `FileSystemWatcher`s. Hosts may force it on for desktop dev builds.
- **SQLite that survives iOS:** the `save`/`sql` services use
  `Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.bundle_green`: the **system
  sqlite3 on iOS** (no native lib to smuggle through the export) and bundled
  `e_sqlite3` elsewhere. Provider init is explicit (`SqliteBoot`).

## Per-game checklist

1. **Godot export presets:** copy the shape of `dev/export_presets.cfg`.
   Include your script content in the export filter — `*.evt,*.scene,*.toml`
   ship as **data** inside the PCK (bundled interpreted code is App Store
   -compliant; *downloading* executable scripts post-install is not — ship
   content updates through app updates or pure-data remote config).
2. **Android:** Gradle build ON, arm64. Verify the APK contains
   `libe_sqlite3.so` (the CI job greps for it) — if Godot's export ever drops
   NuGet native libs, add the `.so` to the Gradle template as a jniLib.
3. **iOS:** `export_project_only=true`, then sign/build in Xcode as usual.
   NativeAOT implications: no runtime codegen anywhere (Evaluate has none);
   reflection works because Godot roots the bindings + project assemblies and
   Evaluate's descriptor roots the rest.
4. **Keep `script_export_mode=2`** (compiled Godot-side scripts) and let
   `.evt` travel as plain resources.
5. **First device run:** watch for `[evaluate] hot reload disabled` in the
   log — that's the exported-build path working.

## Known limits

- Godot's iOS .NET export is upstream-experimental; treat the first Xcode
  build per Godot upgrade as a gate, not a formality.
- `--emit-api` (docs mode) is dev-only and deliberately not AOT-safe.
- Mod *distribution* (loading third-party scripts at runtime from outside the
  PCK) remains out of scope for mobile; desktop only.
