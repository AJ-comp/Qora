// Qora language extension.
//   - Hover docs: hovering a gate / keyword shows a short Korean description.
//   - Live diagnostics: parse errors from the Qora engine are shown as squiggles (debounced).
//   - Command "Qora: Transpile to OpenQASM": emits the current file as OpenQASM 3 in a side editor.
//
// The engine runs as the Qora CLI (a self-contained .NET binary). By default the extension runs the
// per-platform binary BUNDLED in bin/<platform>-<arch>/ (Qora.exe on Windows, Qora on unix) — no .NET
// install needed; `qora.command` overrides it, and `qora.args` (+ dotnet) is a fallback. Whatever runs,
// it gets `--json`, the document is fed on stdin, and it replies with one line of JSON:
//   { success: bool, qasm: string, errors: [ { message, code, start, end } ] }
// (start/end are half-open character offsets; -1 when the error has no located token.)
// This spawns one short-lived process per change; a long-running LSP server is the future upgrade.
const vscode = require('vscode');
const cp = require('child_process');
const path = require('path');
const fs = require('fs');

const DOCS = {
  // single-qubit gates
  'H': '**H** — 하다마드 게이트\n\n큐비트를 **중첩**(50/50)으로 만들어요.\n|0⟩ → (|0⟩+|1⟩)/√2\n\n`H(q[0]);` → `h q[0];`',
  'X': '**X** — 파울리-X (NOT)\n\n큐비트를 **뒤집어요**. |0⟩ ↔ |1⟩\n\n`X(q[0]);` → `x q[0];`',
  'Y': '**Y** — 파울리-Y\n\n비트와 위상을 함께 뒤집는 회전.\n\n`Y(q[0]);` → `y q[0];`',
  'Z': '**Z** — 파울리-Z (위상 뒤집기)\n\n|1⟩의 **위상만** 뒤집어요(부호 −). |0⟩은 그대로.\n\n`Z(q[0]);` → `z q[0];`',
  'S': '**S** — 위상 게이트 (√Z)\n\nZ축으로 90° 회전 (Z의 절반).\n\n`S(q[0]);` → `s q[0];`',
  'T': '**T** — π/8 게이트 (√S)\n\nZ축으로 45° 회전 (S의 절반).\n\n`T(q[0]);` → `t q[0];`',
  // multi-qubit (controlled) gates
  'CNOT': '**CNOT** — 제어-NOT (2큐비트)\n\n**제어**가 |1⟩일 때만 **대상**을 뒤집어요. **얽힘**을 만드는 핵심 게이트.\n\n`CNOT(q[0], q[1]);` → `cx q[0], q[1];`',
  'CX': '**CX** — 제어-X (= CNOT)\n\nNOT = X 라서 CNOT과 **완전히 같은 게이트**. 제어가 |1⟩이면 대상을 뒤집어요.\n\n`CX(q[0], q[1]);` → `cx q[0], q[1];`',
  'CY': '**CY** — 제어-Y (2큐비트)\n\n제어가 |1⟩일 때만 대상에 Y를 걸어요.\n\n`CY(q[0], q[1]);` → `cy q[0], q[1];`',
  'CZ': '**CZ** — 제어-Z (2큐비트)\n\n두 큐비트가 모두 |1⟩일 때 위상을 뒤집어요.\n\n`CZ(q[0], q[1]);` → `cz q[0], q[1];`',
  'SWAP': '**SWAP** — 교환 (2큐비트)\n\n두 큐비트의 상태를 서로 **맞바꿔요**.\n\n`SWAP(q[0], q[1]);` → `swap q[0], q[1];`',
  'CCX': '**CCX** — 토폴리 (3큐비트)\n\n**제어 2개**가 모두 |1⟩일 때만 대상을 뒤집어요. 양자판 AND.\n\n`CCX(q[0], q[1], q[2]);` → `ccx q[0], q[1], q[2];`',
  // rotation gates
  'Rx': '**Rx(θ, q)** — X축 회전\n\n큐비트를 X축으로 **각도 θ만큼** 회전. θ는 `pi/2`, `0.5` 등.\n\n`Rx(pi/2, q[0]);` → `rx(pi/2) q[0];`',
  'Ry': '**Ry(θ, q)** — Y축 회전\n\n큐비트를 Y축으로 각도 θ만큼 회전.\n\n`Ry(0.5, q[0]);` → `ry(0.5) q[0];`',
  'Rz': '**Rz(θ, q)** — Z축 회전\n\n큐비트의 **위상**을 각도 θ만큼 회전.\n\n`Rz(pi/4, q[0]);` → `rz(pi/4) q[0];`',
  // measurement
  'M': '**M(q)** — 측정\n\n큐비트를 측정해서 **0 또는 1로 확정**(붕괴)시키고, 그 값을 비트로 돌려줘요.\n\n`bit r = M(q[0]);` → `r = measure q[0];`',
  // constants & keywords
  'pi': '**pi** — 원주율 π ≈ 3.14159\n\n회전 각도에 써요: `Rx(pi/2, q)`(90°), `Rz(pi/4, q)`(45°).',
  'operation': '**operation** — 함수(서브루틴) 정의\n\n`operation 이름(파라미터) { … }`\n`Main`이 진입점, 나머지는 OpenQASM `def`가 돼요.',
  'use': '**use** — 큐비트 할당\n\n`use q = Qubit[n];` — 큐비트 n개를 빌려 q로.\n\n→ `qubit[n] q;`',
  'Qubit': '**Qubit** — 양자 비트 타입\n\n`use q = Qubit[2];`처럼 할당에 써요. 0과 1의 중첩이 가능.',
  'const': '**const** — 불변 변수\n\n`const int n = 3;` / `const k = 5`. 한 번 정하면 못 바꿔요.',
  'var': '**var** — 가변 변수\n\n`var i = 0;` 후 `i = i + 1;`로 바꿀 수 있어요.',
  'bit': '**bit** — 고전 비트 (0/1)\n\n측정 결과를 담아요. `bit r = M(q[0]);`',
  'int': '**int** — 정수\n\n`int i = 0;` / `const int n = 3;`',
  'if': '**if** — 조건 분기\n\n`if (r == 1) { … }`. 보통 측정 결과 r로 분기(측정 피드백).',
  'for': '**for** — 범위 반복\n\n`for i in 0..2 { … }` — i가 0,1,2 (양끝 포함).',
  'while': '**while** — 조건 반복\n\n`while (i == 0) { … }`. 조건이 맞는 동안 반복.',
  'repeat': '**repeat** — 반복-until\n\n`repeat { … } until (r == 1);` — 조건 만족까지 반복(최소 한 번 실행).',
};

