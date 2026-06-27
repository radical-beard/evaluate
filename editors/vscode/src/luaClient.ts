import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import {
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
  TransportKind,
  DidChangeTextDocumentNotification,
  DidChangeTextDocumentParams,
} from "vscode-languageclient/node";
import { EvtSpec } from "./spec";
import { declaredApis } from "./frontmatter";

// Lua body IntelliSense for .evt files.
//
// .evt = YAML frontmatter + Lua body. We run our OWN lua-language-server and register it on
// the real .evt documents, but blank the frontmatter in the text we sync to it (didOpen /
// didChange middleware). Blanking is line-for-line — the same thing the runtime does — so
// the Lua body keeps its exact line/columns and every position maps 1:1 (completion, hover,
// signature help and push-diagnostics all come back keyed to the real .evt URI, no
// remapping). The server is told to treat *.evt as Lua and to load the generated LuaCATS
// library via the configuration middleware (so the user's own settings are untouched).
//
// Stretch goal — mirror the sandbox: the body's completion only offers the capability apis
// this file actually declared in `apis:` (see completion middleware below).

let client: LanguageClient | undefined;

// ---- frontmatter blanking (identity line mapping) -------------------------

function frontmatterEndLine(text: string): number {
  const lines = text.split(/\r\n|\n/);
  if (lines[0]?.trim() !== "---") return -1;
  for (let i = 1; i < lines.length; i++) if (lines[i].trim() === "---") return i;
  return -1;
}

function blank(text: string): string {
  const end = frontmatterEndLine(text);
  if (end < 0) return text;
  const lines = text.split(/\r\n|\n/);
  for (let i = 0; i <= end; i++) lines[i] = ""; // line-for-line: positions unchanged
  return lines.join("\n");
}

// A view of the document whose getText() (full-text overload) and languageId read as blanked
// Lua. Range reads pass through, so incremental body edits keep working unchanged.
function blankedDoc(doc: vscode.TextDocument): vscode.TextDocument {
  return new Proxy(doc, {
    get(target, prop, receiver) {
      if (prop === "getText") {
        return (range?: vscode.Range) => (range ? target.getText(range) : blank(target.getText()));
      }
      if (prop === "languageId") return "lua";
      return Reflect.get(target, prop, receiver);
    },
  }) as vscode.TextDocument;
}

// ---- server discovery -----------------------------------------------------

interface Server {
  command: string;
  cwd?: string;
}

function exists(p: string): boolean {
  try { return fs.existsSync(p); } catch { return false; }
}

function findOnPath(name: string): string | undefined {
  const exe = process.platform === "win32" ? `${name}.exe` : name;
  for (const dir of (process.env.PATH ?? "").split(path.delimiter)) {
    if (dir && exists(path.join(dir, exe))) return path.join(dir, exe);
  }
  return undefined;
}

// cwd = the lua-language-server root (the dir whose bin/ holds the binary). The launcher
// resolves main.lua / meta/ relative to it; sumneko sets this defensively, so we match.
function serverRoot(command: string): string | undefined {
  const binDir = path.dirname(command);
  if (path.basename(binDir).toLowerCase() === "bin") return path.dirname(binDir);
  return undefined;
}

function locateServer(): Server {
  const configured = vscode.workspace.getConfiguration("evt").get<string>("lua.serverPath") ?? "";
  if (configured) {
    if (!exists(configured)) throw new Error(`evt.lua.serverPath '${configured}' does not exist`);
    return { command: configured, cwd: serverRoot(configured) };
  }

  // The sumneko "Lua" extension bundles a server; reuse its binary (cwd must be its root).
  const ext = vscode.extensions.getExtension("sumneko.lua");
  if (ext) {
    const bin = process.platform === "win32" ? "lua-language-server.exe" : "lua-language-server";
    const osDir = process.platform === "win32" ? "bin-Windows" : process.platform === "darwin" ? "bin-macOS" : "bin-Linux";
    for (const rel of [["server", "bin", bin], ["server", osDir, bin]]) {
      const p = path.join(ext.extensionPath, ...rel);
      if (exists(p)) return { command: p, cwd: path.join(ext.extensionPath, "server") };
    }
  }

  // A lua-language-server on PATH.
  const onPath = findOnPath("lua-language-server");
  if (onPath) return { command: onPath, cwd: serverRoot(onPath) };

  throw new Error("no lua-language-server found");
}

// ---- settings injected via the configuration middleware -------------------

function libraryPaths(): string[] {
  const cfg = vscode.workspace.getConfiguration("evt").get<string[]>("lua.libraryPaths") ?? [];
  const out: string[] = [];
  for (const entry of cfg) {
    if (path.isAbsolute(entry)) { if (exists(entry)) out.push(entry); continue; }
    for (const f of vscode.workspace.workspaceFolders ?? []) {
      const p = path.join(f.uri.fsPath, entry);
      if (exists(p)) out.push(p);
    }
  }
  return out;
}

