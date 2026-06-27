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
  systemHooks: [
    "on_start", "on_load", "on_unload", "on_quit", "on_enter", "on_exit",
    "on_focus_in", "on_focus_out", "on_pause", "on_resume",
    "on_update", "on_physics_update", "on_input",
  ],
  nodeHooks: [
    "on_attach", "on_load", "on_unload", "on_update", "on_physics_update",
    "on_input", "on_exit", "on_quit", "on_focus_in", "on_focus_out", "on_pause", "on_resume",
  ],
  frontmatterKeys: ["config", "apis", "register", "returns", "assets", "scenes"],
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
    return {
      apis: apis.length ? apis : BUNDLED.apis,
      apiMembers: apis.length ? apiMembers : BUNDLED.apiMembers,
      apiNotes: Object.keys(apiNotes).length ? apiNotes : BUNDLED.apiNotes,
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
