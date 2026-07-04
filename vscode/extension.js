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

/**
 * Per-document CLI args: the source travels on stdin (so unsaved edits are analyzed live), which
 * loses the file's location — `--base-dir` restores it so `import` statements resolve relative to
 * the document. Untitled/virtual documents get no base dir: their imports report a clear error.
 */
function docArgs(document) {
  return document && document.uri && document.uri.scheme === 'file'
    ? ['--base-dir', path.dirname(document.uri.fsPath)]
    : [];
}

// ---------- Run (Braket LocalSimulator) ----------
//
// "Qora: Run Program" compiles the active document with the bundled CLI, then executes the QASM on
// Amazon Braket's LOCAL simulator — the one consumer measured to run Qora's FULL output (defs, ints,
// loops, even `while`). Python resolution order:
//   1. the `qora.python` setting (trusted override)
//   2. the env this extension provisioned earlier (remembered in globalState)
//   3. a suitable system Python (3.10–3.13 with amazon-braket-sdk importable)
//   4. one-time auto-provisioning into the extension's own storage: `uv` (a single static binary)
//      installs Python 3.12 + amazon-braket-sdk (~200 MB). The user never touches pip, and nothing
//      outside the extension's storage is modified.

let extContext;          // captured in activate() — globalStorage/globalState for provisioning
let runChannel;          // "Qora Run" output channel, created lazily
let braketPythonCache;   // per-session: a python we already verified

// Run-flow strings in the user's display language (native modals can't be styled, but they CAN be
// structured — title + detail — and speak the user's language).
const RUN_L10N = {
  en: {
    setupTitle: 'Set up the Qora simulator?',
    setupDetail: 'One-time setup so ▶ Run works:\n\n'
      + '  •  a suitable Python if you have one, else a private Python + the Braket simulator (~200 MB)\n'
      + '  •  installed only into this extension\'s own storage — nothing else on your system is touched\n'
      + '  •  afterwards every run takes a few seconds, offline\n\n'
      + 'Needs an internet connection (about 2–3 minutes).',
    setupButton: 'Set up',
    progressTitle: 'Qora: setting up the simulator (one-time)',
    stepProbe: 'looking for a usable Python…',
    stepUv: 'fetching the installer…',
    stepUnpack: 'unpacking…',
    stepPython: 'installing Python 3.12…',
    stepBraket: 'installing the Braket simulator (~200 MB, a few minutes)…',
    stepVerify: 'verifying…',
    setupFailed: (m) => `Qora: simulator setup failed — ${m}`,
    running: (name) => `Qora: running ${name}…`,
    runFailed: (m) => `Qora: run failed — ${m}`,
    openFirst: 'Qora: open a .qor file first.',
    cannotRun: (m) => `Qora: cannot run — ${m}`,
  },
  ko: {
    setupTitle: 'Qora 시뮬레이터를 준비할까요?',
    setupDetail: '▶ 실행을 위해 처음 한 번만 준비하면 돼요:\n\n'
      + '  •  적합한 Python이 있으면 그걸 쓰고, 없으면 전용 Python + Braket 시뮬레이터(~200 MB)를 받아요\n'
      + '  •  확장 전용 폴더에만 설치돼요 — 시스템의 다른 곳은 건드리지 않아요\n'
      + '  •  이후에는 인터넷 없이 몇 초 만에 실행돼요\n\n'
      + '인터넷 연결이 필요해요 (약 2~3분).',
    setupButton: '준비 시작',
    progressTitle: 'Qora: 시뮬레이터 준비 중 (최초 1회)',
    stepProbe: '쓸 수 있는 Python 찾는 중…',
    stepUv: '설치 도구 받는 중…',
    stepUnpack: '압축 푸는 중…',
    stepPython: 'Python 3.12 설치 중…',
    stepBraket: 'Braket 시뮬레이터 설치 중 (~200 MB, 몇 분)…',
    stepVerify: '확인 중…',
    setupFailed: (m) => `Qora: 시뮬레이터 준비 실패 — ${m}`,
    running: (name) => `Qora: ${name} 실행 중…`,
    runFailed: (m) => `Qora: 실행 실패 — ${m}`,
    openFirst: 'Qora: 먼저 .qor 파일을 열어주세요.',
    cannotRun: (m) => `Qora: 실행할 수 없어요 — ${m}`,
  },
  ja: {
    setupTitle: 'Qora シミュレータをセットアップしますか?',
    setupDetail: '▶ 実行のための初回セットアップです:\n\n'
      + '  •  使える Python があればそれを使い、なければ専用 Python + Braket シミュレータ(~200 MB)を取得します\n'
      + '  •  この拡張機能専用のフォルダにのみインストールされます — システムの他の場所には触れません\n'
      + '  •  以降はオフラインで数秒で実行できます\n\n'
      + 'インターネット接続が必要です (約 2〜3 分)。',
    setupButton: 'セットアップ',
    progressTitle: 'Qora: シミュレータを準備中 (初回のみ)',
    stepProbe: '使える Python を探しています…',
    stepUv: 'インストーラを取得中…',
    stepUnpack: '展開中…',
    stepPython: 'Python 3.12 をインストール中…',
    stepBraket: 'Braket シミュレータをインストール中 (~200 MB, 数分)…',
    stepVerify: '確認中…',
    setupFailed: (m) => `Qora: セットアップに失敗しました — ${m}`,
    running: (name) => `Qora: ${name} を実行中…`,
    runFailed: (m) => `Qora: 実行に失敗しました — ${m}`,
    openFirst: 'Qora: まず .qor ファイルを開いてください。',
    cannotRun: (m) => `Qora: 実行できません — ${m}`,
  },
  vi: {
    setupTitle: 'Thiết lập trình mô phỏng Qora?',
    setupDetail: 'Thiết lập một lần để ▶ Run hoạt động:\n\n'
      + '  •  dùng Python sẵn có nếu phù hợp, nếu không thì tải Python riêng + Braket (~200 MB)\n'
      + '  •  chỉ cài vào bộ nhớ riêng của tiện ích — không đụng đến phần còn lại của hệ thống\n'
      + '  •  sau đó mỗi lần chạy chỉ mất vài giây, không cần mạng\n\n'
      + 'Cần kết nối internet (khoảng 2–3 phút).',
    setupButton: 'Thiết lập',
    progressTitle: 'Qora: đang chuẩn bị trình mô phỏng (một lần)',
    stepProbe: 'đang tìm Python phù hợp…',
    stepUv: 'đang tải trình cài đặt…',
    stepUnpack: 'đang giải nén…',
    stepPython: 'đang cài Python 3.12…',
    stepBraket: 'đang cài trình mô phỏng Braket (~200 MB, vài phút)…',
    stepVerify: 'đang kiểm tra…',
    setupFailed: (m) => `Qora: thiết lập thất bại — ${m}`,
    running: (name) => `Qora: đang chạy ${name}…`,
    runFailed: (m) => `Qora: chạy thất bại — ${m}`,
    openFirst: 'Qora: hãy mở một tệp .qor trước.',
    cannotRun: (m) => `Qora: không thể chạy — ${m}`,
  },
};