const WALKTHROUGH_ID = 'qora-lang.qora-language#qora.gettingStarted';

const BELL_EXAMPLE = `operation Bell(Qubit[2] q) {
    H(q[0]);
    CNOT(q[0], q[1]);
}

operation Main() {
    use q = Qubit[2];
    Bell(q);

    bit r0 = M(q[0]);
    bit r1 = M(q[1]);
}
`;

let diagnostics;
let statusBarItem;
let warnedInfra = false;
let extRoot;                       // context.extensionPath, captured in activate()
const chmodDone = new Set();       // binaries we've already +x'd this session
const debounceTimers = new Map();
const parseStates = new Map();

/**
 * Resolve which CLI to run, as { command, args }, or null when nothing runnable is available.
 *   1. qora.command set (non-empty) -> power-user override (a custom exe, or `dotnet` + Qora.dll via qora.args).
 *   2. otherwise (the default)       -> the bundled per-platform binary in bin/<platform>-<arch>/.
 *   3. no bundle for this platform but qora.args is set -> fall back to `dotnet <args>`.
 *   4. nothing runnable -> null (caller shows the once-only "no parser" warning).
 * The stdin-source + `--json` + JSON-reply contract is identical on every path.
 */
function qoraInvocation() {
  const cfg = vscode.workspace.getConfiguration('qora');
  const command = (cfg.get('command') || '').trim();
  const userArgs = cfg.get('args', []);

  if (command) return { command, args: [...userArgs, '--json'] };                    // (1) override

  const binPath = bundledBinaryPath();
  if (binPath && fs.existsSync(binPath)) {                                            // (2) bundled default
    ensureExecutable(binPath);
    return { command: binPath, args: [...userArgs, '--json'] };
  }

  if (userArgs.length) return { command: 'dotnet', args: [...userArgs, '--json'] };  // (3) dotnet fallback

  return null;                                                                       // (4) nothing to run
}

