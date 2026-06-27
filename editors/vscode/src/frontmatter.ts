import * as vscode from "vscode";
import { EvtSpec } from "./spec";

export const EVT_SELECTOR: vscode.DocumentSelector = { language: "evt" };

// ---- frontmatter parsing --------------------------------------------------

interface Frontmatter {
  startLine: number; // the opening ---
  endLine: number;   // the closing --- (exclusive of body)
}

// The leading `---` … `---` block, or null if the file has no signature.
function frontmatterRange(doc: vscode.TextDocument): Frontmatter | null {
  if (doc.lineCount === 0 || doc.lineAt(0).text.trim() !== "---") return null;
  for (let i = 1; i < doc.lineCount; i++) {
    if (doc.lineAt(i).text.trim() === "---") return { startLine: 0, endLine: i };
  }
  return null; // unterminated: treated as all-body by the runtime
}

function inFrontmatter(doc: vscode.TextDocument, line: number): Frontmatter | null {
  const fm = frontmatterRange(doc);
  return fm && line > fm.startLine && line < fm.endLine ? fm : null;
}

// The nearest top-level `key:` above `line` (the section the cursor is editing under).
function sectionAt(doc: vscode.TextDocument, fm: Frontmatter, line: number): string | null {
  for (let i = line; i > fm.startLine; i--) {
    const m = /^([A-Za-z_]+):\s*(.*)$/.exec(doc.lineAt(i).text);
    if (m) return m[1];
  }
  return null;
}

function isNodeScript(doc: vscode.TextDocument): boolean {
  return doc.fileName.endsWith(".node.evt");
}

// The capability apis this file declared in `apis:` (what the sandbox actually exposes).
export function declaredApis(doc: vscode.TextDocument): Set<string> {
  const fm = frontmatterRange(doc);
  return fm ? listedUnder(doc, fm, "apis") : new Set();
}

function hooksFor(doc: vscode.TextDocument, spec: EvtSpec): string[] {
  return isNodeScript(doc) ? spec.nodeHooks : spec.systemHooks;
}