/** The Run-flow string table for VS Code's display language (falls back to English). */
function runL10n() {
  const lang = (vscode.env.language || 'en').split('-')[0];
  return RUN_L10N[lang] || RUN_L10N.en;
}

function runnerDir() { return path.join(extRoot, 'runner'); }

/**
 * Spawn and collect output; resolves { code, stdout, stderr } and never rejects.
 * `onLine(text)` (optional) is called for each complete output line as it arrives — used to stream a
 * long install's live progress into the progress bar. uv redraws with `\r` and ANSI codes, so we split
 * on either newline kind and strip escape sequences before handing the line over.
 */
function execP(command, args, options = {}, stdinText, onLine) {
  return new Promise((resolve) => {
    let proc;
    try { proc = cp.spawn(command, args, options); }
    catch (e) { resolve({ code: -1, stdout: '', stderr: String(e.message || e) }); return; }
    let stdout = '';
    let stderr = '';
    let lineBuf = '';
    const feed = (chunk) => {
      if (!onLine) return;
      lineBuf += chunk;
      let nl;
      while ((nl = lineBuf.search(/[\r\n]/)) >= 0) {
        const line = lineBuf.slice(0, nl).replace(/\[[0-9;?]*[A-Za-z]/g, '').trim();
        lineBuf = lineBuf.slice(nl + 1);
        if (line) onLine(line);
      }
    };
    proc.stdout?.setEncoding('utf8');
    proc.stderr?.setEncoding('utf8');
    proc.stdout?.on('data', (d) => { stdout += d; feed(d); });
    proc.stderr?.on('data', (d) => { stderr += d; feed(d); });
    proc.on('error', (e) => resolve({ code: -1, stdout, stderr: stderr || String(e.message || e) }));
    proc.on('close', (code) => resolve({ code, stdout, stderr }));
    if (stdinText !== undefined) {
      proc.stdin?.on('error', () => { /* EPIPE if it died early */ });
      proc.stdin?.end(stdinText);
    }
  });
}

/** Is this python a 3.10–3.13 with the Braket SDK importable? (The SDK doesn't support 3.14 yet.) */
async function pythonUsable(python) {
  const probe = await execP(python, ['-c',
    'import sys; assert (3,10) <= sys.version_info < (3,14); import braket.devices; print("ok")']);
  return probe.code === 0 && probe.stdout.includes('ok');
}

/** Download to a file, following redirects (GitHub release assets redirect to a CDN). */
function download(url, dest, redirectsLeft = 5) {
  const https = require('https');
  return new Promise((resolve, reject) => {
    https.get(url, { headers: { 'User-Agent': 'qora-vscode' } }, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location && redirectsLeft > 0) {
        res.resume();
        resolve(download(res.headers.location, dest, redirectsLeft - 1));
        return;
      }
      if (res.statusCode !== 200) { res.resume(); reject(new Error(`HTTP ${res.statusCode} for ${url}`)); return; }
      const out = fs.createWriteStream(dest);
      res.pipe(out);
      out.on('finish', () => out.close(resolve));
      out.on('error', reject);
    }).on('error', reject);
  });
}

