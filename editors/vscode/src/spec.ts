import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";

// The slice of the generated evaluate-api.json the extension needs for frontmatter
// IntelliSense. Loaded from the workspace when present (so it tracks the consumer's
// exact Godot + EvaLuate), else a small bundled fallback of the stable names.
export interface EvtSpec {
  apis: string[];
  apiMembers: Record<string, string[]>;
  apiNotes: Record<string, string>;
  // Never declarable in `apis:` (assets load via frontmatter `assets:`).
  blockedApis: string[];
  // Godot class / global-enum names from the live spec (0.10.0 apis-as-modules: each is
  // declarable in `apis:` and injected as a bare global). Empty when bundled — validation
  // accepts any PascalCase name without a list; these only power completion/hover.
  godotClasses: string[];
  godotEnums: string[];
  systemHooks: string[];
  nodeHooks: string[];
  frontmatterKeys: string[];
  returnsGrammar: string;
  evaluateVersion?: string;
  godotVersion?: string;
  source: "workspace" | "bundled";
}

// Stable fallback (api/hook/key names rarely change; the live spec wins when found).
const BUNDLED: Omit<EvtSpec, "source"> = {
  // The five framework services. Since 0.10.0, `apis:` ALSO accepts any Godot class or
  // global-enum name (PascalCase — `apis: [Input, Node3D, Key]` injects each as a bare
  // global) and host-registered C# extension apis; those aren't enumerable here, so the
  // validator accepts PascalCase entries and only the blocked names below are errors.
  apis: ["input", "world", "scene", "save", "sql"],
  apiMembers: {
    input: ["is_down"],
    world: [],
    scene: ["add", "change", "current", "find"],
    save: ["delete", "get", "set"],
    sql: ["exec", "exec_async", "flush", "query", "query_row", "snapshot", "transaction"],
  },
  apiNotes: {
    world: "The persistent global-root Node (a wrapped instance, not a table). Survives scene switches.",
  },
  blockedApis: ["DirAccess", "FileAccess", "ResourceLoader", "ResourceSaver"],
  godotClasses: [],
  godotEnums: [],
  systemHooks: [
    "on_start", "on_load", "on_unload", "on_quit", "on_enter", "on_exit",
    "on_focus_in", "on_focus_out", "on_pause", "on_resume",
    "on_update", "on_physics_update", "on_input",
  ],
  nodeHooks: [
    "on_attach", "on_load", "on_unload", "on_update", "on_physics_update",
    "on_input", "on_exit", "on_quit", "on_focus_in", "on_focus_out", "on_pause", "on_resume",
  ],
  frontmatterKeys: [
    "config", "apis", "register", "returns", "params", "require", "assets", "scenes",
    "properties", "behaviors", "machines", "attributes", "abilities",
    "name", "states", "initial", // *.statemachine.evt signature keys
  ],
  returnsGrammar: "<name>  |  <name>: 'get set <type>'  (read-only omits 'set')",
};

function resolveSpecPath(): string | undefined {
  const configured = vscode.workspace.getConfiguration("evt").get<string>("spec.path") ?? "";
  const candidates: string[] = [];
  if (configured) {
    if (path.isAbsolute(configured)) candidates.push(configured);
    else for (const f of vscode.workspace.workspaceFolders ?? []) candidates.push(path.join(f.uri.fsPath, configured));
  }
  return candidates.find((c) => fs.existsSync(c));
}

export function loadSpec(): EvtSpec {
  const file = resolveSpecPath();
  if (!file) return { ...BUNDLED, source: "bundled" };
  try {
    const raw = JSON.parse(fs.readFileSync(file, "utf8"));
    const apis: string[] = [];
    const apiMembers: Record<string, string[]> = {};
    const apiNotes: Record<string, string> = {};
    for (const a of raw.apis ?? []) {
      apis.push(a.name);
      apiMembers[a.name] = a.members ?? [];
      if (a.note) apiNotes[a.name] = a.note;
    }
    const names = (list: unknown): string[] =>
      Array.isArray(list) ? list.map((e: any) => e?.name).filter((n: any) => typeof n === "string") : [];
    return {
      apis: apis.length ? apis : BUNDLED.apis,
      apiMembers: apis.length ? apiMembers : BUNDLED.apiMembers,
      apiNotes: Object.keys(apiNotes).length ? apiNotes : BUNDLED.apiNotes,
      blockedApis: raw.blockedApis ?? BUNDLED.blockedApis,
      godotClasses: names(raw.godot?.classes),
      godotEnums: names(raw.godot?.globalEnums),
      systemHooks: raw.hooks?.system ?? BUNDLED.systemHooks,
      nodeHooks: raw.hooks?.node ?? BUNDLED.nodeHooks,
      frontmatterKeys: raw.frontmatter?.keys ?? BUNDLED.frontmatterKeys,
      returnsGrammar: raw.frontmatter?.returnsGrammar ?? BUNDLED.returnsGrammar,
      evaluateVersion: raw.evaluateVersion,
      godotVersion: raw.godotVersion,
      source: "workspace",
    };
  } catch {
    return { ...BUNDLED, source: "bundled" };
  }
}
