# Changelog

All notable changes to the Qora Language extension.

## 0.7.1

- Polished the Run setup experience: the one-time consent dialog now uses a proper title + detail
  layout (what gets installed, where, how long), and the whole Run flow — dialog, progress steps,
  errors — speaks your VS Code display language (English / 한국어 / 日本語 / Tiếng Việt).

## 0.7.0

- **Run programs inside the editor**: new **`Qora: Run Program`** command (+ a ▶ CodeLens above
  `Main` and an editor-title button) compiles the file and executes it on Amazon Braket's local
  simulator — the engine measured to run Qora's FULL output (operations, loops, classical
  variables, `while`) — then shows the measurement histogram in the "Qora Run" panel. No manual
  installs: on first run the extension provisions a private Python + Braket SDK into its own
  storage (one-time ~200 MB download; a suitable system Python is used instead when present;
  `qora.python` overrides, `qora.shots` sets the shot count).
- Bundles the **Qora v0.13** compiler: `const` is now enforced as an immutable binding (QSEM024 —
  it may hold a measurement result, but reassigning it is an error with a fix-it hint), bit
  conditions emit as `r == true` (the spelling Qiskit's importer accepts, so measure-then-branch
  programs load there), emitted QASM records its compiler version, and every diagnostic carries a
  source span.

## 0.6.0

- **Precise error squiggles**: every semantic error (QSEM001-023) now underlines the exact offending
  statement or name instead of the whole document — wrong call, out-of-range index, duplicate
  parameter, failing `import`/`open` line, and so on.
- Bundles the **Qora v0.12** compiler: the full module system - `namespace` / `open` / qualified calls
  (`MyLib.Bell(q)`) resolve for real (C#/Q#-style rules; ambiguity and unknown-name errors
  QSEM018/019/022/023), and `import` loads real files: `import gates_lib;` reads `gates_lib.qor` next to
  the importing file, `import lib.gates;` maps dots to directories, `import "a b.qor";` takes a
  literal path. Missing files are QSEM020; import cycles are QSEM021 with the full chain shown.
- The extension now passes the document's directory to the compiler (`--base-dir`), so imports
  resolve live while you type - including in unsaved buffers. Untitled documents report a clear
  error on `import`.
- Emitted OpenQASM now name-mangles every user-defined name with a trailing `_` (namespaces flatten
  as `MyLib.Bell` -> `MyLib__Bell_`), making collisions with OpenQASM keywords and stdgates names
  structurally impossible - declarations like `bit s = M(q[0]);` now compile. The stages panel keeps
  showing your original names; only the QASM output shows the mangled ones.

## 0.5.5

- Added a Qora status bar item for the active `.qor` file: checking, OK, parse-error count, or parser
  unavailable.
- Added CodeLens actions above `operation Main()` for quick OpenQASM transpile and compilation-stage
  inspection.
- Changed **`Qora: Open Example`** to open the bundled `examples/demo.qor` file directly, with a scratch
  fallback when the file is unavailable.

## 0.5.4

- Added a polished first-run path: a Getting Started walkthrough, editor-title actions for Qora files,
  and quick commands to open the bundled demo or create a fresh Bell example.
- Added `//` line-comment highlighting and VS Code line-comment toggling for Qora files.
- Updated the marketplace README with the new onboarding entry points.

## 0.5.3

- Fixed the language-switcher links (한국어 / 日本語 / Tiếng Việt) on the marketplace page: the
  marketplace rewrites relative README links against the repository ROOT, ignoring the extension's
  `vscode/` subdirectory, so they 404'd — now absolute GitHub URLs.

## 0.5.2

- Bundles the **Qora v0.11** compiler (Janglim 0.3.0-preview.1): hardened validation — argument kinds
  for built-in gates, full user-operation call signatures, index bounds (QSEM016), measure-into-bit
  (QSEM017) — and QSEM013 relaxed so def-local names like `Qubit t` are valid again. The module-system
  grammar (`import` / `namespace` / `open`) parses and reports an in-progress error until resolution
  lands. String literals now lex.
- Stages panel: the inverse-IR column shows the same transitive, uniquified inverse defs as the QASM;
  fixed a race where a slow refresh could render stale or wrong-document content.

## 0.5.1

- New marketplace icon — the Qora "Q orbital" mark (matches the project docs).

## 0.5.0

- **"Qora: Show Compilation Stages" command** — opens a side panel showing how the current file moves
  through the compiler: AST → QoraIR → (synthesized inverse IR, when `Adjoint` is used) → OpenQASM 3.
  The panel refreshes on save; the heavy payload is fetched only when you ask (keystroke diagnostics
  stay on the lean `--json` contract).
- Bundles the compiler with the new **semantic-validation pass**: invalid programs now fail with
  QSEM001–QSEM015 errors (shown as squiggles) instead of silently emitting broken OpenQASM —
  non-invertible `Adjoint`, calls in expressions, wrong argument counts/shapes, reserved names,
  recursion, `use` misplacement, and more.
- **Whole-operation `Adjoint`** — `Adjoint Foo(q)` on a user operation now compiles to a synthesized
  inverse subroutine (`Foo__adj`), covering gates, `for` (reversed), `if`, classical declarations, and
  nested calls transitively.
- Grammar: zero-argument functor calls (`Adjoint Nop();`) and unary minus (`Rx(-pi/2, q[0]);`).

## 0.4.0

- Bundles the **Qora v0.9** parser (Janglim `0.2.0-preview.3`), so live errors and transpile now
  understand the newer language features:
  - single-gate functors (`Adjoint G(...)` / `Controlled G(...)`), richer conditions
    (`!= < <= > >= && || !`), `if` / `else` / `else if`, and first-class `Reset` / `ResetAll`;
  - **`//` line comments** — recognized and dropped before parsing, while a lone `/` still lexes as
    division. (Block `/* */` comments are still pending engine support.)

## 0.3.1

- Patch release (bundled-parser bring-up across win32-x64 / darwin-arm64 / linux-x64).

## 0.3.0

- **Bundled parser** — the Qora parser now ships inside the extension as a per-platform, self-contained
  binary (Windows x64, macOS Apple Silicon, Linux x64). Live errors and transpile work with no .NET
  install and no configuration.
- `qora.command` now defaults to empty (use the bundled parser). Set it to override with a custom
  executable, or use `qora.args` + `dotnet` to run a `Qora.dll`.
- Renamed from **Ket** to **Qora** (the previous name was taken): language id `qora`, file extension
  `.qor`, commands/settings under `qora.*`.

## 0.2.0

- **Live parse-error diagnostics (squiggles)** — the parser runs as you type and underlines the
  offending token, backed by the Janglim engine's positioned errors.
- **`Qora: Transpile to OpenQASM`** command — emits the current file as OpenQASM 3 in a side editor.

## 0.1.0

- Initial release: TextMate syntax highlighting, Korean hover docs for gates/keywords, snippets,
  bracket matching, and `.qor` file association.