/** Path to the bundled self-contained binary for this OS/arch (null before activate() ran). */
function bundledBinaryPath() {
  if (!extRoot) return null;
  const name = process.platform === 'win32' ? 'Qora.exe' : 'Qora';
  return path.join(extRoot, 'bin', `${process.platform}-${process.arch}`, name);
}

/** Best-effort set the exec bit once per binary on unix (a .vsix can drop it); no-op on Windows. */
function ensureExecutable(binPath) {
  if (process.platform === 'win32' || chmodDone.has(binPath)) return;
  try { fs.chmodSync(binPath, 0o755); } catch { /* read-only mount etc.; spawn will surface EACCES */ }
  chmodDone.add(binPath);
}

/**
 * Run the Qora CLI over `text`. Resolves to `{ result }` on a parsed JSON reply,
 * or `{ error }` when the process could not run / did not return JSON.
 * `extraArgs` extends the contract per call — e.g. ['--stages'] asks for the heavy
 * ast/ir/irInverse payload, which the keystroke-driven diagnostics path never sends.
 */
function runQora(text, extraArgs = []) {
  return new Promise((resolve) => {
    const inv = qoraInvocation();
    if (!inv) { resolve({ error: 'no-binary' }); return; }   // no bundled binary + no override configured
    const command = inv.command;
    const args = [...inv.args, ...extraArgs];
    let proc;
    try {
      proc = cp.spawn(command, args);
    } catch (e) {
      resolve({ error: e.message });
      return;
    }

    // Decode as UTF-8 with a StringDecoder that holds partial multibyte sequences across chunk
    // boundaries — otherwise a Korean char split across two 'data' events corrupts the JSON.
    proc.stdout.setEncoding('utf8');
    proc.stderr.setEncoding('utf8');

    let stdout = '';
    let stderr = '';
    proc.on('error', (e) => resolve({ error: e.message })); // e.g. command not found
    proc.stdout.on('data', (d) => { stdout += d; });
    proc.stderr.on('data', (d) => { stderr += d; });
    proc.on('close', () => {
      const trimmed = stdout.trim();
      if (!trimmed) { resolve({ error: stderr.trim() || 'no output from Qora CLI' }); return; }
      try {
        resolve({ result: JSON.parse(trimmed) });
      } catch (e) {
        resolve({ error: `unexpected output: ${(stderr.trim() || trimmed).slice(0, 200)}` });
      }
    });

    proc.stdin.on('error', () => { /* ignore EPIPE if the process died early */ });
    proc.stdin.end(text);
  });
}

/** One place to nag (once) when the CLI can't be run, so we don't spam on every keystroke. */
function warnInfraOnce(message) {
  if (warnedInfra) return;
  warnedInfra = true;
  const msg = message === 'no-binary'
    ? `Qora: no bundled parser for ${process.platform}-${process.arch}. Set "qora.command" to a Qora executable, or "qora.args" to your Qora.dll to run it via dotnet.`
    : `Qora: could not run the parser (${message}). Check "qora.command" / "qora.args".`;
  vscode.window.showWarningMessage(msg);
}

/** Map an offset span to a document range, falling back to the first character when unlocated. */
function spanToRange(document, start, end) {
  if (start < 0 || end < 0) return new vscode.Range(0, 0, 0, 1);
  return new vscode.Range(document.positionAt(start), document.positionAt(end));
}

function documentKey(document) {
  return document.uri.toString();
}

function activeQoraDocument() {
  const editor = vscode.window.activeTextEditor;
  return editor && editor.document.languageId === 'qora' ? editor.document : null;
}

function setParseState(document, kind, message = '') {
  if (!document || document.languageId !== 'qora') return;
  parseStates.set(documentKey(document), { kind, message });
  if (activeQoraDocument()?.uri.toString() === document.uri.toString()) updateStatusBar(document);
}