/** The uv release asset for this platform, or null when there is none. */
function uvAsset() {
  return {
    'win32-x64': 'uv-x86_64-pc-windows-msvc.zip',
    'win32-arm64': 'uv-aarch64-pc-windows-msvc.zip',
    'darwin-x64': 'uv-x86_64-apple-darwin.tar.gz',
    'darwin-arm64': 'uv-aarch64-apple-darwin.tar.gz',
    'linux-x64': 'uv-x86_64-unknown-linux-gnu.tar.gz',
    'linux-arm64': 'uv-aarch64-unknown-linux-gnu.tar.gz',
  }[`${process.platform}-${process.arch}`] || null;
}

/** Find a file by name anywhere under dir (uv archive layouts differ per platform). */
function findFile(dir, name) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      const hit = findFile(p, name);
      if (hit) return hit;
    } else if (entry.name === name) {
      return p;
    }
  }
  return null;
}

/** One-time setup: uv → Python 3.12 → amazon-braket-sdk, all inside the extension's storage. */
async function provisionEnv(progress) {
  const storage = extContext.globalStorageUri.fsPath;
  fs.mkdirSync(storage, { recursive: true });
  // keep uv's python downloads and caches inside our storage too — uninstalling cleans everything.
  const uvEnv = {
    ...process.env,
    UV_PYTHON_INSTALL_DIR: path.join(storage, 'pythons'),
    UV_CACHE_DIR: path.join(storage, 'uv-cache'),
  };

  const t = runL10n();
  progress.report({ message: t.stepUv });
  let uv = 'uv';
  if ((await execP(uv, ['--version'])).code !== 0) {
    const uvDir = path.join(storage, 'uv');
    const exeName = process.platform === 'win32' ? 'uv.exe' : 'uv';
    let found = fs.existsSync(uvDir) ? findFile(uvDir, exeName) : null;
    if (!found) {
      const asset = uvAsset();
      if (!asset) throw new Error(`no uv build for ${process.platform}-${process.arch}; set "qora.python" to a Python 3.10–3.13 with amazon-braket-sdk instead`);
      fs.mkdirSync(uvDir, { recursive: true });
      const archive = path.join(uvDir, asset);
      await download(`https://github.com/astral-sh/uv/releases/latest/download/${asset}`, archive);
      progress.report({ message: t.stepUnpack });
      const un = asset.endsWith('.zip')
        ? await execP('powershell', ['-NoProfile', '-Command', `Expand-Archive -Force -LiteralPath "${archive}" -DestinationPath "${uvDir}"`])
        : await execP('tar', ['-xzf', archive, '-C', uvDir]);
      if (un.code !== 0) throw new Error(`could not unpack uv: ${(un.stderr || '').slice(0, 200)}`);
      found = findFile(uvDir, exeName);
      if (!found) throw new Error('uv binary not found after unpacking');
      if (process.platform !== 'win32') { try { fs.chmodSync(found, 0o755); } catch { /* spawn will surface it */ } }
    }
    uv = found;
  }

  const envDir = path.join(storage, 'braket-env');
  const python = path.join(envDir, process.platform === 'win32' ? 'Scripts' : 'bin',
    process.platform === 'win32' ? 'python.exe' : 'python');

  // stream uv's live output into the progress message so the two long steps visibly move instead of
  // sitting on a static label for minutes. uv prints plain per-package lines to a pipe (no TTY = no
  // ANSI): "Resolved 40 packages…", "Prepared…", "Installed 40 packages in 8s".
  const live = (label) => (l) => progress.report({ message: `${label} · ${l}` });

  progress.report({ message: t.stepPython });
  let r = await execP(uv, ['venv', envDir, '--python', '3.12'], { env: uvEnv }, undefined, live(t.stepPython));
  if (r.code !== 0) throw new Error(`Python install failed: ${(r.stderr || '').slice(-300)}`);

  progress.report({ message: t.stepBraket });
  r = await execP(uv, ['pip', 'install', '--python', python, 'amazon-braket-sdk'], { env: uvEnv }, undefined, live(t.stepBraket));
  if (r.code !== 0) throw new Error(`Braket SDK install failed: ${(r.stderr || '').slice(-300)}`);

  progress.report({ message: t.stepVerify });
  if (!(await pythonUsable(python))) throw new Error('the provisioned Python failed verification');
  return python;
}