// Values already listed under a section (so completion can skip them).
function listedUnder(doc: vscode.TextDocument, fm: Frontmatter, section: string): Set<string> {
  const out = new Set<string>();
  let inSection = false;
  for (let i = fm.startLine + 1; i < fm.endLine; i++) {
    const text = doc.lineAt(i).text;
    const key = /^([A-Za-z_]+):/.exec(text);
    if (key) { inSection = key[1] === section; continue; }
    if (!inSection) continue;
    const item = /^\s*-\s*([^:#\s]+)/.exec(text);
    if (item) out.add(item[1]);
  }
  return out;
}

// ---- completion -----------------------------------------------------------

function completionProvider(spec: () => EvtSpec): vscode.CompletionItemProvider {
  return {
    provideCompletionItems(doc, pos) {
      const fm = inFrontmatter(doc, pos.line);
      if (!fm) return undefined;
      const s = spec();
      const line = doc.lineAt(pos.line).text;
      const item = /^(\s*)-\s*(\S*)$/.exec(line.slice(0, pos.character));

      // A list item under a section -> complete that section's values.
      if (item) {
        const section = sectionAt(doc, fm, pos.line);
        if (section === "apis") {
          const used = listedUnder(doc, fm, "apis");
          return s.apis.filter((a) => !used.has(a)).map((a) => {
            const c = new vscode.CompletionItem(a, vscode.CompletionItemKind.Module);
            c.detail = "EvaLuate capability api";
            c.documentation = new vscode.MarkdownString(
              s.apiNotes[a] ?? `Members: ${(s.apiMembers[a] ?? []).map((m) => `\`${a}.${m}\``).join(", ") || "(node)"}`,
            );
            return c;
          });
        }
        if (section === "register") {
          const used = listedUnder(doc, fm, "register");
          return hooksFor(doc, s).filter((h) => !used.has(h)).map((h) => {
            const c = new vscode.CompletionItem(h, vscode.CompletionItemKind.Event);
            c.detail = isNodeScript(doc) ? "node lifecycle hook" : "system lifecycle hook";
            return c;
          });
        }
        return undefined; // config/scenes/assets/returns: free text
      }

      // Start of a line at the top level -> complete frontmatter keys.
      if (/^\s*[A-Za-z_]*$/.test(line.slice(0, pos.character)) && !/^\s+/.test(line)) {
        const present = new Set<string>();
        for (let i = fm.startLine + 1; i < fm.endLine; i++) {
          const k = /^([A-Za-z_]+):/.exec(doc.lineAt(i).text);
          if (k) present.add(k[1]);
        }
        return s.frontmatterKeys.filter((k) => !present.has(k)).map((k) => {
          const c = new vscode.CompletionItem(k, vscode.CompletionItemKind.Property);
          c.insertText = new vscode.SnippetString(`${k}:\n - $0`);
          c.detail = "frontmatter key";
          return c;
        });
      }
      return undefined;
    },
  };
}

// ---- diagnostics ----------------------------------------------------------

function validate(doc: vscode.TextDocument, s: EvtSpec, sink: vscode.DiagnosticCollection): void {
  if (doc.languageId !== "evt") return;
  const fm = frontmatterRange(doc);
  if (!fm) { sink.delete(doc.uri); return; }

  const diags: vscode.Diagnostic[] = [];
  const validHooks = new Set(hooksFor(doc, s));
  const validApis = new Set(s.apis);
  const validKeys = new Set(s.frontmatterKeys);
  let section: string | null = null;

  for (let i = fm.startLine + 1; i < fm.endLine; i++) {
    const text = doc.lineAt(i).text;
    const key = /^([A-Za-z_]+):/.exec(text);
    if (key) {
      section = key[1];
      if (!validKeys.has(section)) {
        diags.push(diag(i, key[1], 0, `Unknown frontmatter key '${section}'. Valid: ${[...validKeys].join(", ")}.`,
          vscode.DiagnosticSeverity.Warning));
      }
      continue;
    }
    const item = /^(\s*-\s*)([^:#\s]+)/.exec(text);
    if (!item) continue;
    const value = item[2];
    const col = item[1].length;
    if (section === "apis" && !validApis.has(value) && !value.startsWith("godot:")) {
      diags.push(diag(i, value, col, `Unknown api '${value}'. Valid: ${[...validApis].join(", ")}. (It must be declared to be usable.)`,
        vscode.DiagnosticSeverity.Warning));
    } else if (section === "register" && !validHooks.has(value)) {
      diags.push(diag(i, value, col,
        `Unknown ${isNodeScript(doc) ? "node" : "system"} hook '${value}'. Valid: ${[...validHooks].join(", ")}.`,
        vscode.DiagnosticSeverity.Warning));
    }
  }

  // Sandbox fidelity: a capability api used in the body but NOT declared in `apis:` is
  // absent at runtime (the sandbox only exposes declared apis), so the script would error.
  // Flag it here the same way the body's completion hides undeclared apis.
  for (const d of undeclaredApiUses(doc, fm.endLine, validApis, declaredApis(doc))) diags.push(d);

  sink.set(doc.uri, diags);
}

// Find body references to a capability api that the frontmatter did not declare. A use is
// `name.` / `name:` / `name(`; skipped if the body locally rebinds the name (`local name`
// or `name =`), which would shadow the (absent) global.
function undeclaredApiUses(
  doc: vscode.TextDocument, bodyStart: number, allApis: Set<string>, declared: Set<string>,
): vscode.Diagnostic[] {
  const out: vscode.Diagnostic[] = [];
  // Strip line comments once; an api named in a comment isn't a real use.
  const code: string[] = [];
  for (let i = 0; i < doc.lineCount; i++) code[i] = i > bodyStart ? doc.lineAt(i).text.replace(/--.*$/, "") : "";
  const body = code.join("\n");

  // An api the body locally rebinds (`local save` / `save = ...`) shadows the absent global,
  // so it isn't a sandbox violation — exclude it across the WHOLE body, not line-by-line.
  const undeclared = [...allApis].filter(
    (a) => !declared.has(a) && !new RegExp(`\\blocal\\s+${a}\\b|(^|[^.\\w])${a}\\s*=[^=]`).test(body),
  );
  if (undeclared.length === 0) return out;

  for (let i = bodyStart + 1; i < doc.lineCount; i++) {
    for (const api of undeclared) {
      const use = new RegExp(`(^|[^.:\\w])(${api})\\s*[.:(]`, "g");
      let m: RegExpExecArray | null;
      while ((m = use.exec(code[i])) !== null) {
        const col = m.index + m[1].length;
        out.push(diag(i, api, col, `'${api}' is used but not declared in 'apis:'. ` +
          `The sandbox only exposes declared apis, so this is nil at runtime. Add '${api}' to 'apis:'.`,
          vscode.DiagnosticSeverity.Warning));
      }
    }
  }
  return out;
}

function diag(line: number, token: string, col: number, message: string, severity: vscode.DiagnosticSeverity): vscode.Diagnostic {
  const range = new vscode.Range(line, col, line, col + token.length);
  const d = new vscode.Diagnostic(range, message, severity);
  d.source = "evt";
  return d;
}

// ---- hover ----------------------------------------------------------------

function hoverProvider(spec: () => EvtSpec): vscode.HoverProvider {
  const keyDocs: Record<string, string> = {
    config: "TOML files exposed as `config.<section>.*` (read live; hot-reloads).",
    apis: "Capability apis added to the sandbox. Only what you list here is reachable.",
    register: "Lifecycle hooks this script handles — each needs a matching global `function`.",
    returns: "The narrowed module contract for `require` consumers.",
    assets: "Files watched for hot-reload alongside the script.",
    scenes: "System scripts only: restrict hooks to these scenes (omit ⇒ global).",
  };
  return {
    provideHover(doc, pos) {
      const fm = inFrontmatter(doc, pos.line);
      if (!fm) return undefined;
      const range = doc.getWordRangeAtPosition(pos, /[A-Za-z_][A-Za-z0-9_]*/);
      if (!range) return undefined;
      const word = doc.getText(range);
      const s = spec();
      if (keyDocs[word] && /^[A-Za-z_]+:/.test(doc.lineAt(pos.line).text)) {
        return new vscode.Hover(new vscode.MarkdownString(`**${word}** — ${keyDocs[word]}`), range);
      }
      if (s.apis.includes(word)) {
        const note = s.apiNotes[word];
        const members = (s.apiMembers[word] ?? []).map((m) => `\`${word}.${m}\``).join(", ");
        return new vscode.Hover(new vscode.MarkdownString(`**${word}** — ${note ?? (members || "capability api")}`), range);
      }
      return undefined;
    },
  };
}

// ---- registration ---------------------------------------------------------

export function registerFrontmatter(context: vscode.ExtensionContext, getSpec: () => EvtSpec): void {
  const diagnostics = vscode.languages.createDiagnosticCollection("evt");
  context.subscriptions.push(
    diagnostics,
    vscode.languages.registerCompletionItemProvider(EVT_SELECTOR, completionProvider(getSpec), "-", " ", ":"),
    vscode.languages.registerHoverProvider(EVT_SELECTOR, hoverProvider(getSpec)),
    vscode.workspace.onDidOpenTextDocument((d) => validate(d, getSpec(), diagnostics)),
    vscode.workspace.onDidChangeTextDocument((e) => validate(e.document, getSpec(), diagnostics)),
    vscode.workspace.onDidCloseTextDocument((d) => diagnostics.delete(d.uri)),
  );
  for (const d of vscode.workspace.textDocuments) validate(d, getSpec(), diagnostics);
}
