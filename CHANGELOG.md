# Changelog

Release notes for the **Qora language** — its grammar (`QoraGrammar`) and OpenQASM emitter
(`QoraQasmEmitter`). This tracks the language itself; the VS Code extension is versioned separately in
[`vscode/CHANGELOG.md`](vscode/CHANGELOG.md).

Qora is a Q#/C#-flavored quantum learning language built on the
[Janglim](https://www.nuget.org/packages/Janglim) parser engine: source is parsed into an AST, which is
emitted as **OpenQASM 3.0**.

> **Note:** Qora was renamed from **Ket** on 2026-07-01 (a "Ket" extension already existed). Versions
> 0.1–0.7 below were authored under the old name.

## 0.9 — 2026-07-02

### Added
- **`//` line comments.** A `//` runs to the end of the line and is dropped before parsing, while a lone
  `/` still lexes as division. This requires Janglim `0.2.0-preview.3`, whose lexer treats a comment
  terminal's value as a raw regex, so `//` wins the longest-match over the `/` operator.

### Pending
- Block `/* */` comments still need the engine's scope-comment path wired into the lexer, so they remain
  deferred to Janglim.

## 0.8 — 2026-07-01

### Added
- **Single-gate functors** `Adjoint G(...)` and `Controlled G(...)`, emitted as OpenQASM `inv @` / `ctrl @`
  (e.g. `Controlled X(c, t)` → `ctrl @ x c, t;`, i.e. a CNOT).
- **Richer conditions** in `if` / `while` / `repeat`: `== != < <= > >= && || !`.
- **`if / else`** and **`else if`**.
- **First-class `Reset`.**

### Notes
- Operations are still void — no return values yet.

## 0.7 — 2026-06-30

### Added
- **Rotation gates** `Rx` / `Ry` / `Rz` with an angle expression: `Rx(pi/2, q[0])` → `rx(pi / 2) q[0];`.
- **`*` and `/` operators** and a **float literal** (`0.5`); `pi` passes through to OpenQASM's built-in.
  Expressions stay flat (no precedence) — OpenQASM re-parses precedence on its side, so `1 + pi / 2`
  still comes out right.
- Gate arguments generalized to `qubitRef | expr`, so an argument can be a qubit `q[0]`, a whole register
  `q`, a number, or an angle `pi/2`.

## 0.6 — 2026-06-30

### Added
- **Parameterized operations and subroutine calls.** C#-style type-first parameters
  (`operation Bell(Qubit[2] q)`); operations call each other, either by whole register `Bell(q)` or by
  qubit `Bell(q[0], q[1])`.
- The operation named **`Main`** is the entry point (its body becomes the QASM top level); every other
  operation becomes a `def`. Calls and gate applications share one surface form — the emitter tells them
  apart by name.

## 0.5 — 2026-06-30

### Added
- **Classical variables and reassignment**, so `while` loops and counters can actually terminate.
  Type-first / C# model: `const i` / `const int i` (immutable), `var i` (mutable, inferred),
  `int i` (mutable, typed).
- Reassignment `i = i + 1;` with `+` / `-` arithmetic.
- **Gate set** expanded with `S`, `T`, `CZ`, `SWAP`, `CCX` (Toffoli) — emitter-only, no grammar change.

### Changed
- Removed `let` (its meaning differs across languages); measurement is now `bit r = M(q[0]);` (type-first).
- Qubits stay on `use q = Qubit[n];` — an allocated resource, kept distinct from ordinary variables.

## 0.4 — 2026-06-30

### Added
- **`while (cond) { ... }`** and **`repeat { ... } until (cond);`**.

### Notes
- OpenQASM 3 has no repeat-until, so `repeat { B } until (c)` lowers to
  `while (true) { B; if (c) { break; } }`.
- Declarations inside loop bodies are hoisted (recursively), so a `bit r = M(q)` inside a loop declares
  its `bit r;` once at the top — this is what makes the repeat-until-success pattern emit valid QASM.

## 0.3 — 2026-06-30

### Added
- **Control flow:** `if (r == 1) { ... }` (measurement feedback) and `for i in 0..2 { ... }` (both ends
  inclusive). New operators `==` and `..`.

## 0.2 — 2026-06-30

### Changed
- **Q#-flavored surface** (replacing the QASM-like v0.1): `operation Name() { ... }`, scoped allocation
  `use q = Qubit[2];`, function-call gates `H(q[0]); CNOT(q[0], q[1]);`, measurement `let r = M(q[0]);`.
- The emitter now translates gate names (`H`→`h`, `CNOT`→`cx`, `X`→`x`) and hoists declarations above the
  body.

## 0.1 — 2026-06-30

### Added
- **First working language.** A minimal grammar (`qreg` / `creg` / `h` / `x` / `cx` / `measure`,
  deliberately close to OpenQASM), parsed by Janglim into a `MeaningUnit`-tagged AST.
- **OpenQASM 3.0 emitter:** `qreg q[n]` → `qubit[n] q;`, `creg c[n]` → `bit[n] c;`,
  `measure` → `c[0] = measure q[0];`.
