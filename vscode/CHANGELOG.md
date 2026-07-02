# Changelog

All notable changes to the Qora Language extension.

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
