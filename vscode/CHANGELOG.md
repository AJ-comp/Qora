# Changelog

All notable changes to the Qora Language extension.

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
