**English** · [한국어](https://github.com/AJ-comp/Qora/blob/main/vscode/README.ko.md) · [日本語](https://github.com/AJ-comp/Qora/blob/main/vscode/README.ja.md) · [Tiếng Việt](https://github.com/AJ-comp/Qora/blob/main/vscode/README.vi.md)

# Qora Language

**Qora** is a small quantum toy language built on the [Janglim](https://www.nuget.org/packages/Janglim)
parser engine. You write circuits in a Q#/C#-flavored syntax and it transpiles to **OpenQASM 3**. This
extension gives `.qor` files syntax highlighting, hover docs, **live error diagnostics**, and a
**transpile** command, and local execution with typed entry-return results.

Extension **0.36.0** bundles **Qora 0.43.0**, which adds the built-in math functions `Sqrt` and `ArcCos`: `Ry(2.0 * ArcCos(0.6), q[0])` writes a rotation angle from an amplitude. The run log's compile summary now counts only gate statements. Since Qora 0.42.0, operations can return `bit[]`, and Run shows one ordered list per shot—even a single bit is displayed as `[1]`, not `1`.
See the [extension release notes](CHANGELOG.md#0360--2026-11-22) and
[language release notes](https://github.com/AJ-comp/Qora/blob/main/CHANGELOG.md#0430--2026-11-22).

## Features

- **Syntax highlighting** — keywords (`operation`/`use`/`new`/`if`/`for`/…), types (`Qubit`/`bool`/`int`/`float`/`bit`/`angle`), gates (`H`/`CNOT`/`Rx`/…), `pi`, numbers, operators
- **Hover docs** — hover a gate or keyword for a short description (e.g. `Rx`, `CNOT`, `M`)
- **Live parse errors** — the parser runs as you type and underlines the offending token
- **Status bar feedback** — the active `.qor` file shows `Qora: OK`, an error count, or parser status
- **Main CodeLens actions** — `operation Main()` gets quick links for OpenQASM transpile and compilation stages
- **Getting Started walkthrough** — install the extension and follow the Qora guide from VS Code's Getting Started page
- **Editor title actions** — open a `.qor` file and use the top-right buttons to transpile, inspect stages, or open an example
- **Examples on demand** — run **`Qora: Open Example`** to open `demo.qor`, or **`Qora: New Bell Example`** to start from a scratch Bell circuit
- **Run your program** — **`Qora: Run Program`** (or the ▶ CodeLens above `Main`) executes the file on Amazon Braket's local simulator and groups the entry operation's return values by frequency. A `void` entry reports successful execution and the shot count, without a histogram. First run sets the simulator up automatically (one-time ~200 MB download into the extension's storage — you install nothing yourself).
- **Transpile to OpenQASM** — run **`Qora: Transpile to OpenQASM`** from the Command Palette; the result opens beside your file
- **Show compilation stages** — run **`Qora: Show Compilation Stages`** to follow AST, source-shaped HIR, SSA/CFG MIR and its analyses, the typed OpenQASM target, and final OpenQASM; refreshes on save
- **Snippets** — `operation`, `main`, `use`, `measure`, `for`, `if`, `bell`
- **Bracket matching / auto-closing**

## Execution results

The Run command displays only explicit entry return values: `bool`, `bit`, `bit[]`, `int`, `float`, or `angle` (radians).
For a `bit[]`, each shot returns one ordered list (e.g. `[1, 0]`); its length must be known when exporting OpenQASM.
`bool` results display as `true`/`false`, while measurement-oriented `bit` results remain `0`/`1`.
For example, `operation Main(): bit { use q = Qubit[1]; H(q[0]); return M(q[0]); }` returns one bit per shot.
Measurements stored in local variables and helper return values are not additional outputs, and Run does not add a final measurement.
Large integers remain exact decimal values. A custom compiler must provide the current `entryResult` metadata; older compiler bundles must be updated rather than falling back to internal measurement counts.

## Known simulator limitations

The standard setup pins Braket SDK 1.127.8 and default simulator 1.43.1; it does not include the pending
upstream fixes used in local verification. The pinned simulator passes 72 of 81 execution cases in Qora 0.43.0,
including the bit-array return case. The same nine cases failed in 0.41.0 and 0.42.0; in 0.41.0, all 80 passed with four Braket patches combined. Boolean arrays and other affected paths therefore
remain restricted in the standard setup. Measuring `Qubit[]` helper-parameter elements and some
noncontiguous controlled-gate layouts remain unresolved even with those patches. See the
[backend limitations](https://github.com/AJ-comp/Qora/blob/main/CHANGELOG.md#known-execution-limitations)
before relying on affected programs; successful compilation alone does not guarantee simulator correctness.

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