/**
 * A python that can run Braket — resolving through override → remembered → consent → probe/provision.
 * CRITICAL for latency: everything BEFORE the consent dialog is a cheap synchronous check (settings,
 * a file-exists test). NO process is spawned until AFTER the user consents, so the dialog appears
 * instantly on click. Probing system pythons and provisioning happen under the progress bar, later.
 */
async function ensureBraketPython() {
  if (braketPythonCache) return braketPythonCache;
  const t = runL10n();

  // (1) explicit override — trust the configured path; a bad one surfaces at run time.
  const override = (vscode.workspace.getConfiguration('qora').get('python') || '').trim();
  if (override) return (braketPythonCache = override);

  // (2) a previously chosen/provisioned env — trust its existence (a cheap fs check, NO spawn).
  const remembered = extContext.globalState.get('qora.braketPython');
  if (remembered && fs.existsSync(remembered)) return (braketPythonCache = remembered);

  // (3) nothing known yet → ask consent NOW, before spawning anything, so the dialog is instant.
  const pick = await vscode.window.showInformationMessage(
    t.setupTitle,
    { modal: true, detail: t.setupDetail },
    t.setupButton);
  if (pick !== t.setupButton) return null;

  // (4) only after consent do we spawn: prefer a usable system python (skips the download), else provision.
  try {
    return await vscode.window.withProgress(
      { location: vscode.ProgressLocation.Notification, title: t.progressTitle },
      async (progress) => {
        progress.report({ message: t.stepProbe });
        for (const candidate of process.platform === 'win32' ? ['python', 'py'] : ['python3', 'python']) {
          if (await pythonUsable(candidate)) return (braketPythonCache = candidate);
        }
        const python = await provisionEnv(progress);
        await extContext.globalState.update('qora.braketPython', python);
        return (braketPythonCache = python);
      });
  } catch (e) {
    vscode.window.showErrorMessage(t.setupFailed(e.message || e));
    return null;
  }
}

async function runProgram() {
  const editor = vscode.window.activeTextEditor;
  if (!editor || editor.document.languageId !== 'qora') {
    vscode.window.showInformationMessage(runL10n().openFirst);
    return;
  }
  const document = editor.document;

  // Resolve the runner FIRST — this shows the (instant) setup dialog when needed, before the compile
  // spawn. So clicking Run pops the dialog immediately instead of after compile + probe latency.
  const python = await ensureBraketPython();
  if (!python) return;

  const { result, error } = await runQora(document.getText(), docArgs(document));
  if (error) { warnInfraOnce(error); return; }
  if (!result.success) {
    const first = (result.errors && result.errors[0] && result.errors[0].message) || 'parse failed';
    vscode.window.showErrorMessage(runL10n().cannotRun(first));
    return;
  }

  const shots = vscode.workspace.getConfiguration('qora').get('shots', 1000);
  const name = path.basename(document.fileName);
  const reply = await vscode.window.withProgress(
    { location: vscode.ProgressLocation.Window, title: runL10n().running(name) },
    () => execP(python, [path.join(runnerDir(), 'braket_run.py'), '--shots', String(shots)],
      { cwd: runnerDir() }, result.qasm));

  let parsed;
  try { parsed = JSON.parse(reply.stdout.trim().split('\n').pop()); }
  catch { parsed = { error: (reply.stderr || reply.stdout || 'no output from the runner').slice(0, 300) }; }
  if (parsed.error) {
    vscode.window.showErrorMessage(runL10n().runFailed(parsed.error));
    return;
  }
  renderHistogram(name, parsed.counts, parsed.shots);
}