function qoraErrorCount(document) {
  return (diagnostics.get(document.uri) || [])
    .filter((d) => d.severity === vscode.DiagnosticSeverity.Error)
    .length;
}

function updateStatusBar(document = activeQoraDocument()) {
  if (!statusBarItem) return;
  if (!document || document.languageId !== 'qora') {
    statusBarItem.hide();
    return;
  }

  const state = parseStates.get(documentKey(document));
  statusBarItem.backgroundColor = undefined;
  statusBarItem.command = undefined;

  if (state?.kind === 'checking') {
    statusBarItem.text = '$(sync~spin) Qora: checking';
    statusBarItem.tooltip = 'Qora parser is checking this file.';
  } else if (state?.kind === 'unavailable') {
    statusBarItem.text = '$(warning) Qora: parser unavailable';
    statusBarItem.tooltip = state.message || 'Qora parser could not run.';
    statusBarItem.backgroundColor = new vscode.ThemeColor('statusBarItem.warningBackground');
  } else {
    const count = qoraErrorCount(document);
    if (count > 0) {
      statusBarItem.text = `$(error) Qora: ${count} ${count === 1 ? 'error' : 'errors'}`;
      statusBarItem.tooltip = 'Open the Problems panel for Qora parse errors.';
      statusBarItem.command = 'workbench.actions.view.problems';
      statusBarItem.backgroundColor = new vscode.ThemeColor('statusBarItem.errorBackground');
    } else {
      statusBarItem.text = '$(check) Qora: OK';
      statusBarItem.tooltip = 'Qora parser accepted this file.';
    }
  }

  statusBarItem.show();
}

async function refreshDiagnostics(document) {
  if (!document || document.languageId !== 'qora') return;

  setParseState(document, 'checking');
  const version = document.version;                 // guard against a slower run overwriting a newer one
  const { result, error } = await runQora(document.getText());
  // Bail if the buffer moved on (a fresher run is/will be queued) OR the document was closed while we ran —
  // closing does NOT bump .version, so without the isClosed check a late result re-adds orphaned squiggles.
  if (document.isClosed || document.version !== version) return;
  // Don't clobber squiggles on an infra hiccup.
  if (error) {
    setParseState(document, 'unavailable', error);
    warnInfraOnce(error);
    return;
  }

  const diags = (result.errors || []).map((e) => {
    const d = new vscode.Diagnostic(
      spanToRange(document, e.start, e.end),
      e.message || 'parse error',
      vscode.DiagnosticSeverity.Error
    );
    d.code = e.code;
    d.source = 'qora';
    return d;
  });
  diagnostics.set(document.uri, diags);
  setParseState(document, 'ready');
  updateStatusBar(document);
}

function scheduleRefresh(document) {
  if (!document || document.languageId !== 'qora') return;
  setParseState(document, 'checking');
  const key = document.uri.toString();
  clearTimeout(debounceTimers.get(key));
  debounceTimers.set(key, setTimeout(() => refreshDiagnostics(document), 400));
}

function bundledExamplePath() {
  return path.join(extRoot || __dirname, 'examples', 'demo.qor');
}

function bundledExampleSource() {
  const examplePath = bundledExamplePath();
  try {
    return fs.readFileSync(examplePath, 'utf8');
  } catch {
    return BELL_EXAMPLE;
  }
}

async function openQoraScratch(content) {
  const doc = await vscode.workspace.openTextDocument({
    content: content.trimEnd() + '\n',
    language: 'qora',
  });
  await vscode.window.showTextDocument(doc, vscode.ViewColumn.Active);
  return doc;
}

async function openExample() {
  const examplePath = bundledExamplePath();
  if (fs.existsSync(examplePath)) {
    const doc = await vscode.workspace.openTextDocument(vscode.Uri.file(examplePath));
    await vscode.window.showTextDocument(doc, vscode.ViewColumn.Active);
    return;
  }
  await openQoraScratch(bundledExampleSource());
}

async function newBellExample() {
  await openQoraScratch(BELL_EXAMPLE);
}

async function openWalkthrough() {
  try {
    await vscode.commands.executeCommand('workbench.action.openWalkthrough', WALKTHROUGH_ID, false);
  } catch {
    await vscode.commands.executeCommand('workbench.action.openGettingStarted');
  }
}

