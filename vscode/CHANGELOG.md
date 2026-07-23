# Changelog

All notable changes to the Qora Language extension.

## 0.20.0

- Bundles the **Qora v0.27** compiler. A whole `bit[]` register is now a container of bits rather than a
  number: `var n: int = f;`, `f + 1`, `if (f)` and `f == 1` are rejected (`QSEM036`), because a bit pattern
  has no sign and the same bits read `2` unsigned or `-2` signed. Write `AsInt(f)` to read a register as an
  unsigned integer — it emits the width-qualified `uint[N](f)`. Comparing two registers of the same width
  (`f == g`) stays legal, and a single bit (`f[i]`) or a scalar `bit` is unchanged. `AsInt` is highlighted
  and hover-documented.
- `return` may now stand anywhere a statement may — an early return, one inside `if`/`else`, one inside a
  `for`/`while`. The compiler reshapes the function into the form the execution target needs, so the
  "a `return` must be the last statement of its block" error is gone.

## 0.19.0

- Bundles the **Qora v0.26** compiler. Adds `function` — a classical, pure, value-returning subroutine
  (`function angleOf(k: int): angle { return pi / k; }`) alongside the quantum, void `operation`. A function
  is pure (no gates, no `use`, no measurement, no operation calls), so its call is a value you can use
  anywhere in an expression: `var k: int = two();`, `Rx(angleOf(4), q[0])`, `if (c == g())`. Measurement stays
  the one side-effecting value form (`var r: bit = M(q[i]);`) and an operation stays void. Functions emit as
  OpenQASM `def Name(...) -> T { … }` and run on the Braket local simulator, including `if`/`else`-returning
  ones. New diagnostics: QSEM033 (impure function), QSEM034 (qubit in a function signature), QSEM035 (return
  placement / coverage).

## 0.18.0

- Bundles the **Qora v0.25** compiler. Type annotations are now **trailing** (`name: T`): a parameter is
  `q: Qubit[]`, a declaration is `var x: int = 5` or `const a: int[] = [1, 2]`. The type stays optional
  (`var x = 5` still infers it). The leading forms (`int n`, `Qubit[] q`, `bit r = M(q[0])`) are removed —
  there is one way to write a type. Existing files update mechanically: move the type after the name with a
  `:`, and prefix a keyword-less typed declaration with `var`. Snippets and the demo example ship in the
  new syntax.

## 0.17.1

- Bundles the **Qora v0.24** compiler — a hardening release over v0.23. Array-local hoisting now mints
  collision-proof names (a minted global/parameter can never shadow a user variable, operation, or gate),
  and a measurement lowered out of a condition no longer masks a user's undeclared `__mN` error.

## 0.17.0

- Bundles the **Qora v0.23** compiler. Classical array locals may now be declared anywhere a scalar
  goes — helper operations, loops, branches; the OpenQASM backend handles the target's global-scope
  rule automatically (hidden array-reference parameter / scope-top hoisting).
- `bit[]` parameters are specialized per call-site length and emit as `bit[N]`; writing to a `bit[]`
  parameter is reported as `QSEM032` (bit registers pass by value — the caller would never see it).
- Whole bit-register comparisons emit as `int(r) == …`, the form Amazon Braket executes.

## 0.16.0

- Bundles the **Qora v0.22** compiler. Every array and register index is now proven in bounds at compile
  time: an index with no proof is reported as `QSEM030`, and one provably out of range as `QSEM016`.
  Proofs come from a literal within a known length, a `0..a.Count-1` loop, a constant-bounded loop, a
  call-site minimum-length precondition for a classical-array parameter, or a guard
  `if (0 <= n && n < a.Count)`.
- A name used before its declaration in its own scope is reported as `QSEM025`, and a measurement into a
  non-`bit` array element as `QSEM017`.

## 0.15.0

- Bundles the **Qora v0.21** compiler. Operation register parameters now use `Qubit[]` and inspect
  their call-site-specialized length with `.Count`; concrete allocation remains `use q = Qubit[N]`.
- Adds `int[]`, `float[]`, `bit[]`, and `angle[]` highlighting and examples, including literals,
  zero-initialized `new T[N]`, indexed reads/writes, and `.Count`.
- `bit[]` now compiles to an OpenQASM bit register (`bit[N]`) rather than a general array, which
  OpenQASM does not allow for bits.
- An out-of-bounds literal index is now reported at the call site when the array reaches the access
  through a parameter, matching what was already reported for the same access written inline.

## 0.14.0

