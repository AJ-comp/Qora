**English** · [한국어](README.ko.md) · [日本語](README.ja.md)

<div align="center">

<img src="docs/images/quantum-language-icon-128.png" alt="Qora" width="128" height="128">

# Qora

**A quantum programming language built for learning.**
Q#/C#-flavored syntax in, verified **OpenQASM 3** out.

[![VS Code Marketplace](https://img.shields.io/visual-studio-marketplace/v/qora-lang.qora-language?style=flat-square&label=VS%20Code&labelColor=111827&color=6b3fd4)](https://marketplace.visualstudio.com/items?itemName=qora-lang.qora-language)
[![Docs](https://img.shields.io/badge/docs-GitHub%20Pages-111827?style=flat-square&labelColor=111827&color=6b3fd4)](https://aj-comp.github.io/Qora/en/)
[![Built on Janglim](https://img.shields.io/badge/built%20on-Janglim%200.3.0--preview.1-111827?style=flat-square&labelColor=111827)](https://www.nuget.org/packages/Janglim)
[![Emits OpenQASM 3.0](https://img.shields.io/badge/emits-OpenQASM%203.0-111827?style=flat-square&labelColor=111827)](https://openqasm.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-111827?style=flat-square&labelColor=111827)](LICENSE)

</div>

---

**Qora is a small language for *learning* quantum programming.** Quantum code is usually written
through Python libraries (Qiskit, Cirq) that can't tell you when your program stops making physical
sense — or in research languages that assume you already know the theory. Qora sits in between: you
write circuits in a familiar, C#-shaped syntax, the compiler checks that your program actually means
something a quantum computer can do, and then it emits standard **OpenQASM 3** you can inspect line by
line or feed to real toolchains.

Being a teaching language shapes every design decision:

- **Errors come before output, and they explain themselves.** If a program compiles, its OpenQASM is
  valid — never silently wrong.
- **The compiler is see-through.** One command shows how your source became an AST, a typed IR, a
  synthesized inverse, and finally QASM.
- **The hard quantum rules are enforced, not assumed** — you can't accidentally reuse a dirty qubit,
  invert a measurement, or pass a register where a single qubit belongs.

## A taste

<table>
<tr>
<th>Qora</th>
<th>emitted OpenQASM 3</th>
</tr>
<tr>
<td>

```
operation Bell(Qubit[2] q) {
    H(q[0]);
    CNOT(q[0], q[1]);
}

operation Main() {
    use q = Qubit[2];
    Bell(q);
    bit r = M(q[0]);
}
```

</td>
<td>

```
OPENQASM 3;
include "stdgates.inc";

def Bell(qubit[2] q) {
  h q[0];
  cx q[0], q[1];
}

qubit[2] q;
bit r;
Bell(q);
r = measure q[0];
```

</td>
</tr>
</table>

## Quick start

The fastest path is the VS Code extension — it bundles the whole compiler, so there is **nothing else
to install** (no .NET, no Python):

1. Install **[Qora Language](https://marketplace.visualstudio.com/items?itemName=qora-lang.qora-language)**
   from the VS Code Marketplace.
2. Create a file named `bell.qor` and paste the example above.
3. You already have a working toolchain:
   - errors underline **as you type** (hover them for the reason — try `CNOT(q[0], q[0])`);
   - hover any gate or keyword for a short doc (`H`, `use`, `M`, …);
   - **`Ctrl+Shift+P` → `Qora: Transpile to OpenQASM`** opens the compiled output beside your file;
   - **`Qora: Show Compilation Stages`** shows the whole pipeline live — source → AST → IR →
     (inverse IR) → QASM — and refreshes every time you save.

Prefer the repo? With the .NET 10 SDK:

```bash
git clone https://github.com/AJ-comp/Qora.git
cd Qora
dotnet run --project src/Qora          # parses a demo program, prints its OpenQASM
```

(The same binary powers the extension via `qora --json` — one line of JSON per compile:
`{success, qasm, errors[]}`.)

## The language in five minutes

**Qubits and gates.** Qubits are allocated with `use` and addressed like arrays. Gates are calls:

```
use q = Qubit[3];              // three qubits, all |0⟩
H(q[0]);                       // superposition
CNOT(q[0], q[1]);              // entangle
Rx(pi/2, q[2]);                // rotations take the angle first
Reset(q[2]);                   // back to |0⟩
```

Built-in gates: `H X Y Z S T` · `CNOT/CX CY CZ SWAP CCX` · `Rx Ry Rz` · `Reset ResetAll`.

**Measurement** collapses a qubit into a classical `bit` — and that's the only way to get one:

```
bit r = M(q[0]);               // measure (this is irreversible!)
r = M(q[0]);                   // re-measure into the same bit
```

**Classical control** looks exactly like you expect, and can react to measurements:

```
const int n = 2;
var count = 0;
for i in 0..n {  H(q[i]);  }
if (r == 1) {  X(q[1]);  } else {  Z(q[1]);  }
while (count < 2) {  count = count + 1;  }
repeat {  H(q[0]);  r = M(q[0]);  } until (r == 1);
```

**Operations** are subroutines (`Main` is the entry point), and **functors** transform them:

```
operation Prep(Qubit[2] q) {
    H(q[0]);
    Controlled X(q[0], q[1]);   // add a control to any gate
    T(q[1]);
}

Adjoint Prep(q);                // ★ the compiler SYNTHESIZES Prep's inverse:
                                //   gates reversed & inverted, for-loops run backwards,
                                //   if-branches inverted, nested calls handled transitively
```

That `Adjoint` line is Qora's flagship: "undo this computation" is a one-word request, and it is the
first stone on the road to [automatic uncomputation](docs/TODO.md).

**When you get it wrong, the compiler says why** — every violation in one compile, before any output:

```
[QSEM001] in `Main`: `Adjoint Meas` cannot be compiled: it measures a qubit,
          and measurement is irreversible
[QSEM006] in `Main`: `Bell` expects 1 argument(s) but got 2
[QSEM016] in `Main`: index `q[5]` is out of range; `q` has 2 qubit(s) (valid: 0..1)
```

**Running the output**: the emitted QASM is standard OpenQASM 3. Programs without subroutines load
straight into Qiskit (`qiskit.qasm3.loads(...)`); `def` subroutine support varies by consumer, so
flat programs are the most portable.

## Documentation

All docs are live on GitHub Pages in **English · 한국어 · 日本語** — the language is auto-detected from your browser (English fallback), and every page has a switcher:

| | |
|---|---|
| **[Learning Quantum Gates with Qora](https://aj-comp.github.io/Qora/en/book/)** | The 11-chapter book: qubit states, amplitudes, superposition, interference, and the X/H/Z/CNOT gates — each read through runnable Qora code |
| **[Understanding the H Gate with Arrows](https://aj-comp.github.io/Qora/en/)** | The arrow-based introduction: why amplitudes interfere, and why applying H twice brings \|0⟩ back |
| **[The Adjoint Compilation Pipeline](https://aj-comp.github.io/Qora/en/adjoint-pipeline.html)** | A full compiler walkthrough — one example traced through source → AST → IR → inverse IR → OpenQASM, with real outputs |

## How the compiler works

```
source ─parse─▶ AST ─lower─▶ typed IR ─validate─▶ (errors? stop & report ALL of them)
                                      └─ clean ─▶ invert (Adjoint synthesis) ─▶ OpenQASM 3
```

- The **typed IR** (`src/Qora.Core/Ir/`) is owned end-to-end by the compiler; the parse engine stops
  at the lowering boundary.
- The **validator** (QSEM001–017) is collect-all: one compile reports every problem — argument kinds
  and counts, register sizes, index bounds, reserved names, recursion, misplaced `use`, irreversible
  `Adjoint` targets (with the reason chained through the call graph), and more.
- The **inverter** is a pure IR→IR pass — the same machinery a future pass will reuse to inject
  automatic uncomputation.
- A **module system** (`import` / `namespace` / `open`) is in progress: the grammar already parses,
  the resolver is next ([design](docs/namespaces-design.md)).

## Repository layout

| Path | What it is |
|---|---|
| `src/Qora.Core` | The compiler: grammar, typed IR, validation, inversion, OpenQASM emission |
| `src/Qora` | Console runner + the `--json` / `--stages` CLI contract the extension consumes |
| `src/Qora.Playground` | Blazor WASM playground (Monaco editor, live parse → AST → QASM) |
| `vscode/` | The VS Code extension (independently versioned, self-contained compiler bundled) |
| `docs/` | GitHub Pages content: the book, guides, and design docs |

## Roadmap & releases

- Direction and priorities: [docs/TODO.md](docs/TODO.md) — next up is the module-system resolver, then
  effect analysis (`qfree`/`mfree`) on the road to **automatic uncomputation**.
- Language release notes: [CHANGELOG.md](CHANGELOG.md) (v0.1 → v0.11). The VS Code extension is
  versioned separately: [vscode/CHANGELOG.md](vscode/CHANGELOG.md).

## License

[MIT](LICENSE) — built on the [Janglim](https://www.nuget.org/packages/Janglim) parser engine.