async function transpile() {
  const editor = vscode.window.activeTextEditor;
  if (!editor || editor.document.languageId !== 'qora') {
    vscode.window.showInformationMessage('Qora: open a .qor file first.');
    return;
  }

  const { result, error } = await runQora(editor.document.getText());
  if (error) { warnInfraOnce(error); return; }

  if (!result.success) {
    const first = (result.errors && result.errors[0] && result.errors[0].message) || 'parse failed';
    vscode.window.showErrorMessage(`Qora: cannot transpile — ${first}.`);
    return;
  }

  const doc = await vscode.workspace.openTextDocument({ content: result.qasm, language: 'plaintext' });
  await vscode.window.showTextDocument(doc, vscode.ViewColumn.Beside);
}

// ---- "Show compilation stages" panel ----------------------------------------------------------
// Explicit-request only: the heavy `--stages` payload (AST / IR / synthesized inverse IR) is fetched
// when the user runs the command, never on keystrokes. While the panel stays open it refreshes on
// SAVE of the same document (opening the panel is the opt-in; save is a deliberate checkpoint).

let stagesPanel = null;      // the single reusable webview panel
let stagesDocUri = null;     // which document the panel is following

function escapeHtml(s) {
  return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/** One pipeline stage as a titled column; hidden when its text is empty. */
function stageColumn(title, text) {
  if (!text) return '';
  return `<section><h2>${escapeHtml(title)}</h2><pre>${escapeHtml(text)}</pre></section>`;
}

function stagesHtml(fileName, result) {
  const errors = (result.errors || [])
    .map((e) => `<li><code>${escapeHtml(e.code)}</code> ${escapeHtml(e.message)}</li>`)
    .join('');
  const banner = result.success
    ? ''
    : `<div class="errors"><strong>컴파일 실패 — 아래 단계까지 진행된 모습이에요.</strong><ul>${errors}</ul></div>`;

  return `<!DOCTYPE html>
<html lang="ko"><head><meta charset="UTF-8"><style>
  body { font-family: var(--vscode-editor-font-family, monospace); color: var(--vscode-editor-foreground);
         background: var(--vscode-editor-background); padding: 10px 14px; }
  .file { color: var(--vscode-descriptionForeground); margin-bottom: 10px; }
  .flow { color: var(--vscode-descriptionForeground); font-size: 12px; margin-bottom: 12px; }
  .errors { border-left: 3px solid var(--vscode-editorError-foreground); padding: 6px 12px; margin-bottom: 12px; }
  .errors ul { margin: 6px 0 0; padding-left: 18px; }
  main { display: flex; gap: 14px; align-items: flex-start; overflow-x: auto; }
  section { flex: 1 1 0; min-width: 260px; }
  h2 { font-size: 13px; margin: 0 0 6px; color: var(--vscode-descriptionForeground);
       border-bottom: 1px solid var(--vscode-panel-border); padding-bottom: 4px; }
  pre { font-size: 12px; line-height: 1.45; white-space: pre; overflow-x: auto;
        background: var(--vscode-textCodeBlock-background); padding: 10px; border-radius: 6px; }
</style></head><body>
  <div class="file">${escapeHtml(fileName)} — 저장하면 갱신돼요</div>
  <div class="flow">소스 → 파싱 → AST → Lowering → QoraIR → (Inverter → 역 IR) → Emitter → OpenQASM</div>
  ${banner}
  <main>
    ${stageColumn('1. AST (파서 출력)', result.ast)}
    ${stageColumn('2. QoraIR (Lowering)', result.ir)}
    ${stageColumn('3. 역 IR (Inverter가 합성)', result.irInverse)}
    ${stageColumn('4. OpenQASM 3 (Emitter)', result.qasm)}
  </main>
</body></html>`;
}

async function refreshStages(document) {
  if (!stagesPanel || !document) return;
  // capture identity BEFORE the (slow) CLI run: the panel may be disposed, re-targeted to another
  // document, or the document may change/close while we wait — a stale result must never render.
  const uri = document.uri.toString();
  const version = document.version;
  const { result, error } = await runQora(document.getText(), ['--stages']);
  if (!stagesPanel || stagesDocUri !== uri) return;         // disposed or re-targeted while the CLI ran
  if (document.isClosed || document.version !== version) return;  // buffer moved on; a fresher run follows
  if (error) { warnInfraOnce(error); return; }
  stagesPanel.webview.html = stagesHtml(path.basename(document.fileName), result);
}

async function showStages() {
  const editor = vscode.window.activeTextEditor;
  if (!editor || editor.document.languageId !== 'qora') {
    vscode.window.showInformationMessage('Qora: open a .qor file first.');
    return;
  }

  stagesDocUri = editor.document.uri.toString();
  if (!stagesPanel) {
    stagesPanel = vscode.window.createWebviewPanel(
      'qoraStages', 'Qora: 컴파일 단계', vscode.ViewColumn.Beside, { enableScripts: false }
    );
    stagesPanel.onDidDispose(() => { stagesPanel = null; stagesDocUri = null; });
  } else {
    stagesPanel.reveal(undefined, true);
  }
  await refreshStages(editor.document);
}

function provideQoraCodeLenses(document) {
  const lenses = [];
  for (let i = 0; i < document.lineCount; i++) {
    const line = document.lineAt(i);
    if (!/^\s*operation\s+Main\s*\(/.test(line.text)) continue;

    const start = line.firstNonWhitespaceCharacterIndex;
    const range = new vscode.Range(i, start, i, Math.min(line.text.length, start + 'operation Main'.length));
    lenses.push(
      new vscode.CodeLens(range, { title: 'OpenQASM으로 변환', command: 'qora.transpile' }),
      new vscode.CodeLens(range, { title: '컴파일 단계 보기', command: 'qora.showStages' })
    );
  }
  return lenses;
}

function activate(context) {
  extRoot = context.extensionPath;   // used to locate the bundled per-platform binary

  context.subscriptions.push(
    vscode.languages.registerHoverProvider('qora', {
      provideHover(document, position) {
        const range = document.getWordRangeAtPosition(position);
        if (!range) return undefined;
        const md = DOCS[document.getText(range)];
        return md ? new vscode.Hover(new vscode.MarkdownString(md), range) : undefined;
      },
    })
  );

  context.subscriptions.push(
    vscode.languages.registerCodeLensProvider('qora', {
      provideCodeLenses: provideQoraCodeLenses,
    })
  );

  diagnostics = vscode.languages.createDiagnosticCollection('qora');
  context.subscriptions.push(diagnostics);

  statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
  context.subscriptions.push(statusBarItem);

  context.subscriptions.push(
    vscode.commands.registerCommand('qora.transpile', transpile),
    vscode.commands.registerCommand('qora.showStages', showStages),
    vscode.commands.registerCommand('qora.openExample', openExample),
    vscode.commands.registerCommand('qora.newBellExample', newBellExample),
    vscode.commands.registerCommand('qora.openWalkthrough', openWalkthrough),
    vscode.window.onDidChangeActiveTextEditor((editor) => updateStatusBar(editor?.document)),
    vscode.workspace.onDidOpenTextDocument(refreshDiagnostics),
    vscode.workspace.onDidChangeTextDocument((e) => scheduleRefresh(e.document)),
    vscode.workspace.onDidSaveTextDocument(refreshDiagnostics),
    vscode.workspace.onDidSaveTextDocument((doc) => {
      if (stagesPanel && doc.uri.toString() === stagesDocUri) refreshStages(doc);
    }),
    vscode.workspace.onDidCloseTextDocument((doc) => {
      const key = doc.uri.toString();
      clearTimeout(debounceTimers.get(key));
      debounceTimers.delete(key);
      diagnostics.delete(doc.uri);
      parseStates.delete(doc.uri.toString());
      updateStatusBar();
    })
  );

  // Lint any Qora files already open when the extension activates.
  vscode.workspace.textDocuments.forEach(refreshDiagnostics);
  updateStatusBar();
}

function deactivate() {
  for (const t of debounceTimers.values()) clearTimeout(t);
  debounceTimers.clear();
  parseStates.clear();
}

module.exports = { activate, deactivate };
