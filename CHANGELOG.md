# Changelog

Release notes for the **Qora language** — its grammar (`QoraGrammar`) and compiler pipeline
(`src/Qora.Core/Ir/`: lowering, validation, inversion, OpenQASM emission). This tracks the language
itself; the VS Code extension is versioned separately in [`vscode/CHANGELOG.md`](vscode/CHANGELOG.md).

Qora is a Q#/C#-flavored quantum learning language built on the
[Janglim](https://www.nuget.org/packages/Janglim) parser engine: source is parsed into an AST, which is
emitted as **OpenQASM 3.0**.

> **Note:** Qora was renamed from **Ket** on 2026-07-01 (a "Ket" extension already existed). Versions
> 0.1–0.7 below were authored under the old name.

## 0.14 — 2026-07-05

### Added
- **`float` and `angle` classical types.** Operations take real-valued classical parameters
  (`operation Turn(angle theta, Qubit q)`), and `var`/`const` bind them (`const angle t = tau / 8;`).
  `float` is a general real; `angle` is its 2π-periodic, hardware-friendly cousin — both lower to
  OpenQASM 3's `float` / `angle`. Types are carried, not yet enforced (a loose model that keeps the
  teaching path frictionless; the executor owns the 2π wrap).
- **Parameterized register sizes `Qubit[n]`.** An operation can be generic over its register width
  (`operation Fanout(Qubit[n] q) { for i in 0..n-1 { H(q[i]); } }`); a new monomorphization pass
  (`Ir/Passes/Monomorphizer.cs`) stamps out a concrete copy per call-site size (`Fanout__sz3` for a
  3-qubit register) and re-validates once sizes are known — the Silq-family, C++-template-style route to
  generics (versus Q#'s dynamic `Qubit[]`), and the foundation a future automatic-uncompute pass needs.
- **Referential-integrity gate (`Ir/Passes/ReferentialCheck.cs`).** A post-mangle safety net proves
  every emitted identifier resolves to a declaration / operation / built-in; a dangling name (a compiler
  bug where a pass renamed a declaration but not one of its uses) now fails loudly as `QINTERNAL` instead
  of shipping silently-broken QASM.

### Changed
- **A name collision never fails compilation.** The name mangler now renames a name ONLY when it would
  actually collide — with an OpenQASM keyword, a stdgates gate, or another emitted name — appending `_`
  until unique and recording a `// Qora:` note in the QASM header. Collisions are resolved at the source
  (they can never surface as a compile error) and non-colliding names pass through unchanged (`q` stays
  `q`), superseding 0.12's suffix-every-name scheme.
- **Whole-operation `Adjoint` is now a real IR→IR pass.** A new `AdjointMaterializer`
  (`Ir/Passes/AdjointMaterializer.cs`) runs before name mangling: it turns each `Adjoint Foo(...)` on a
  user operation into an ordinary inverse-def op (`Foo__adj`, via the inversion kernel) and rewrites the
  call to a plain call, closing the transitive set. Because the synthesized name is minted *before*
  mangling, it flows through the same collision resolution as every other op — structurally closing the
  emit-time seam where an inverse-def name could clash with a user name (previously patched in the
  emitter). `QasmEmitter` is now a pure printer: it invents no names and inverts nothing.

### Docs
- New **compilation-pipeline architecture** page (en / ko / ja): source → parse → lower → the IR passes
  → emit, each stage with what it does and every listing produced by the real compiler. The Adjoint
  pipeline page was refreshed to the current collision-only mangling and the materialize-before-mangle
  pass structure.

## 0.13 — 2026-07-03

### Changed
- **Bit comparisons emit bool literals**: `if (r == 1)` now compiles to `if (r_ == true)` (either
  operand order, `!=` too, only for names known to be bits). Both spellings are valid OpenQASM 3,
  but the dominant consumer — Qiskit's `qasm3` importer — accepts only the bool form; this makes
  the core measure-then-branch teaching pattern actually load and run in Qiskit.

### Added
- **Full-language execution path, measured and shipped.** Amazon Braket's LOCAL simulator runs
  Qora's complete output — `def` subroutines (synthesized `___adj` included), `int`/`const`,
  `for` with variable indices, measure→`if`, even `while` — verified 8/8 with correct statistics
  (`tools/braket_validation.py`, now release gate #2; whole-operation `Adjoint` provably returns
  states to `|0…0⟩` on an external engine). New `tools/run_braket.py` runs any `.qor` end-to-end
  (compile → Braket → measurement histogram); `tools/stdgates.inc` ships the canonical standard-
  gate library Braket needs on disk. Requires Python 3.10–3.13 (the Braket SDK does not support
  3.14 yet).
- **`const` is now enforced as an immutable binding (QSEM024).** `const` may hold any value —
  including a measurement result (`const r = M(q[0]);`, the Q#-`let` idiom) — but assigning to a
  `const` name is a compile error with a fix-it hint (`var` for mutable values, `bit` for
  re-measured results). Previously `const` was accepted but silently unenforced.
- **`tools/qiskit_validation.py`** — release-gate script: compiles golden programs, verifies they
  LOAD (`qiskit.qasm3.loads`) and RUN (Aer) with correct measured statistics (Bell entanglement,
  deterministic flips, functor identities, reset, measure→if feedback). First measured run
  (Qiskit 2.4 / qiskit-qasm3-import 0.6.0) also mapped the importer's gaps: `def` subroutines,
  `int`/`const` declarations, variable qubit indices (`q[i]` in for), and `&&`/`||` are not
  accepted — the planned fix is a flattening emission mode (inline defs + unroll literal loops +
  constant-fold), feasible because Qora guarantees literal bounds and no recursion.

## 0.12 — 2026-07-03

### Added
- **Source spans on every semantic error.** All QSEM diagnostics now point at the exact offending
  source range — the whole statement for statement-level rules (`HH(q[0]);`), just the name token
  for declaration-level ones (the second duplicate parameter, the operation name that shadows a
  built-in, the `open`/`import` line that failed). The editor squiggles the real culprit instead of
  the whole document. Errors raised inside IMPORTED files stay position-less on purpose (their
  offsets belong to another document) and keep the file-name prefix.
- **Provenance comment in emitted QASM**: output now begins with `// generated by Qora v0.12`, so a
  QASM file that travels alone (papers, repos) records which compiler produced it.
- **`docs/llms.txt`** — the complete language surface as one machine-readable page, for feeding to
  AI assistants (new languages are only as usable as their AI legibility).
- Error-message hint: `open X;` where `X` is unknown now explains that `open` does not load files
  and suggests the missing `import`.
- **Built-in gate names relaxed to Q#-style** ("declaration allowed, ambiguous use is an error").
  The built-ins now live in the implicit `Qora.Intrinsic` namespace, open everywhere. A namespaced
  operation may reuse a gate name (`namespace L { operation Rx … }` — call it as `L.Rx(q)`); an
  unqualified use that could mean both the user op and the built-in is a QSEM018 ambiguity listing
  both qualifications (`L.Rx(...)` / `Qora.Intrinsic.Rx(...)`) — never a silent pick. With no user
  candidate in scope the bare name is the built-in, as always. Still reserved (QSEM013): the
  measurement family and `pi`/`tau`/`euler` everywhere, gate names for GLOBAL operations (no
  qualifier exists there), and declaring `Qora.Intrinsic` itself.
- **The module system is real.** Namespaces resolve (`Resolver.cs`, run between lowering and
  validation) with the standard C#/Q# rules: a qualified call (`MyLib.Bell(q)`) resolves directly;
  an unqualified name searches the own namespace, then the global namespace, then the `open`ed
  namespaces — exactly one match wins, two or more is **QSEM018** (candidates listed, qualify to fix),
  an unknown namespace/member is **QSEM019** (also raised for `open` of a nonexistent namespace).
  `open` is not transitive. Duplicate operation names are now per-namespace: within one namespace
  **QSEM022**, in the global namespace still QSEM008 — the same simple name in two namespaces is fine.
- **Multi-file programs.** `import` loads real files before resolution (`ModuleLoader.cs`):
  `import gates_lib;` reads `gates_lib.qor` relative to the importing file, `import lib.gates;` maps
  dots to directories (`lib/gates.qor`), `import "a b.qor";` takes a literal path. Loading is
  transitive with diamond-sharing (each file loads once); a missing/unreadable file is **QSEM020**,
  an import cycle is **QSEM021** with the chain shown (`a.qor -> b.qor -> a.qor`). The CLI resolves
  imports next to the entry file; stdin input takes a new `--base-dir <dir>` flag (the VS Code
  extension passes the document's directory automatically). Without a file context, `import` reports
  a clear QSEM020. QSEM099 (the "module system in progress" gate) is gone.
- **Total name mangling at emission** (`NameMangler.cs`, runs after validation, before the emitter).
  Every user-defined name gets a trailing `_` in the QASM output (`q` → `q_`, `Bell` → `Bell_`), and
  namespaces flatten C++-style (`MyLib.Bell` → `MyLib__Bell_`). No built-in ends with `_` and the
  transform is injective, so collisions with OpenQASM keywords and stdgates names are structurally
  impossible — `bit s = M(q[0]);` now compiles (`s` used to collide with the stdgates `s` gate).
  Errors and the stages view (`ast`/`ir`/`irInverse`) keep original names; only QASM shows mangled
  ones. QSEM013 now only guards source-level meaning: an operation may not take a built-in
  gate/measurement name (checked on the last segment, so `namespace L { operation Rx … }` still
  errors), and no declaration may be named `pi`/`tau`/`euler`.

## 0.11 — 2026-07-03

### Added
- **Module-system grammar (in progress).** `import gates_lib;` / `import "path.qor";`,
  `namespace A.B { open C.D; … }`, and dotted qualified names now parse. Until the resolver pass lands
  they are gated with a clear QSEM099 error, so a namespaced program can never silently compile with
  global semantics. Design: `docs/namespaces-design.md` (resolution = local scope → own namespace →
  opened namespaces, ambiguity is an error; built-in gate names stay reserved in v1).
- **String literals.** Janglim `0.3.0-preview.1` adds a raw-regex `StringLiteral` token type
  (requested in `docs/janglim-request-raw-regex-terminal.md`), enabling the string-path import form —
  quote-delimited tokens were previously impossible to lex.
- **Hardened validation** (fourth adversarial review, 17 confirmed findings fixed):
  - argument KINDS for built-in gates — `Rx(q[0], pi/2)` (swapped), `H(pi)`, `X(5)` are now errors;
  - full user-operation call signatures — argument count, register size, single-qubit vs register,
    qubit-into-classical (QSEM006), including `Adjoint` call sites;
  - whole-register duplicate/overlap operands — `CNOT(q, q)`, `CNOT(q, q[0])` (QSEM014);
  - literal index bounds and no indexing of single-qubit parameters (QSEM016);
  - measurement must land in a `bit` (QSEM017); duplicate parameter/register/measure-bit names (QSEM015).
- **QSEM013 relaxed to match OpenQASM scoping**: def-local parameters, variables, and loop variables
  may legally shadow stdgates names (`operation ApplyTwice(Qubit t)` is valid again); QASM keywords
  stay illegal everywhere, and global-scope declarations plus operation names stay checked.

### Fixed
- `!` in conditions parenthesizes its operand, so Qora's negate-the-whole-expression meaning survives
  OpenQASM's re-parse.
- Untyped `var`/`const` float inference now propagates through variables (`var a = pi; var b = a / 2;`
  emits `float b`).
- Hoisted declarations deduplicate; the stages view's inverse-IR column now shows the same transitive,
  uniquified inverse defs the QASM contains.

## 0.10 — 2026-07-02

### Added
- **Whole-operation `Adjoint`.** `Adjoint Foo(args)` on a user operation now compiles: the compiler
  synthesizes the inverse subroutine (`Foo__adj` — body reversed, each gate inverted) instead of
  emitting the invalid `inv @ Foo`. Covers straight-line gates, classical declarations (kept in
  place), `if` (branches inverted), `for` (iterated backwards as `[hi:-1:lo]`), and nested operation
  calls (their inverses are synthesized transitively). Non-invertible bodies — measurement, reset,
  classical mutation, `while`/`repeat`, local `use`, recursion — are compile errors with the reason
  chained through the call graph.
- **A typed intermediate representation (`QProgram`, `src/Qora.Core/Ir/`).** The pipeline is now
  `AST → lowering → IR → validation → inversion → emission`; every pass works on IR the compiler
  owns, and the Janglim AST stops at the lowering boundary.
- **Semantic validation (QSEM001–QSEM015).** Invalid programs now fail *before* emission — with every
  violation reported in one compile — instead of silently producing broken OpenQASM: non-invertible
  `Adjoint` (001), functors on defs/reset (002/003), bare or in-expression measurement (004/005),
  argument count and qubit shape/size mismatches for built-ins *and* user operations (006), unknown
  names with a case hint (007), duplicate definitions (008), entry-op calls/params (009/010),
  recursion (011), misplaced `use` (012), reserved/shadowing names (013), duplicate gate operands
  (014), duplicate registers (015).
- **Grammar:** unary minus in expressions (`Rx(-pi/2, q[0]);`) and zero-argument functor calls
  (`Adjoint Nop();`).
- **Emitter correctness:** defs are emitted in dependency (declare-before-use) order; synthesized
  inverse names are uniquified on collision; hoisted declarations are deduplicated; untyped
  `var`/`const` with a real-valued initializer infer `float`; functor-modified rotation gates keep
  all their arguments.
- **Compilation-stages output.** `qora --json --stages` additionally returns `ast` / `ir` /
  `irInverse` texts (consumed by the VS Code "Show Compilation Stages" panel); the default `--json`
  reply stays lean for keystroke diagnostics.

### Changed
- The built-in gate table is a single registry (`QoraGates`): the emitter's name mapping, the
  validator's arity/unitarity checks, the inverter's irreversibility rules, and the reserved-name set
  all derive from one entry per gate.

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
