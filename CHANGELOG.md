# Changelog

Release notes for the **Qora language** — its grammar (`QoraGrammar`) and compiler pipeline
(`src/Qora.Core/Ir/`: lowering, validation, inversion, OpenQASM emission). This tracks the language
itself; the VS Code extension is versioned separately in [`vscode/CHANGELOG.md`](vscode/CHANGELOG.md).

Qora is a Q#/C#-flavored quantum learning language built on the
[Janglim](https://www.nuget.org/packages/Janglim) parser engine: source is parsed into an AST, which is
emitted as **OpenQASM 3.0**.

> **Note:** Qora was renamed from **Ket** on 2026-07-01 (a "Ket" extension already existed). Versions
> 0.1–0.7 below were authored under the old name.

## 0.25 — 2026-07-21

### Changed
- **Type annotations move to trailing position (`name: T`).** A parameter is now written `q: Qubit[]`,
  `n: int`, `a: float[]`; a declaration is `var x: int = 5` or `const a: int[] = [1, 2]`. The type stays
  optional — `var x = 5` and `const c = M(q[0])` still infer it from the initializer — but when written,
  it always follows the name after a `:`. This matches Q#, Rust, Kotlin, Swift, and TypeScript, and reads
  the type left-to-right with the name it describes. The `:` is a structural separator excluded from the
  AST (like `=`), so the compiler's order-independent lowering, every validation pass, and OpenQASM
  emission are unchanged — the emitted QASM is byte-identical to the leading-syntax era.
- **The leading forms (`int n`, `Qubit[] q`, `bit r = M(q[0])`) are removed.** There is exactly one way
  to write a type annotation. Existing programs update mechanically: move the type after the name and add
  a `:`, and prefix a keyword-less typed declaration with `var` (`bit r = …` → `var r: bit = …`).

## 0.23 — 2026-07-21

### Added
- **Array locals live anywhere a scalar does** — a helper operation, a loop, a branch. OpenQASM requires
  arrays at global scope and hides mutable globals from `def` bodies, so the backend now absorbs the
  target rule instead of the language exporting it (`QSEM012` no longer restricts array placement): a
  def-local `int[]`/`float[]`/`angle[]` is threaded as a hidden `mutable array[T, #dim = 1]` reference
  parameter backed by a global (declaration site becomes per-entry element-wise re-initialization, so
  "fresh value on every entry" is preserved; intermediate defs pass the reference through), and a
  `bit[]` declared inside a control-flow block hoists to the top of its own scope
  (`Ir/Qasm/ArrayLocalHoisting.cs`).
- **`bit[]` parameters are length-specialized per call site**, exactly like `Qubit[]`, and emit as the
  sized register `bit[N]` — the only parameter form OpenQASM allows for bits (the previous
  `array[bit, …]` emission was spec-invalid). Writing to a `bit[]` parameter is rejected (`QSEM032`):
  bit registers pass by value, so a write would be silently invisible to the caller — unlike the other
  array parameters, which are mutable references. A const-bounded guard (`if (n < 2)`) and a
  cross-array `0..f.Count-1` loop bound over a `bit[]` parameter now defer to the post-specialization
  re-check instead of being rejected outright.

### Changed
- **Whole bit-register comparisons emit through the spec cast**: `if (r == 0)` on a `bit[N]` becomes
  `if (int(r) == 0)` — the one spelling the Braket oracle actually executes (both the bare form and the
  earlier bool-literal rewrite fail there). Element comparisons (`r[0] == 1`) are unchanged.
- **The fan-in idiom no longer false-positives the aliasing check**: `for i in 0..q.Count-2
  { CNOT(q[i], q[4]); }` on a generic register defers `QSEM014` to the post-specialization re-check,
  which knows the loop's real range — a genuinely overlapping operand is still rejected there. An
  uncalled generic operation keeps the conservative pre-specialization judgement (its re-check never
  runs).
- **Emitted parentheses respect associativity**: a same-precedence right operand is parenthesized
  (`n == (m == 0)` no longer re-reads as `(n == m) == 0`), completing the minimal-parenthesis rule that
  already handled precedence.
- **`SemanticModel` records two new fact families**: `DeferredSizeChecks` — every size-dependent
  verdict the pre-specialization validation postponed (the deferral ledger; empty on the surviving model
  of every successful compile), and `WillBeRechecked(opId)` — whether the post-specialization
  re-validation will run for an operation (false for a generic op nothing reaches).

### Fixed
- **Deep expressions can no longer crash the compiler.** Parsing runs on a wide-stack worker, an
  over-deep program is a clean `QSEM031` (with no partial IR exposed to `--stages`), and array-literal
  elements count toward the same depth limit — previously a machine-generated deep expression could
  overflow the process stack.
- **A measurement inside an array-literal initializer participates in effect analysis** (`bit[] r =
  [M(q[1]), 0]` marks the qubit measured), so the uncompute planner can no longer misjudge an entangled
  qubit as a safe cleanup candidate.

## 0.24 — 2026-07-21

### Fixed
- **Array-local hoisting mints collision-proof names.** Every backing global, hidden parameter, and
  pass-through parameter the hoisting pass introduces is a unique placeholder carrying the desired
  human-readable base name; the name mangler then turns each into a pretty, collision-free emitted name.
  Because the placeholders are distinct by construction, the mangler never merges two unrelated arrays
  (its same-name unification is meant for genuine same-variable locals), and its per-name freshening
  splits two arrays that want the same base into `x` / `x_` and separates any clash with a user variable,
  operation, or gate name — with references renamed in lockstep. This replaces the v0.23 scheme that
  reserved names against an enumerated scope, whose incompleteness had let a minted name collide with a
  loop variable or a nested declaration; nine collision vectors are now covered and Braket-verified. When
  a hidden parameter's base clashes with a same-named parameter it shadows, only the array's own in-scope
  references are rewritten to it.
- **A measurement lowered out of a condition can no longer mask an undeclared-name error.** The synthetic
  `bit` a measurement-in-condition desugars to (`if (M(q[0]) == 1)` → `bit __m0 = M(q[0]); if (__m0 == 1)`)
  is now minted as an internal placeholder, so a user's own undeclared `__m0 = …` no longer silently binds
  to it and keeps its `QSEM025` diagnostic. Emitted names are unchanged.

## 0.22 — 2026-07-18

### Added
- **Every array and register index is proven in bounds at compile time (rung B′).** An index is accepted
  only with a proof: a literal within a known length, a loop over `0..a.Count-1` (safe for any length), a
  constant-bounded loop, a call-site minimum-length precondition for a classical-array parameter, or a
  programmer guard `if (0 <= n && n < a.Count) { … }` that narrows a runtime index in its then-branch. An
  index that is provably out of range is `QSEM016`; one that cannot be proven either way is `QSEM030` —
  OpenQASM 3 has no runtime bounds check, so an unprovable index cannot be deferred to run time. The bound
  arithmetic is evaluated (`+ - * /`, `const` names, `.Count`) in 64-bit checked arithmetic, so
  `for i in 0..a.Count*2-4` is judged by its actual value.
- **A name may not be used before its declaration in its own scope (`QSEM025`, point-of-declaration
  scoping).** A later same-name `const`/`var`/measure-bit shadows an outer binding; using the name earlier
  in the same block — where it still means the outer binding — is now rejected rather than silently read
  two different ways by the validator and the emitted code.
- **A measurement into a non-`bit` array-literal element is rejected (`QSEM017`),** and measure-target
  indexes inside conditions and array-literal initializers are bounds-checked like every other index.

### Changed
- **Expressions are parsed to a tree once, at lowering.** Bounds, indices, conditions, and guards are
  read from a structured `QNode` tree (`Ir/ExprTree.cs`) instead of being re-parsed from text by each
  consumer. One evaluator folds a bound or index, one reader narrows a guard, one walker finds every
  indexed access — so no two readings of one expression can disagree, and a `const` carries its own
  folded value (`const hi = q.Count` is transparent to the folder).
- **`SemanticModel` carries the bounds-proof facts as data.** `UnprovenIndexes` records each access that
  could not be proven (the source each `QSEM030` is derived from), and `RequiredArgLengths` records each
  operation's classical-array-parameter minimum lengths — the same table the call-site check reads.
- **Gate-operand distinctness (`QSEM014`) compares indices by value, not spelling.** `CNOT(q[k], q[2])`
  with `const int k = 2` is caught as the same qubit twice, as is a loop variable whose range reaches a
  literal operand.
- **Cyclic import back-edges are now harmless.** `ModuleLoader` registers each canonical path in
  `loaded` before file I/O, so both a cycle and a diamond reuse return `false` from `loaded.Add(full)`
  and skip only that import edge. `QSEM021` is retired/reserved; missing or unreadable imports remain
  `QSEM020`.

## 0.21 — 2026-07-16

### Changed
- **Qubit array parameters now use the size-independent source type `Qubit[]`.** Helpers inspect
  `q.Count`; monomorphization specializes each call to its actual register size and replaces `Count`
  with the concrete integer. `Qubit[N]` remains the allocation form in `use q = Qubit[N]`, but is no
  longer accepted in an operation signature.
- **Qubit and classical type keywords remain explicit in the Janglim semantic AST.** Array parameter
  types are represented by an `ArrayType` node, so `QoraLowering` can map both `Qubit[]` and classical
  `T[]` directly to `QParam.IsArray` without reconstructing a missing type.
- **Indexed source references now use the type-neutral semantic AST node `IndexAccess`.** The syntax
  `name[index]` no longer produces a misleading `Qubit` node before type resolution; semantic analysis
  still determines whether the base symbol is a qubit register or a classical array.

### Added
- **One-dimensional classical arrays** for `int[]`, `float[]`, `bit[]`, and `angle[]`: explicit
  literals (`[1, 2]`), zero-initialized allocation (`new int[3]`), indexed reads and writes, `.Count`,
  and mutable array parameters. Declarations are restricted to the top level of `Main`; helper
  operations receive arrays through parameters, and overlapping mutable array arguments are rejected.
- **OpenQASM 3 general-array emission:** `int[]`, `float[]` and `angle[]` declarations use
  `array[T, N]`, helper parameters use `mutable array[T, #dim = 1]`, and `.Count` lowers to
  `sizeof(array)`.
- **`bit[]` emits an OpenQASM bit register, `bit[N]`.** Bit is the one element type OpenQASM forbids as
  an array base type ("bit, bit[n] and stretch are not valid array base types") — it has a dedicated
  register type instead. So a `bit[]` declaration emits `bit[N]`, and its `.Count` folds to a literal
  rather than `sizeof`, which is likewise undefined on a bit register. A `new bit[N]` spells its
  zero-initialization out as `bit[N] name = "0…0";`, because a bare `bit[N] name;` is undefined rather
  than zeroed; an array literal is written element by element, never as a bitstring, since the spec puts
  element 0 at the least-significant end while simulators read it at the leftmost.

### Fixed
- **A literal index outside an array's bounds is now caught through a parameter, not only inline
  (QSEM016).** An array parameter carries no length of its own — the length arrives with the argument
  and differs per call — so a literal `x[5]` in an operation body states a precondition on its callers:
  *the array passed here needs at least 6 elements*. That minimum is computed per parameter, folded
  through call chains, and checked at every call site against the argument's known length. Previously
  nothing related the two, so `a[5]` on a three-element array was rejected when written inline but
  silently emitted once the same access was factored into a helper. Dynamic indices are unaffected and
  remain a runtime concern.

## 0.20 — 2026-07-14

### Added
- **Rung ④ of the automatic-uncomputation ladder — the cleanup PLAN builder**
  (`Ir/Passes/UncomputePlanner.cs`). For one safely-uncomputable ancilla,
  `UncomputePlanner.Plan(model, inverter, op, q)` computes its cleanup — the adjoints of the gates that
  wrote the ancilla, in reverse program order — anchored at the ancilla's rung-② death point, WITHOUT
  touching the IR (splicing is a later rung-④ step). The |0…0⟩ birth is excluded, and one statement that
  writes several elements of the register (e.g. `SWAP(a[0], a[1])`) is inverted once, not once per element.
- **Statement-Id → statement lookup** (`Ir/Passes/StmtMap.cs`) — resolves a qubit event's `StmtId` back to
  its gate node, sharing `ContainerMap`'s single tree walk so a new container type is taught in one place.
- **`QGate.CalleeOpId` — reference binding for calls.** A user-operation call now carries a stable node
  reference to its callee, bound once at name resolution (`Resolver`) and re-pointed to the size
  specialization at monomorphization. Effect analysis follows the reference instead of re-matching the
  callee name (which shifts across monomorphization/mangling) — the standard "resolve once, reference
  after", removing name-matching from the analysis middle.

### Fixed
- **Rung ③ no longer certifies an ancilla whose write it cannot actually invert** (`SemanticModel.cs`,
  `EffectAnalysis.cs`). A write that is a call to an operation the inverter cannot statement-adjoint — a
  `while`/`repeat` of runtime-unknown count, classical mutation, or a local `use` in the callee — is
  blocked by a new `UncomputeBlocker.NotInvertibleCall` (shown in the `--stages` uncompute view). This
  closes a soundness gap (a runtime-unknown loop count hidden behind a call was previously deemed safe) and
  keeps the safety verdict in agreement with the inverter. Keyed by the call statement's stable Id, so it
  answers correctly on the pre-monomorphization and analyzed trees alike.

## 0.19 — 2026-07-12

### Added
- **Rung ③ of the automatic-uncomputation ladder — the full safety verdict**
  (`Ir/Passes/SemanticModel.cs`). `UncomputeSafety(op, q)` answers "can this register/element be
  returned to |0⟩ by injecting the adjoint of the statements that wrote it, without touching any
  surviving qubit?" with a reason and a culprit statement: `Irreversible` (reset, or a call that
  transitively resets/measures), `NonQfreeWrite` (a superposition write — H/Rx/Ry — **or a
  phase-carrying permutation write — Y/CY**, whose basis-value-dependent phase becomes a
  survivor-relative phase under entanglement that the injected adjoint would strip;
  state-vector-verified, matching Silq's `qfree`), `ContainedWrite` (a write inside an
  `if`/loop/conjugation — a straight-line adjoint cannot mirror conditional/repeated execution;
  callee-internal containers are fine because a call is inverted as a whole), and
  `CoWrittenPartner`/`SourceDied` (well-sourced compute: co-written partners such as SWAP operands
  block outright, a blanket write wider than an element query blocks with only the `use` birth
  exempt, and every recorded source must survive unchanged to the death point — extended to the
  outermost container's end when the death is contained). `IsSafelyUncomputable` is the boolean view.
- **Two-layer classification, one term per concept**: `IsAncilla` (the BIRTH question — a `use`-born
  workspace register, answered from graph facts: parameters carry seeds, use registers carry hoisted
  births) and `IsCleanupCandidate` (the LIVENESS question — an ancilla never measured, directly or
  transitively; a measured ancilla is *promoted to an output* and leaves the cleanup pool).
  Measurement is ruled at the candidacy layer and relayed by the verdict (`Measured`,
  `NotACleanupCandidate`) before any safety clause runs.
- **Qubit graph** (`QubitNode`/`QubitEdge`): a per-operation value-genealogy DAG recorded by effect
  analysis in the same pass as the event stream — nodes are value versions, parent edges say what
  each value was made from (own previous version, read sources, co-written partners) with `Via`
  preserving the access breadth of blanketed reads. Guarded by an always-on coherence sweep that
  re-derives every link and parent set each compile and fails loud (QINTERNAL) on any mismatch;
  the sweep itself is falsifiable through an internal test seam (13 negative tests).
- **Hoisted register births**: `use` births are recorded at the front of the event stream, mirroring
  the emitter's declaration hoisting — a gate may textually precede its register's `use`.
- **`--stages` gained an `uncompute` view**: every qubit classified (input / ancilla promoted to
  output / cleanup candidate with its verdict), per-element blocked reasons under a blanket headline,
  and live ranges. `ContainerMap` pass added; `QubitEvent` gained `Irreversible`/`NonQfree`/`NodeId`.
- SemanticModel analysis stores are **add-only** (a second analysis of the same op fails loud).

### Docs
- New 11-page auto-uncomputation course (`docs/architecture/ko/uncompute-01…11.html`) — ancilla &
  cleanup candidates, the ledger model, gate edits, the qfree contract, why Y/CY are blocked, the
  injector, loops vs calls, the blanket problem, principled vs conservative blocks, the two runtimes,
  and the 2026 language landscape — linked from the datasheet, one chapter per page.

## 0.18 — 2026-07-09

### Changed
- **Rung ① of the automatic-uncomputation ladder now stores a per-operation, program-ordered
  *qubit-event stream* instead of per-statement touched/modified sets (`Ir/Passes/EffectAnalysis.cs`,
  `Ir/Passes/SemanticModel.cs`).** The old `StmtEffects(Touched, Modified)` record and its
  statement-Id-keyed `SemanticModel.AddEffects` / `FindEffects` map are gone. In their place, effect
  analysis emits, for every *leaf* statement (a gate, a measurement, or a `use`), one
  `QubitEvent(QubitRef, QubitEventKind, Order, StmtId)` per qubit operand — `Read` (a control or
  diagonal-gate target: basis value preserved), `Write` (a target / reset / register birth: value may
  change), or `Measure` (collapsed, irreversible) — collected into one ordered stream per operation and
  stored keyed by `op.Id`. `Order` is a per-operation program-order index; all events of one statement
  share that statement's `Order` and `StmtId`. The two views the later rungs need are both *read off*
  this single stream: a statement's old touched/modified sets by filtering on `StmtId`, and the qubits
  that interacted at a statement (its entanglement edge) by grouping on it. Containers
  (`if` / `for` / `while` / `repeat` / `within…apply`) emit no events of their own — their leaf children
  carry the precise per-gate detail. This is rung ① in its final storage form; the per-statement record
  was an interim shape.
- **Rung ② liveness is now a *derived query*, not a stored pass (`SemanticModel.LiveRange`).**
  `LiveRange(opId, qubit)` returns the `[Birth, Death]` events bracketing a qubit's life inside an
  operation — birth is its earliest event by `Order` (a `use` register's birth `Write`, or a parameter's
  first use); death is its latest event of *any* kind (a final control `Read` counts — the qubit still
  holds a value to be cleaned after it). It is computed on demand as min/max `Order` over the event
  stream, storing nothing, and is subsumption-aware through a new `QubitRef.Overlaps` (a whole-register
  effect `{q}` and an element `{q[0]}` overlap both ways). Death is the point after which the future
  *inject* rung may place an uncompute.
- **Previously-silent under-approximations in effect analysis are now loud invariant checks.** The branch
  that used to `return` empty effects for a name that is neither a user operation nor a built-in gate now
  throws (empty effects would make the statement look like it touches nothing). Three positional-projection
  invariants the earlier validation passes already guarantee now throw `InvalidOperationException` rather
  than silently mis-mapping or dropping effects: a user-op call carrying a non-`Adjoint` functor (QSEM002),
  a call whose argument count differs from its parameter count (QSEM006), and an indexed effect leaking
  into a single-qubit binding (QSEM016).

### Docs
- **Documentation reorganized into two separated tracks, plus a new compiler datasheet.** The per-language
  pages now live under two trees — `docs/architecture/{lang}/` (the pipeline overview, the H-gate intro,
  and the `Adjoint`-pipeline walkthrough) and `docs/book/{lang}/` (the 15-chapter learning book) — with
  **no cross-links between them**; the README is the only gateway into each, reached through the top-level
  `architecture.html` / `book/` landing redirects. A new **compiler datasheet**
  (`docs/architecture/ko/datasheet.html`, Korean-only for now) puts the whole pipeline, the pass catalog,
  the IR node reference, the persistent `SemanticModel`, the symbol table and the two name domains, effect
  analysis and the qubit-event model, the automatic-uncomputation ladder, the QSEM diagnostics, and the
  version history on one page. The pre-localization flat redirect stubs under `docs/` were removed and the
  README docs section was condensed from four standalone links to the two tracks.

### Tests
- +5 cases — `QubitRef.Overlaps` subsumption in both directions; entanglement-edge grouping by `StmtId`;
  and `LiveRange` birth-to-last-use, a death that is a final control `Read`, and a null range for an unused
  qubit — all in `EffectAnalysisTests.cs`, whose existing cases were rewritten to query the event stream →
  168 total. This is internal analysis infrastructure: the `QubitEvent` stream feeds no pass yet, so there
  is no new surface syntax and emission is unchanged — demo QASM is byte-identical to 0.17 apart from the
  version-stamp comment.

## 0.17 — 2026-07-09

### Added
- **Effect analysis — rung ① of the automatic-uncomputation ladder (`Ir/Passes/EffectAnalysis.cs`).**
  A pure, diagnostics-free pass over validated, monomorphized IR computes, for every statement, which
  qubits it *touched* (any operand, controls included) and which it *modified* (computational-basis value
  may change), plus a per-operation summary over the formal qubit parameters (with an `Irreversible` flag
  when the body measures or resets). Results are stored on the `SemanticModel` keyed by stable node Id, so
  tree-copying passes still find them through the `DerivedFrom` chain. The gate table now records
  `Controls` (leading control slots — steered, never value-changed) and `Diagonal` (phase-only gates —
  targets keep their 0/1 value), so controls and diagonal targets are correctly *touched but not
  modified*. This is the use/def foundation the liveness / qfree rungs build on.
- **Operations are first-class symbols in one connected symbol table.** An operation is now a
  program-level symbol (kind `Operation`), inserted through the *same* single `Scope.TryAdd` door every
  parameter / register / variable uses — no side path. The program scope is the parent of each
  operation's body scope, so the whole program is one connected symbol-table tree: `FindSymbol(op.Id)`
  resolves an operation, its `Uses` accrue one entry per call site, and the `--stages` symbols view shows
  each op as an `operation`. Using an operation name as a *value* (`var x = Foo`, `Foo = 5`, an op in any
  expression / argument slot) is now the precise **QSEM028** ("an operation, not a value — it can only be
  called"), replacing the misleading "not declared" it produced before.
- **Both name domains are explicit on the semantic model.** A declaration's *source* name (what the user
  wrote, frozen at validation — `Symbol.SourceName`) and its *emitted* name (what the QASM says, recorded
  by `NameMangler` — `SemanticModel.FindEmittedName`) now each have exactly one home, joined by stable
  node Id. `Symbol.Name` was renamed to `SourceName` so every read states which domain it means, and the
  mangler records the emitted name for every declaration (renamed or not) — so a null lookup means "the
  mangler has not seen this node", never "unchanged".

### Tests
- +23 cases (effect analysis; emitted-name recording; operation-as-symbol + use sites; QSEM028;
  size-param/op-name collision) → 163 total.

## 0.16 — 2026-07-08

### Added
- **`within/apply` conjugation in the IR (`QConjugate` + `Ir/Passes/ConjugationLowering.cs`).** The
  compute–act–uncompute pattern (U V U†) is now a first-class IR construct: the lowering pass flattens
  each conjugation into the `within` statements, the `apply` statements, and a synthesized inverse of
  `within` — installed *before* `AdjointMaterializer`, so every synthesized `Adjoint` becomes a real
  inverse def that flows through the mangler's collision resolution. A `within` block that cannot be
  inverted (measurement, assignment, `use`, `while`/`repeat`) is a clean, span-carrying **QSEM027**, and
  emission is gated — an uncompute is never silently dropped. No surface syntax yet (that is the next
  step); this is the foundation the automatic-uncomputation ladder builds on.
- **Stable node identity + a persistent semantic model.** Every IR statement, operation, and parameter
  now carries an `Id` minted at construction and preserved through `with` rewrites (C# record semantics:
  `new` = fresh identity, `with` = same node, edited). The symbol table built at the final validation is
  no longer discarded and rebuilt per consumer — it is packaged as an Id-keyed **`SemanticModel`**
  side table (`Ir/Passes/SemanticModel.cs`) and carried to the end of the pipeline, Roslyn-SemanticModel
  / rust-analyzer-AstId style: build once, query everywhere. The emitter's variable-type seeding and the
  `--stages` symbols view now consume the model instead of re-deriving it, which joins a node's FINAL
  (post-mangling) name to its validation-time facts by Id. Subtree-copying passes (`Monomorphizer`,
  `ConjugationLowering`, `AdjointMaterializer`) re-mint Ids via a new **`ReId`** helper, recording
  `DerivedFrom` lineage so a copied statement still resolves to its source's symbol. This model is the
  designated result store for the upcoming effect analysis (qfree / liveness).
- **Id-uniqueness safety net.** `ReferentialCheck` now sweeps the whole final tree for duplicate node
  Ids — a pass that copies a subtree without `ReId` fails loudly as `QINTERNAL` instead of silently
  corrupting every Id-keyed table.
- **`--stages` reports `symbols`** — the validation-time symbol table (scopes, kinds, types, const
  values, use counts), rendered from the persisted model.

### Tests
- +11 cases (conjugation lowering; node identity, lineage, and pipeline-wide Id uniqueness) → 140 total.
  The refactor is observably invisible: demo QASM and every `--stages` field are byte-identical to 0.15.

## 0.15 — 2026-07-06

### Added
- **Measurement inside a condition**: `if (M(q[0]) == 1)` (and `while` / `repeat … until`) now works,
  Q#-style — a new `Ir/Passes/MeasureConditionLowering.cs` hoists the measurement into a `bit` and
  tests that, so the emitted OpenQASM is valid.
- **Measure bits are block-scoped** like `var`/`const`; using one after its block is **QSEM025**.
- **`const` accepts any immutable value** (literal, runtime variable, measurement — Q#-`let` style).
  A runtime-bound `const` is emitted as a plain never-reassigned variable by a new OpenQASM
  target-lowering pass (`Ir/Passes/OpenQasmLowering.cs`), since OQ3 `const` is compile-time only.
- **Unified per-slot argument-kind checking** for built-in gates AND user operations via one
  `ICallableSig` signature.
- **Hardening** — each of these is now a precise diagnostic instead of invalid QASM or a crash: a qubit
  in a classical-value position or as an assignment target (**QSEM026**), `!` on any classical value
  including loop variables, `var x = <bit>` inferred as `bit` (not `int`), and an oversized register
  size/index (clean QSEM016 instead of an `int.Parse` crash).

### Tests
- New `tests/Qora.Tests` xUnit project: 129 cases over the whole front end.

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
  `import "gates_lib.qor";`, `import "lib/gates.qor";`, and `import "a b.qor";` all use the quoted
  relative path exactly as written, including the extension. Loading is transitive with
  diamond-sharing (each file loads once); a missing/unreadable file is **QSEM020**,
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
- **Module-system grammar (in progress).** `import "path.qor";`,
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
