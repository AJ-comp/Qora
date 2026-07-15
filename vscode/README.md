**English** · [한국어](https://github.com/AJ-comp/Qora/blob/main/vscode/README.ko.md) · [日本語](https://github.com/AJ-comp/Qora/blob/main/vscode/README.ja.md) · [Tiếng Việt](https://github.com/AJ-comp/Qora/blob/main/vscode/README.vi.md)

# Qora Language

**Qora** is a small quantum toy language built on the [Janglim](https://www.nuget.org/packages/Janglim)
parser engine. You write circuits in a Q#/C#-flavored syntax and it transpiles to **OpenQASM 3**. This
extension gives `.qor` files syntax highlighting, hover docs, **live error diagnostics**, and a
**transpile** command.

## Features

- **Syntax highlighting** — keywords (`operation`/`use`/`new`/`if`/`for`/…), types (`Qubit`/`int`/`float`/`bit`/`angle`), gates (`H`/`CNOT`/`Rx`/…), `pi`, numbers, operators
- **Hover docs** — hover a gate or keyword for a short description (e.g. `Rx`, `CNOT`, `M`)
- **Live parse errors** — the parser runs as you type and underlines the offending token
- **Status bar feedback** — the active `.qor` file shows `Qora: OK`, an error count, or parser status
- **Main CodeLens actions** — `operation Main()` gets quick links for OpenQASM transpile and compilation stages
- **Getting Started walkthrough** — install the extension and follow the Qora guide from VS Code's Getting Started page
- **Editor title actions** — open a `.qor` file and use the top-right buttons to transpile, inspect stages, or open an example
- **Examples on demand** — run **`Qora: Open Example`** to open `demo.qor`, or **`Qora: New Bell Example`** to start from a scratch Bell circuit
- **Run your program** — **`Qora: Run Program`** (or the ▶ CodeLens above `Main`) executes the file on a real quantum simulator (Amazon Braket local) and shows the measurement histogram. First run sets the simulator up automatically (one-time ~200 MB download into the extension's storage — you install nothing yourself).
- **Transpile to OpenQASM** — run **`Qora: Transpile to OpenQASM`** from the Command Palette; the result opens beside your file
- **Show compilation stages** — run **`Qora: Show Compilation Stages`** to see the pipeline live: AST → QoraIR → synthesized inverse IR (for `Adjoint`) → OpenQASM; refreshes on save
- **Snippets** — `operation`, `main`, `use`, `measure`, `for`, `if`, `bell`
- **Bracket matching / auto-closing**

## The parser is bundled

Live errors and transpile are powered by the Qora parser (.NET), which ships **inside the extension as a
self-contained binary** — no separate .NET install needed. Per-platform builds are included (Windows x64 /
macOS Apple Silicon / Linux x64) and the extension runs the right one automatically.

> On an unsupported platform, highlighting / hover / snippets still work and only errors / transpile are
> off. You can then point `qora.command` (a Qora executable) or `qora.args` (+ `dotnet` on a `Qora.dll`)
> at your own build.

## Settings

| Setting | Default | Description |
|---|---|---|
| `qora.command` | *(empty → use the bundled parser)* | Override with a Qora executable (advanced) |
| `qora.args` | `[]` | Extra args for `qora.command` (e.g. a `Qora.dll` path) |

## License

MIT — source at [github.com/AJ-comp/Qora](https://github.com/AJ-comp/Qora).
