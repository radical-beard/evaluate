import * as vscode from "vscode";
import { loadSpec, EvtSpec } from "./spec";
import { registerFrontmatter } from "./frontmatter";
import { startLuaClient, stopLuaClient } from "./luaClient";

let spec: EvtSpec;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  spec = loadSpec();

  // Frontmatter IntelliSense (apis/register/keys) — always on, no LSP needed.
  registerFrontmatter(context, () => spec);

  // Reload the spec when settings change or the generated file is written.
  const reload = () => { spec = loadSpec(); };
  context.subscriptions.push(
    vscode.workspace.onDidChangeConfiguration((e) => {
      if (e.affectsConfiguration("evt")) reload();
    }),
  );
  const watcher = vscode.workspace.createFileSystemWatcher("**/evaluate-api.json");
  watcher.onDidChange(reload);
  watcher.onDidCreate(reload);
  context.subscriptions.push(watcher);

  // Lua body IntelliSense via lua-language-server (best-effort: needs a server binary).
  if (vscode.workspace.getConfiguration("evt").get<boolean>("lua.enable", true)) {
    try {
      await startLuaClient(context, () => spec);
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      vscode.window.showWarningMessage(
        `EvaLuate: Lua IntelliSense is off — ${msg}. ` +
          `Install the “Lua” (sumneko) extension or set evt.lua.serverPath. Frontmatter help still works.`,
      );
    }
  }
}

export function deactivate(): Thenable<void> | undefined {
  return stopLuaClient();
}