/** Measurement histogram in the "Qora Run" output channel (append-only, like a lab notebook). */
function renderHistogram(name, counts, shots) {
  if (!runChannel) {
    runChannel = vscode.window.createOutputChannel('Qora Run');
    extContext.subscriptions.push(runChannel);
  }
  const total = Object.values(counts).reduce((a, b) => a + b, 0) || 1;
  const width = 40;
  runChannel.appendLine('');
  runChannel.appendLine(`${name} — ${shots} shots (Braket LocalSimulator, ${new Date().toLocaleTimeString()})`);
  for (const [key, n] of Object.entries(counts).sort((a, b) => b[1] - a[1])) {
    const bar = '█'.repeat(Math.max(1, Math.round(width * n / total)));
    runChannel.appendLine(`  ${key}  ${bar}  ${n}  (${(100 * n / total).toFixed(1)}%)`);
  }
  runChannel.show(true);
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
  const { result, error } = await runQora(document.getText(), docArgs(document));
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

  const { result, error } = await runQora(editor.document.getText(), docArgs(editor.document));
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
  const { result, error } = await runQora(document.getText(), ['--stages', ...docArgs(document)]);
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
      new vscode.CodeLens(range, { title: '▶ 실행', command: 'qora.run' }),
      new vscode.CodeLens(range, { title: 'OpenQASM으로 변환', command: 'qora.transpile' }),
      new vscode.CodeLens(range, { title: '컴파일 단계 보기', command: 'qora.showStages' })
    );
  }
  return lenses;
}

// Import-path completion: inside `import "…"`, offer the sibling .qor files and folders so you pick
// the file instead of typing it (like JS/TS import-path completion). The one import form is a quoted
// relative path resolved against the importing file's directory, so completion only fires in that string.
function provideImportPaths(document, position) {
  const uptoCursor = document.lineAt(position).text.slice(0, position.character);
  const m = /(?:^|\s)import\s+"/.exec(uptoCursor);
  if (!m) return undefined;
  const afterQuote = uptoCursor.slice(m.index + m[0].length);
  if (afterQuote.includes('"')) return undefined;    // string already closed before the cursor
  const typed = afterQuote;

  const docPath = document.uri.scheme === 'file' ? document.uri.fsPath : undefined;
  if (!docPath) return undefined;                    // unsaved buffer: no directory to resolve against
  const baseDir = path.dirname(docPath);

  // The typed text may include subdirectories: split into a dir part (to list) and a prefix (to filter).
  const slash = typed.lastIndexOf('/');
  const dirPart = slash >= 0 ? typed.slice(0, slash) : '';
  const prefix = slash >= 0 ? typed.slice(slash + 1) : typed;
  let listDir;
  try { listDir = path.resolve(baseDir, dirPart); } catch { return undefined; }

  let entries;
  try { entries = fs.readdirSync(listDir, { withFileTypes: true }); }
  catch { return undefined; }                        // folder doesn't exist yet — nothing to offer

  // Replace only the segment after the last slash, so subdirectory navigation stays clean.
  const replaceRange = new vscode.Range(position.translate(0, -prefix.length), position);
  const self = path.basename(docPath);
  const items = [];
  for (const e of entries) {
    if (e.name.startsWith('.')) continue;
    if (e.isDirectory()) {
      const it = new vscode.CompletionItem(e.name + '/', vscode.CompletionItemKind.Folder);
      it.insertText = e.name + '/';
      it.range = replaceRange;
      it.sortText = '0' + e.name;                    // folders first
      it.command = { command: 'editor.action.triggerSuggest', title: '' }; // keep completing into it
      items.push(it);
    } else if (e.isFile() && e.name.toLowerCase().endsWith('.qor')) {
      if (dirPart === '' && e.name === self) continue; // importing yourself is just a cycle (QSEM021)
      const it = new vscode.CompletionItem(e.name, vscode.CompletionItemKind.File);
      it.insertText = e.name;
      it.range = replaceRange;
      it.sortText = '1' + e.name;
      items.push(it);
    }
  }
  return items;
}

function activate(context) {
  extRoot = context.extensionPath;   // used to locate the bundled per-platform binary
  extContext = context;              // globalStorage/globalState for the Run provisioning

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

  // Path autocomplete inside `import "…"` — trigger on the opening quote and on `/` for subfolders.
  context.subscriptions.push(
    vscode.languages.registerCompletionItemProvider(
      'qora',
      { provideCompletionItems: provideImportPaths },
      '"', '/'
    )
  );

  diagnostics = vscode.languages.createDiagnosticCollection('qora');
  context.subscriptions.push(diagnostics);

  statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
  context.subscriptions.push(statusBarItem);

  context.subscriptions.push(
    vscode.commands.registerCommand('qora.run', runProgram),
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