// EvaLuate is Lua 5.2; the LuaCATS declare the ambient globals (godot/std/save/...). We add
// self/config and silence the "global function" lint that hook definitions would trip.
function luaSection(base: Record<string, unknown> | undefined): Record<string, unknown> {
  const lua = { ...(base ?? {}) } as Record<string, any>;
  lua.runtime = { ...(lua.runtime ?? {}), version: "Lua 5.2" };
  lua.workspace = {
    ...(lua.workspace ?? {}),
    library: [...((lua.workspace?.library as string[]) ?? []), ...libraryPaths()],
    checkThirdParty: false,
  };
  lua.diagnostics = {
    ...(lua.diagnostics ?? {}),
    globals: [...((lua.diagnostics?.globals as string[]) ?? []), "self", "config"],
    disable: [...((lua.diagnostics?.disable as string[]) ?? []), "lowercase-global"],
  };
  lua.telemetry = { ...(lua.telemetry ?? {}), enable: false };
  return lua;
}

// ---- stretch goal: scope body completion to declared apis -----------------

function undeclaredApiSet(doc: vscode.TextDocument, spec: EvtSpec): Set<string> {
  const declared = declaredApis(doc);
  return new Set(spec.apis.filter((a) => !declared.has(a)));
}

function labelText(item: vscode.CompletionItem): string {
  return typeof item.label === "string" ? item.label : item.label.label;
}

// ---- lifecycle ------------------------------------------------------------

export async function startLuaClient(context: vscode.ExtensionContext, getSpec: () => EvtSpec): Promise<void> {
  const server = locateServer();

  const serverOptions: ServerOptions = {
    run: { command: server.command, args: ["--locale=en-us"], options: { cwd: server.cwd }, transport: TransportKind.stdio },
    debug: { command: server.command, args: ["--locale=en-us"], options: { cwd: server.cwd }, transport: TransportKind.stdio },
  };

  const clientOptions: LanguageClientOptions = {
    documentSelector: [{ scheme: "file", language: "evt" }],
    middleware: {
      // (1) Tell the server *.evt is Lua and load the generated library — without writing to
      //     the user's real settings (which would also retag the file and hide the YAML).
      workspace: {
        configuration: async (params, token, next) => {
          const result = (await next(params, token)) as any[];
          params.items.forEach((item, i) => {
            if (item.section === "files.associations") {
              result[i] = { ...(result[i] ?? {}), "*.evt": "lua" };
            } else if (item.section === "Lua") {
              result[i] = luaSection(result[i]);
            }
          });
          return result;
        },
      },
      // (2) Blank the frontmatter in the synced text.
      didOpen: (document, next) => next(blankedDoc(document)),
      didChange: (event, next) => {
        const fmEnd = frontmatterEndLine(event.document.getText());
        const touchesFrontmatter = fmEnd >= 0 && event.contentChanges.some((c) => c.range.start.line <= fmEnd);
        if (!touchesFrontmatter) return next(event); // body edit: incremental, identity ranges
        // A frontmatter edit can shift the body; resync the whole blanked text.
        const params: DidChangeTextDocumentParams = {
          textDocument: {
            uri: client!.code2ProtocolConverter.asUri(event.document.uri),
            version: event.document.version,
          },
          contentChanges: [{ text: blank(event.document.getText()) }],
        };
        return client!.sendNotification(DidChangeTextDocumentNotification.type, params);
      },
      // (3) Stretch goal: hide capability apis this file didn't declare, so the body's
      //     autocomplete reflects exactly what the sandbox exposes.
      provideCompletionItem: async (document, position, ctx, token, next) => {
        const result = await next(document, position, ctx, token);
        if (!result) return result;
        // Only gate GLOBAL-scope completion. After a `.`/`:` it's member access (e.g. a Godot
        // member that happens to be named `scene`) — never filter those.
        const prefix = document.lineAt(position.line).text.slice(0, position.character);
        if (/[.:]\s*[A-Za-z0-9_]*$/.test(prefix)) return result;
        const undeclared = undeclaredApiSet(document, getSpec());
        if (undeclared.size === 0) return result;
        const keep = (items: vscode.CompletionItem[]) => items.filter((it) => !undeclared.has(labelText(it)));
        return Array.isArray(result)
          ? keep(result)
          : new vscode.CompletionList(keep(result.items), result.isIncomplete);
      },
    },
  };

  client = new LanguageClient("evtLua", "EvaLuate Lua", serverOptions, clientOptions);
  await client.start();
  context.subscriptions.push(client);
}

export async function stopLuaClient(): Promise<void> {
  await client?.stop();
  client = undefined;
}
