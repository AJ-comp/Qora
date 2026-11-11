**English** · [한국어](README.ko.md) · [日本語](README.ja.md)

<div align="center">

<img src="docs/images/quantum-language-icon-128.png" alt="Qora" width="128" height="128">

# Qora

**A learning language for quantum programming.**
Write quantum circuits in Q#/C#-like syntax and emit verified **OpenQASM 3**.

[![VS Code Marketplace](https://img.shields.io/visual-studio-marketplace/v/qora-lang.qora-language?style=flat-square&label=VS%20Code&labelColor=111827&color=6b3fd4)](https://marketplace.visualstudio.com/items?itemName=qora-lang.qora-language)
[![Docs](https://img.shields.io/badge/docs-GitHub%20Pages-111827?style=flat-square&labelColor=111827&color=6b3fd4)](https://aj-comp.github.io/Qora/)
[![Built on Janglim](https://img.shields.io/badge/built%20on-Janglim%200.6.0-111827?style=flat-square&labelColor=111827)](https://www.nuget.org/packages/Janglim/0.6.0)
[![Emits OpenQASM 3.0](https://img.shields.io/badge/emits-OpenQASM%203.0-111827?style=flat-square&labelColor=111827)](https://openqasm.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-111827?style=flat-square&labelColor=111827)](LICENSE)

</div>

[Documentation](#documentation) · [Quick start](#quick-start) · [Compiler pipeline](#how-the-compiler-works) · [Roadmap](#roadmap--releases)

Current versions: **Qora 0.40.0** and **VS Code extension 0.33.0**.
See the [release notes and migration guide](CHANGELOG.md#0400--2026-11-11) for the new return-result
contract, separate `bool`/`bit` types, and known backend restrictions.

---

**Qora is a learning language for quantum programming.** Quantum code is often written through Python
libraries such as Qiskit or Cirq, or through research languages that assume you already know the theory.
For a learner, it can be hard to move between quick experiments and precise concepts.

Qora focuses on bridging that gap. Write a circuit with familiar programming syntax, and the compiler
first checks whether the program describes something a quantum computer can actually do.
Then it produces standard **OpenQASM 3** that you can inspect line by line or pass to real toolchains.

That direction carries into the design:

- **Errors come before output.** If a program compiles, its OpenQASM is valid.
- **The compiler internals are inspectable.** One command shows source, AST, source-shaped HIR,
  symbol facts, SSA/CFG MIR, quantum-effect summaries, and QASM.
- **Quantum rules are checked by the compiler.** Using the same qubit as both control and target,
  using a moved binding, or passing a register where a single qubit is expected produces a diagnostic.

## Documentation

Start with the [documentation portal](https://aj-comp.github.io/Qora/) to choose a track.
The book and developer manual have matching **English · 한국어 · 日本語** editions.
The architecture overview is available in all three languages; its detailed datasheet, IR-pass notes,
and uncomputation course are currently available in Korean.

| Track | What to read | Online | Repository source |
|---|---|---|---|
| **Compiler developer manual** | Follow `Next` and `Work` through the actual C# implementation, from HIR to MIR and CFG. | [Read the manual](https://aj-comp.github.io/Qora/developer/en/index.html) | [HTML contents](docs/developer/en/index.html) |
| **Learning Quantum Gates with Qora** | Learn qubit states, amplitudes, interference, entanglement, and gates through Qora examples. | [Read the book](https://aj-comp.github.io/Qora/book/en/index.html) | [HTML contents](docs/book/en/index.html) |
| **Compiler architecture** | Read the pipeline overview; use the Korean edition for detailed ownership, pass, and target contracts. | [Read the reference](https://aj-comp.github.io/Qora/architecture/en/index.html) | [HTML contents](docs/architecture/en/index.html) |

The [Korean compiler datasheet](https://aj-comp.github.io/Qora/architecture/ko/datasheet.html)
([source](docs/architecture/ko/datasheet.html)) is a separate reference linked from the architecture overview.

### HIR → MIR implementation walkthrough

The developer manual currently has detailed chapters **9–17**; the other chapters are an outline for future writing.
Chapter 10's detailed flows pair Qora examples with C# explanations and before/after state trees.

- [Chapter 9: HIR → MIR boundary](https://aj-comp.github.io/Qora/developer/en/chapter-09.html) · [source](docs/developer/en/chapter-09.html)
- [Chapter 10: Build a MIR callable](https://aj-comp.github.io/Qora/developer/en/chapter-10.html) · [source](docs/developer/en/chapter-10.html)
- [Detailed function flows](https://aj-comp.github.io/Qora/developer/en/chapter-10-call-flow.html#lower-to-mir-callable) · [source](docs/developer/en/chapter-10-call-flow.html#lower-to-mir-callable) — callable construction, parameters, statements, and block termination.
- [`CloseCallableBody()`: finish the callable body](https://aj-comp.github.io/Qora/developer/en/chapter-10-call-flow.html#close-callable-body) · [source](docs/developer/en/chapter-10-call-flow.html#close-callable-body)
- [Chapters 11–17: values, storage, qubit versions, branches, snapshots, and CFG](https://aj-comp.github.io/Qora/developer/en/index.html#c11) · [contents source](docs/developer/en/index.html#c11)

Use the “View detailed flow” regions to enter a helper and the caller-return buttons to return.
The example code and state comparison stay together within each callable's explanation.

### Read local documentation

The online links show the deployed site. Changes in your checkout are visible locally before they are deployed.
GitHub displays repository HTML links as source; use a local browser to read the rendered pages.

From the repository root, with Python 3 installed:

```bash
python -m http.server 8000 --bind 127.0.0.1 --directory docs
```

Keep that terminal running, then open:

- [Documentation portal](http://127.0.0.1:8000/index.html)
- [Compiler developer manual](http://127.0.0.1:8000/developer/en/index.html)
- [Chapter 10](http://127.0.0.1:8000/developer/en/chapter-10.html)
- [Detailed function flows](http://127.0.0.1:8000/developer/en/chapter-10-call-flow.html#lower-to-mir-callable)

This command serves the whole `docs/` directory. The book, developer manual, and architecture reference share the same server.
Stop it with `Ctrl+C`.

## A taste

<table>
<tr>
<th>Qora</th>
<th>emitted OpenQASM 3</th>
</tr>
<tr>
<td>

```
operation Bell(q: Qubit[])
{
    H(q[0]);
    CNOT(q[0], q[1]);
}

operation Main()
{
    use q = Qubit[2];
    Bell(q);
    var r: bit = M(q[0]);
}
```

</td>
<td>

```
// generated by Qora v0.40.0
OPENQASM 3;
include "stdgates.inc";

def Bell__sz2(qubit[2] q) {
  int v0;
  int v1;
  int v2;
  v0 = 0;
  h q[v0];
  v1 = 0;
  v2 = 1;
  cx q[v1], q[v2];
}

qubit[2] q;
int v0;
bit v1;
Bell__sz2(q);
v0 = 0;
v1 = measure q[v0];
```

</td>
</tr>
</table>

The output includes MIR temporaries such as `v0` and `v1`; the source binding `r` is represented by `v1`
in this example. Callable and storage names are retained when safe for OpenQASM. When a retained name
collides with a keyword, a built-in gate, or another output name, target lowering assigns a safe name
and emits a `// Qora:` note explaining the change.

## Quick start

The fastest path is to start with the VS Code extension.
The compiler is bundled inside the extension, so there is **nothing else to install**, and neither .NET nor Python is required.

1. Install [Qora Language](https://marketplace.visualstudio.com/items?itemName=qora-lang.qora-language) from the VS Code Marketplace.
2. Create a file named `bell.qor` and paste the example above.
3. Save the file and the features below are ready to use.

- **As you type**, errors are underlined, and hovering the underline shows the reason. Try `CNOT(q[0], q[0])`.
- Hover a gate or keyword to see a short note for items such as `H`, `use`, and `M`.
- **`Ctrl+Shift+P` → `Qora: Transpile to OpenQASM`** opens the compiled output beside your file.
- **`Qora: Show Compilation Stages`** shows source → AST → HIR/symbols → SSA/CFG MIR/quantum effects → QASM every time you save.

To run the repository directly, you only need the .NET 10 SDK.

```bash
git clone https://github.com/AJ-comp/Qora.git
cd Qora
dotnet run --project src/Qora          # parses a demo program, prints its OpenQASM
```

To compile your own file or inspect its stages:

```bash
dotnet run --project src/Qora -- --json bell.qor
dotnet run --project src/Qora -- --json --stages bell.qor
```

The VS Code extension uses the same `--json` interface. Each reply contains `{success, qasm, errors[]}`;
`--stages` also includes AST, HIR, symbols, MIR, and quantum-effect summaries.
Without a file path, `--json` reads the source from standard input.

## The language in five minutes

**Qubits and gates.** A new qubit register is allocated with `use q = Qubit[N];` and addressed like an array.
Here, `use` is not a C# `using`-style automatic cleanup marker; it is the syntax that creates a qubit register for the Qora program.
Helper operations accept a register as `Qubit[]` and can read the caller's concrete length through `.Count`.
Gate calls have the same shape as function calls.

```
use q = Qubit[3];              // allocate qubit register q, size 3
H(q[0]);                       // superposition
CNOT(q[0], q[1]);              // entangle
Rx(pi/2, q[2]);                // rotations take the angle first
Reset(q[2]);                   // back to |0⟩
```

Built-in gates: `H X Y Z S T` · `CNOT/CX CY CZ SWAP CCX` · `Rx Ry Rz` · `Reset ResetAll`.

**Measurement** with `M` stores a qubit's result in a classical `bit`.
A `bit` can also be initialized with the literals `0` or `1`; measurement is how quantum information becomes a classical result.

```
var r: bit = M(q[0]);          // measure, irreversible
r = M(q[0]);                   // re-measure into the same bit
```

**Classical control** looks exactly like you expect, and can react to measurements:

```
const n: int = 2;
var count = 0;
for i in 0..n {  H(q[i]);  }
if (r == 1) {  X(q[1]);  } else {  Z(q[1]);  }
while (count < 2) {  count = count + 1;  }
repeat {  H(q[0]);  r = M(q[0]);  } until (r == 1);
```

**Classical arrays** use the familiar `T[]` form for `bool`, `int`, `float`, `bit`, and `angle` values.
Declare them anywhere a scalar goes — `Main`, helper operations, loops, branches (the compiler handles
OpenQASM's global-scope rule automatically) — then read or update elements by index and use `.Count` when iterating.

```qora
var counts: int[] = [1, 2, 3];
var weights: float[] = new float[3];   // three zero-initialized elements
weights[1] = 0.5;
for i in 0..counts.Count-1 { counts[i] = counts[i] + 1; }
```

`float` values cannot be implicitly converted to `int`: `var i: int = 0.5;` is a compile error,
as is `var i: int = 1.0;`. The same rule applies to reassignment, array elements, callable arguments,
and return values. `var i = 0.5;` instead infers `float`, and `int` → `float` remains allowed.

**Functions** compute classical values. A `function` is pure and returns a value usable in expressions.
It may not allocate qubits, apply gates, measure, or call an operation. An `operation` may perform quantum
work and may also declare a scalar return type: `bool`, `int`, `bit`, `float`, or `angle`.

```qora
operation Read(q: Qubit): bit { return M(q); }
operation Main(): bit {
    use q = Qubit[1];
    H(q[0]);
    return Read(q[0]);
}
```

Each execution shot exposes **only the entry operation's returned value**. A helper's return goes to its
caller, not directly to the result display. Omitting the return type means no result: internal measurements
are not displayed automatically, and the runner never appends a measurement. Such an operation may use
`return;` for an early exit. A typed operation must return a compatible value on every path.
Array and tuple return types are not supported; `angle` results are reported in radians.

Arithmetic expressions accept nested parentheses: `(1 + 2) * 3` evaluates to `9`, while
`1 + 2 * 3` evaluates to `7`. Groups also work in returns, call arguments, array indices,
and loop bounds. Comparisons (`==`, `!=`, `<`, `<=`, `>`, `>=`) produce `bool` values:

```qora
function IsPositive(value: int): bool { return value > 0; }
operation Main(): bool {
    var isEqual: bool = 1 + 2 == 3;
    isEqual = (1 + 2) * 3 == 9;
    return isEqual == IsPositive(3);
}
```

Comparison values can be stored, passed as arguments, or returned; omitting the variable's type infers
`bool`. Arithmetic binds before `<`, `<=`, `>`, `>=`, which bind before `==`, `!=`. Logical operators
(`!`, `&&`, `||`) remain condition-only syntax. Existing measurement-placement restrictions still apply.

`bool` stores logical `true`/`false`; `bit` stores measurement values `0`/`1`. They are separate types:
there is no implicit conversion between `bool` and `bit`, `int`, `float`, or `angle`, in either direction.
Initializers, assignments, array elements, arguments, and returns follow this same rule.
Conditions accept only `bool`: use `if (r == 1)` for a measured bit or `if (count != 0)` for an integer,
not `if (r)` or `if (count)`. Boolean equality compares booleans, while ordering comparisons require numbers.
Execution results preserve this distinction: a returned `bool` is `true`/`false`, not the `0`/`1` of a returned `bit`.

```qora
function angleOf(k: int): angle { return pi / k; }

operation Main()
{
    use q = Qubit[1];
    Rx(angleOf(4), q[0]);       // the function result is the rotation angle
    var r: bit = M(q[0]);
}
```

User-defined operations are written as a name, parameters, and a block; `Main` is the entry point.
The surface **`Controlled` functor** adds a control qubit to a built-in gate:

```qora
operation Main()
{
    use q = Qubit[2];
    H(q[0]);
    Controlled X(q[0], q[1]);
}
```

`Adjoint` is not Qora source syntax and does not exist in HIR or MIR today. Automatic uncomputation is
planned as a future MIR scheduler that will analyze exact SSA/CFG facts and generate the required cleanup.

**Namespaces and imports** let large programs be split across multiple files.
`import` uses a quoted file path, like Python, including the extension.

`gates_lib.qor`

```qora
namespace GatesLib
{
    operation Bell(q: Qubit[])
    {
        H(q[0]);
        CNOT(q[0], q[1]);
    }
}
```

`rotations.qor`

```qora
namespace Rotations
{
    operation Tilt(q: Qubit)
    {
        Ry(pi/4, q);
        Rz(pi/8, q);
    }
}
```

`main.qor`

```qora
import "gates_lib.qor";         // load gates_lib.qor from the same directory
import "rotations.qor";         // load rotations.qor from the same directory

namespace App
{
    open GatesLib;
    open Rotations;

    operation Prepare(q: Qubit[])
    {
        Bell(q);                // unqualified through open
        Tilt(q[0]);             // unqualified through open
    }
}

operation Main()
{
    use q = Qubit[2];
    App.Prepare(q);             // qualified call into App
    Rotations.Tilt(q[1]);       // direct qualified call into another imported namespace
}
```

Name resolution is similar to the pattern used by familiar programming languages with namespaces.
Qora looks in the current namespace, then the global namespace, then the `open`ed namespaces.
If two `open`ed namespaces contain the same name, Qora does not silently pick one; it reports a compile error with the candidates.

`import "lib/gates.qor";` loads `lib/gates.qor` relative to the file that contains the import.
The path is written exactly inside quotes, including the extension.
Successfully loaded documents are reused by canonical path, so repeated imports and cyclic back-edges
do not load another copy of the same document. Unreadable imports still produce diagnostics.

Built-in gate names follow the same rule, and built-in gates live in the implicit `Qora.Intrinsic` namespace.
Because of that, a namespace may define an operation with a name such as `Rx`.
If a call could mean both a user-defined operation and a built-in gate, Qora reports an explicit ambiguity error.
In that case, write the user-defined operation as `L.Rx(q)` and the built-in gate as `Qora.Intrinsic.Rx(pi/2, q)`.

If there is no user candidate with the same name in scope, a bare gate name means the built-in gate.
That lets you use the gate names from the documentation directly in your program.

**When you get it wrong, the compiler says why** — diagnostics are reported before output is produced:

```
[QSEM006] in `Main`: `Bell` expects 1 argument(s) but got 2
[QSEM016] in `Main`: index `q[5]` is out of range; `q` has 2 qubit(s) (valid: 0..1)
[QSEM018] in `Go`: `Flip` is ambiguous here: it could be `LibA.Flip` or `LibB.Flip`
          — qualify the call (e.g. `LibA.Flip(...)`)
```

**Running your programs.** Qora programs with user-defined operations, loops, classical variables, and `while` can run today on Amazon Braket's local simulator:

```bash
dotnet build src/Qora
python -m pip install -r vscode/runner/requirements.txt  # Python 3.11-3.13
python tools/run_braket.py yourfile.qor --shots 1000
```

The repository includes [Braket return-result validation](tools/braket_validation.py) and a
[Qiskit importer compatibility probe](tools/qiskit_validation.py). The latter is a diagnostic, not a
Qora result runner: its current importer cannot load all generated classical/output declarations.
It never substitutes internal measurement registers for entry returns. These checks are not a guarantee
that every Qora program runs on every simulator or device.

Known execution limitation: the unpatched Braket local simulator used for validation raises
`AttributeError: 'BoolType' object has no attribute 'size'` when reading a `bool[]` element or passing
the array to a helper. The same failure occurs with standalone OpenQASM `array[bool, 2]`, without Qora.
Qora keeps the valid Boolean array type; it does not silently replace it with a numeric array.
Reported as [Braket issue #429](https://github.com/amazon-braket/amazon-braket-default-simulator-python/issues/429),
with the fix in [PR #430](https://github.com/amazon-braket/amazon-braket-default-simulator-python/pull/430).
Combining that fix with PRs #425, #426, and #428 passes the current 54-case Qora execution suite,
including Boolean-array and S/T cases. Upstream merge and release are still pending; an ordinary
package install does not include these local verification fixes.
Separate Braket failures remain even in that combined-patch environment: measuring an element of a
`Qubit[]` helper parameter can fail, and some noncontiguous controlled-gate layouts can return wrong
results. These cases are outside the passing 54-case suite, so that result is not blanket backend support.

## How the compiler works

For the design contracts, see the **[compiler architecture reference](https://aj-comp.github.io/Qora/architecture/en/index.html)**.
For a worked source-to-implementation trace, start with the **[compiler developer manual](https://aj-comp.github.io/Qora/developer/en/index.html)**.

A compiler is a program that translates the code you wrote into a standard form a quantum computer can
actually run — here, **OpenQASM 3**. Qora doesn't do that in one leap; it splits the work into small steps,
each doing exactly one job.

The very first thing it does is move your code into a form that's convenient to work on internally, called
the **IR** (intermediate representation) — think of it as the compiler's own tidy notebook copy of your
program. Because it reshapes this IR instead of re-reading your text, every later step stays simple and hard
to get wrong.

It then checks names, catches rule violations, and applies the transformations it needs on that IR, and
finally prints the result out as OpenQASM text. Each row below is one of those steps:

| Stage | Pass | Input | Output | What it does |
|---|---|---|---|---|
| Load and parse | `SourceGraphLoader` + *Janglim* | entry source + imported files | parsed documents and import graph | Loads source documents, reuses successfully loaded paths, and parses each document into an AST. |
| Lower HIR | `AstToHirLowering` | AST + revision-bound lowering session | `HirProgram` | Rebuilds each document as an immutable declaration tree. Namespaces own nested declarations and `open` directives structurally; every expression is a typed, identity-bearing HIR node. |
| Expand | `ModuleLoader` | document `HirProgram` trees + import graph | merged `HirProgram` | Merges the already loaded documents in deterministic graph order, preserving namespace ownership. File I/O belongs to `SourceGraphLoader`. |
| Resolve | `Resolver` | exact `HirProgram` + source-bound rewrite session | rewritten `HirProgram` | Works out which callable a short call like `Bell(q)` really means, pins it to its fully qualified name (e.g. `GatesLib.Bell`), and records its typed HIR node identity. |
| Validate | `QoraValidator` + `SymbolTableBuilder` | exact `HirSnapshot` | semantic artifact or errors | Checks names, types, ownership, and callable contracts; compilation stops if HIR validation fails. Later MIR and target checks can also report diagnostics. |
| Monomorphize | `Monomorphizer` | accepted HIR + rewrite session | specialized HIR | Specializes unsized `Qubit[]` and `bit[]` callable parameters for concrete argument lengths while preserving namespace placement. Common HIR preserves `.Count`. |
| Finalize HIR | `HirPipelineBuilder` | specialized HIR | final `HirSemanticArtifact` | Revalidates changed HIR and records the formal-qubit effects needed by MIR lowering. |
| Lower to MIR | `QoraMirLowering` | final `HirSemanticArtifact` | SSA/CFG `MirSnapshot` | Gives every classical value a version, makes control flow explicit, lowers measurement expressions at their exact evaluation points (including short-circuit paths), and preserves typed HIR↔MIR provenance. |
| Analyze MIR | `MirAnalysisStore` | exact MIR program and its owned callables | immutable analysis facts | Computes and caches effects, CFG, aliases, memory state, path conditions, and bounds when consumers request them. |
| Target-lower | `MirOpenQasmLowering` | lowered MIR | `MirOpenQasmTargetProgram` | Recovers structured control flow from the CFG, assigns collision-free target names, and builds a typed OpenQASM-owned model. |
| Emit | `MirQasmEmitter` | typed OpenQASM target model | OpenQASM text | Serializes the completed target model without consulting HIR or making semantic decisions. |

- The **typed IR** (`src/Qora.Core/Ir/`) is owned end-to-end by the compiler; the parse engine stops
  at the lowering boundary.
- The **module loader** merges the whole `import` graph into one program.<br>The **resolver** then pins operation call targets to fully-qualified names (FQNs), so every later call site points at exactly one operation.<br>As in other programming languages with namespaces, ambiguity is an error; see the [design document](docs/namespaces-design.md) for details.
- The **validator** reports collect-all `QSEM` diagnostics.<br>One compile reports argument kinds and counts, register sizes, index bounds, reserved names, recursion, misplaced `use`, unknown or used-before-declared identifiers, and more.<br>The unified symbol table used by the validator is also exposed in the stages view.
- The accepted **HIR** remains the source-oriented semantic model. Its final effect summary retains only
  the formal-qubit `ParamModified` set that `QoraMirLowering` needs to create the correct qubit versions.
  Lowering then creates one authoritative SSA/CFG MIR snapshot; qubit versions, quantum effects, analyses,
  and every backend consume data from that exact MIR program.
- **Automatic uncomputation is future work.** The planned MIR scheduler will consume facts from one exact
  SSA/CFG program, choose cleanup sites, and generate the required inverse operations. Qora source, HIR,
  and the current MIR contain no `Adjoint`, inverse-request marker, or inversion pass.
- The OpenQASM backend consumes the exact lowered MIR snapshot and freezes a typed
  `MirOpenQasmTargetProgram`. Collision-safe names and structured target control flow belong to that lowering;
  `MirQasmEmitter` only serializes the target model.

## Repository layout

| Path | What it is |
|---|---|
| `src/Qora.Core` | The compiler: grammar, typed IR, validation, MIR analysis, OpenQASM emission |
| `src/Qora` | Console runner + the `--json` / `--stages` CLI contract the extension consumes |
| `src/Qora.Playground` | Blazor WASM playground (Monaco editor, live parse → AST → QASM) |
| `vscode/` | The VS Code extension (independently versioned, self-contained compiler bundled) |
| [`docs/developer/`](docs/developer/en/index.html) | Compiler developer manual and detailed function flows |
| [`docs/book/`](docs/book/en/index.html) | Quantum-programming learning book |
| [`docs/architecture/`](docs/architecture/en/index.html) | Compiler architecture and pass contracts |
| [`tests/Qora.Tests/`](tests/Qora.Tests/) | Compiler and MIR regression tests |
| [`tools/`](tools/) | Braket runner and simulator validation scripts |

## Roadmap & releases

- Direction and priorities: [docs/TODO.md](docs/TODO.md) — the planned automatic-uncomputation work will
  add a MIR scheduler that computes the global cleanup order and generates the required inverse operations.
- Language release notes: [CHANGELOG.md](CHANGELOG.md) (v0.1 → v0.40.0). The VS Code extension is
  versioned separately: [vscode/CHANGELOG.md](vscode/CHANGELOG.md).

## License

[MIT](LICENSE) — built on the [Janglim](https://www.nuget.org/packages/Janglim) parser engine.