- Bundles the **Qora v0.20** compiler — rung ④ of the automatic-uncomputation ladder begins with the
  cleanup **plan builder** (a safe ancilla's reverse-order adjoint cleanup, computed but not yet injected);
  rung ③ now blocks an ancilla whose write is a call it cannot invert (a `while`/`repeat`, classical
  mutation, or local `use` in the callee), with the reason shown in the `--stages` uncompute view; and
  calls are bound to their callee by stable node reference (`CalleeOpId`) rather than by name.

## 0.13.0

- Bundles the **Qora v0.19** compiler — rung ③ of the automatic-uncomputation ladder: the per-qubit
  safety verdict (reason + culprit statement), the qubit value-genealogy graph with its always-on
  coherence sweep, the `IsAncilla` / `IsCleanupCandidate` classification, hoisted register births,
  and the `--stages` uncompute view with per-element blocked reasons.
- **▶ Run now shows staged progress.** The "Qora Run" output channel logs each run like a lab
  notebook — the compile step with a circuit summary (qubits · gates · measurements) and timing,
  the execution step with the shot count and timing, and failures marked with `✗` so the cause
  stays visible after the popup is gone. The status-bar progress message updates per stage
  (compiling → running N shots) instead of a single "running…".

## 0.12.0

- Bundles the **Qora v0.18** compiler — internal analysis infrastructure only, no new surface syntax:
  - **Qubit-event analysis model**: effect analysis (rung ① of the auto-uncompute ladder) now records,
    per operation, a program-ordered stream of `QubitEvent`s — one per qubit that a *leaf* statement
    (gate / measurement / `use`) touches, tagged **Read** (a control or diagonal-gate target, value
    preserved), **Write** (a target / reset / register birth), or **Measure**. The per-qubit timeline
    and the per-statement entanglement grouping are both read off this one stream, replacing the old
    per-statement touched/modified side table.
  - **Liveness as a query (rung ②)**: a new derived `LiveRange(op, qubit)` returns the birth→last-use
    events bracketing a qubit's life (min/max program order), subsumption-aware so a whole-register
    effect answers an element query and vice versa — computed on demand, nothing stored. The last use
    (a final control *Read* counts) is the death point the injection rung will hang an uncompute off.
  - **Defensive fail-loud locks**: spots that used to silently under-approximate — an unknown callee,
    a mismatched argument/parameter count, an indexed effect collapsed onto a single-qubit binding, a
    non-`Adjoint` functor on a user-op call — now throw an internal error, since each is already
    guaranteed impossible on clean IR by an earlier validation pass (QSEM002 / 006 / 007 / 016).
  - Compiler test suite grew to 168 cases.

## 0.11.0

- Bundles the **Qora v0.17** compiler:
  - **Effect analysis**: a new pass computes, per statement, which qubits are *touched* vs *modified*
    (controls and diagonal-gate targets are touched but not modified), stored on the semantic model —
    the use/def groundwork for automatic uncomputation.
  - **Operations are first-class symbols** in one connected symbol table: an op resolves as a symbol
    with its call sites, the compilation-stages symbols view now lists operations, and using an operation
    as a value gives a precise error (QSEM028) instead of a misleading "not declared".
  - Compiler test suite grew to 163 cases.

## 0.10.0

- Bundles the **Qora v0.16** compiler — an architecture release:
  - **Stable node identity + persistent semantic model**: the symbol table built at validation is now
    carried through the whole pipeline as an Id-keyed side table (Roslyn-SemanticModel style) instead
    of being rebuilt per consumer, and a new Id-uniqueness safety net fails loudly (`QINTERNAL`) on any
    compiler bug that would corrupt it. Groundwork for effect analysis / automatic uncomputation.
  - **`within/apply` conjugation in the IR**: compute–act–uncompute (U V U†) flattening with a
    synthesized inverse, gated by a clean **QSEM027** when the `within` block cannot be inverted.
    (IR-level only for now — surface syntax comes next.)
  - Compiler test suite grew to 140 cases; emitted QASM is byte-identical to v0.15.
- **Compilation-stages panel: new symbol-table column.** "Show compilation stages" now renders the
  validation-time symbol table (scopes, kinds, types, const values, use counts) between the IR and
  inverse-IR columns, straight from the compiler's persisted semantic model.

## 0.9.0

- Bundles the **Qora v0.15** compiler — a correctness & ergonomics release:
  - **Measure inside a condition**: `if (M(q[0]) == 1) { … }` (and `while` / `repeat … until`) now
    works, Q#-style. The compiler lowers it to a hoisted `bit` that is tested in the condition, so it
    emits valid OpenQASM.
  - **Measure bits are block-scoped**, like `var`/`const`: a bit measured inside a branch is scoped to
    that branch, and using it after the block is a clear error — consistent, safer scoping.
  - **`const` accepts any immutable value** — a literal, a runtime variable, or a measurement (Q#-`let`
    style) — and always emits valid OpenQASM. A runtime-bound `const` (`const c = x;`) is emitted as a
    plain variable that is still never reassigned, instead of an invalid compile-time `const`.
  - **Hardening** — these no longer emit invalid OpenQASM or crash the compiler; each is a precise
    diagnostic now: a qubit used where a classical value belongs (`if (q == 1)`, `Rx(pi/q)`, `0..q`),
    assigning to a qubit (`q = 5`), `!` on a loop variable, `var x = <bit>` mis-typed as an `int`, an
    argument of the wrong kind to a gate or operation, and a register size or index too large to
    represent (`Qubit[99999999999]`).
  - Backed by a new 129-case compiler unit-test suite (`tests/Qora.Tests`).

## 0.8.0

- Bundles the **Qora v0.14** compiler: `float` / `angle` classical types, parameterized register sizes
  `Qubit[n]` (monomorphized to a concrete copy per call site), name collisions that auto-resolve (with a
  `// Qora:` note) instead of erroring, and whole-operation `Adjoint` compiled by a dedicated
  inverse-synthesis pass.
- **Import-path editing**: typing `import "…"` now completes sibling `.qor` files and folders (like
  JS/TS import-path completion), `"` auto-closes, and an `import` snippet inserts the quoted-path form.
  `import` / `namespace` / `open` keywords and string literals are now syntax-highlighted.

## 0.7.2

- Fixed the Run setup dialog appearing several seconds late: the extension no longer spawns any
  process (compile, Python probes) before showing it — the consent dialog now pops instantly on
  click, and Python detection / provisioning happen afterward under the progress bar. It also
  prefers a suitable system Python before downloading anything.
- The setup progress bar now shows live activity during the long install steps (streaming the
  installer's per-package output) instead of a static message, so it no longer looks stalled while
  the ~200 MB simulator downloads.

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
  QSEM018/019/022/023), and `import` loads real files: `import "gates_lib.qor";`,
  `import "lib/gates.qor";`, and `import "a b.qor";` use quoted relative paths exactly as written,
  including the extension. Missing files are QSEM020; import cycles are QSEM021 with the full chain shown.
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
