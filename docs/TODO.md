# Qora — roadmap / TODO

Where Qora stands versus Q#, and what to build next. Priorities weigh **learning value**
(quantum-idiomatic and teachable) against **OpenQASM-3 feasibility** (Qora transpiles to OpenQASM 3,
so a feature that lowers cleanly to a QASM construct beats one that needs a whole compiler pass).
Source-grounded gap analysis, 2026-07-01.

Feasibility legend: **clean** = ~1:1 to a QASM construct · **workable** = doable with real front-end/emit
work · **hard** = needs a self-contained compiler pass · **not-expressible** = QASM has no equivalent
(erases at the target).

---

## ✅ Done — v0.8

- **Single-gate functors** — `Adjoint G(...)` → `inv @ g`, `Controlled G(...)` → `ctrl @ g`.
  Teaches the two defining facts of gates: every unitary is invertible (dagger) and any gate can take a
  control. (clean)
- **Richer conditions** — `== != < <= > >= && || !` in `if` / `while` / `repeat` (flat; OpenQASM
  re-parses precedence). Enables real measurement-feedback and counters. (clean)
- **`if` / `else` / `else if`** — both-way branching on a measured bit. (clean)
- **First-class `Reset` / `ResetAll`** — → OpenQASM `reset`. (clean)

Earlier: operations + C#-style params + calls, `use`/`Qubit`, gates (H/X/Y/Z/S/T/CX/CY/CZ/SWAP/CCX,
Rx/Ry/Rz), measurement (`var r: bit = M(q)`), classical vars (const/var/int/bit) + reassignment + arithmetic,
`for`-in-range, `while`, `repeat`-`until`. Emits OpenQASM 3.

Also shipped: **size-independent qubit-register parameters and classical arrays**. Helpers receive
`Qubit[]` and can iterate with `.Count`; the compiler specializes each call to its concrete register
size. Classical `int[]` / `float[]` / `bit[]` / `angle[]` values support literals, zero-initialized
`new T[N]`, indexed reads and writes, `.Count`, and mutable `T[]` helper parameters. Array storage is
declared at `Main` top level and lowers to OpenQASM general arrays. (clean)

## ⏸ Deferred to the ENGINE (not to be hacked around in Qora)

- **Block comments `/* */`.** Line comments `//` shipped in v0.9, but block comments still depend on
  the Janglim lexer's true longest-match support. Fix that capability in the engine; do **not** add a
  Qora-side pre-lexing string pass, which would blur the engine/language boundary and make source spans
  fragile.
- **Parser crash on very large inputs.** Found incidentally during a 2026-07-10 adversarial review's
  performance probe: a generated source with tens of thousands of statements makes the Janglim parse-tree
  path throw a raw `InvalidOperationException` ("Sequence contains no elements" — an internal
  `First()`/`Last()` on an empty sequence at scale) at `QoraParser.cs`'s `result.ToParseTree`. Report to
  the engine with a minimized repro; no Qora-side workaround.

---

## 🟡 MEDIUM — gateway features (bigger work)

- **Module system + namespaces** ⚛️ — ✅ SHIPPED in v0.12 (2026-07-03): `namespace` / `open` /
  qualified call targets resolve (Resolver.cs, QSEM018/019/022), `import` loads real multi-file programs
  (`SourceGraphLoader` owns path resolution, I/O, per-document syntax/lowering, and the immutable
  `ImportGraph`; `ModuleLoader` purely merges the prepared graph; QSEM020; CLI `--base-dir` /
  `--source-path`), and emission flattens namespace names (`MyLib.Bell` → `MyLib_Bell`) while
  auto-renaming only real collisions. Canonical document identities deduplicate cyclic and repeated
  paths without discarding imported-document source spans. The symbol-table machinery the
  effect-analysis step needs now exists. The QSEM013 follow-up also shipped: built-in gate names are
  relaxed Q#-style ("declaration allowed, ambiguous use is an error") with the built-ins living in the
  implicit `Qora.Intrinsic` namespace; the measurement family, `pi`/`tau`/`euler`, and global
  gate-named ops stay reserved.

- **`function` vs `operation`** ⚛️ — ✅ SHIPPED in v0.30: deterministic-classical `function` declarations
  coexist with effectful quantum `operation` declarations. The validator enforces pure function bodies,
  classical-only signatures, and the operation/function call boundary. The entry callable becomes the
  OpenQASM top-level body, while every non-entry function or operation lowers to a `def`.
- **Return values + typed returns** — ✅ SHIPPED in v0.30: functions declare one scalar
  `int` / `bit` / `float` / `angle` return type and use `return expr;`, while operations remain void.
  Return paths and result types are validated before MIR and target lowering.
- **Calls used as expressions** — ✅ SHIPPED in v0.30: `QCallNode` preserves nested function calls in
  values, conditions, arguments, and returns. Resolver binds each call to `CalleeOpId`; validation,
  monomorphization, MIR lowering, and OpenQASM emission consume that identity.
- **`bool` classical type** — `float` and `angle` have shipped, while a Boolean type distinct from the
  measurement-oriented `bit` remains. It maps directly to OpenQASM 3 `bool`. (clean)
- **`within { } apply { }` conjugation (auto-uncompute)** ⚛️ — run U, then V, then automatically
  `Adjoint U` — the U·V·U† scratch/ancilla pattern; one of the most important quantum lessons. OpenQASM
  has no conjugation construct, so Qora must synthesize `inv(U)` itself — feasible only once the
  single-gate `inv @` path exists (it now does) and only for fully-unitary `within` blocks. (workable)
- **Local / loop-scoped `use` (qubit allocation lowering)** ⚛️ — allow `use` inside subroutines and
  loops (Q#-style helper-owned ancillas), lifting QSEM012. OpenQASM has global-only qubit declarations,
  but a target limit must not be a language limit: a dedicated allocation-lowering pass (same family as
  the namespace mangling / `const` demotion that already compile away QASM gaps) maps local `use` onto
  global registers — static-bound loops unroll or reuse, subroutine locals reuse across calls. The
  correctness key is the **scope-exit |0⟩ guarantee** (reuse is legal only if the register is provably
  returned), so this SITS ON the uncompute return-semantics decision; the QSEM012 gate stays until the
  pass exists (its origin was exactly a silent-hoist bug — never hoist without the guarantee), and shapes
  the pass cannot handle yet are loud errors, never silent skips. Standing design rule, already in force:
  every analysis (events / liveness / verdicts / ContainerMap) is written placement-neutral, so nothing
  built for uncompute needs rework when this lands. Decided as a goal 2026-07-10. (hard)
- **Expression-sized qubit allocation** — loop bounds now accept expressions, and `Qubit[]` helpers can
  iterate with `.Count`. However, `use q = Qubit[N]` still requires a positive literal size; accepting a
  const/expression there needs compile-time evaluation before allocation. (workable)
- **Register measurement + `MResetZ`** ⚛️ — measure a whole register (`M(q)` → `c = measure q;`) and add
  a measure-and-reset combinator. Today a whole declaration or assignment RHS accepts one single-qubit
  reference, either `M(a)` or `M(q[i])`, and conditions also accept indexed `M(q[i])`; whole-register
  measurement and measure-reset remain. OpenQASM handles both natively. (clean)

## ⚪ LOW — later (limited near-term value / target limits)

- **Whole-operation Auto-`Adjoint` / Auto-`Controlled`** ⚛️ — auto-generate the inverse / controlled
  version of a *user operation* (Q#'s `adjoint auto` / `controlled auto`). The showcase Q# feature, but
  OpenQASM's `inv @` / `ctrl @` invert only a *single gate*, not a `def` — so it needs a self-contained
  compiler pass (reversibility check, reversed emission, per-gate inversion, synthesized `gate`).
  Expensive; do it only after the single-gate functors prove the modifier path. (hard)
- **`Result` type (`Zero`/`One`)** ⚛️ — a measurement-outcome type distinct from classical `bit`. Clarifies
  the quantum/classical boundary conceptually, but OpenQASM has no Result type (measurement yields `bit`,
  which Qora already uses correctly) — it would erase to `bit`, a Qora-only abstraction. (not-expressible)
- **`Pauli` type + Pauli-basis ops/measurement** ⚛️ — `PauliI/X/Y/Z` to select rotation/measurement bases.
  No native Pauli in OpenQASM; must desugar by case analysis (basis-change + measure + uncompute). Only
  after functors + richer measurement. (hard)
---

## Auto-uncompute — rung ④ injector prerequisites (2026-07-12 deep-dive on blanket+all-scratch)

- **#17 — wiring the injector to the EXISTING adjoint materialization.** A safe verdict on a register
  written by a CALL (e.g. `Bcast(a)`) promises injection of `Adjoint Bcast(a)`. The materialization
  machinery for that ALREADY SHIPS: `AdjointMaterializer` + `Inverter` (v0.14/v0.16) turn an explicit
  `Adjoint Foo(...)` call into a real `Foo__adj` operation — body reversed, statements inverted, loop
  ranges reversed — verified end-to-end by the existing functor/conjugation tests. What rung ④ must ADD
  is the wiring, not an inverter: emit the implicit `Adjoint <stmt>` calls at injection points (death
  point / after outermost container, LIFO) and ensure the materializer runs over compiler-generated
  adjoint references the same way it does over user-written ones. The verdict's clauses already fence
  the preconditions (no measurement — transitive flags; static loop bounds — language rule; bit
  conditions imply measurement ⇒ excluded), so everything verdict-SAFE is materializable. Note: Qora
  already performs Classiq-style COMPOUND INVERSION implicitly at call boundaries (a callee's internal
  loops are mirrored by inverting the whole call, which is why callee-internal containers correctly do
  NOT trigger ContainedWrite — only caller-side containers around the write do).

## Auto-uncompute — registered data gaps (from the requirements cross-check, 2026-07-11)

The rung-③ analysis (events + qubit graph + ContainerMap) answers the injector's questions except:

- **#11 — classical condition-bit flow.** Lifting the `ContainedWrite` block via conditional inverses
  (`if (r==1) { inverse }`) requires proving the condition bit unchanged between the compute and the
  injected inverse; classical bits are not in the event stream (only `Symbol.Uses`, thin). Fill when
  building the if-tools.
- **#14 — post-injection re-analysis.** Snapshot- and phase-qualified storage now exists through
  `HirSemanticArtifactId(HirSnapshotId, HirSemanticPhase)`. Rung ④ must publish the injected tree as a
  new HIR snapshot, record its lineage, and wire validation/effect analysis to produce new artifacts
  over that exact snapshot instead of reusing the pre-injection facts.
- **#16 — ancilla-identification conditions coupled to FUTURE features** (2026-07-11 literature
  cross-check: Silq PLDI'20, Q#, Unqomp PLDI'21/Reqomp, Twist POPL'22, Quipper, Bennett'73, Gidney'18 —
  20/20 key claims source-verified). `IsCleanupCandidate`'s two conditions (`IsAncilla` use-birth + never
  measured) are provably COMPLETE for today's quantum feature set (operations remain void, functions
  are classical-only, and there are no general qubit aliases or closures: measurement is the only
  value-escape channel). Future escape channels add conditions to the CANDIDACY layer
  (`IsCleanupCandidate`); the birth layer (`IsAncilla`) is feature-invariant. Add each in the SAME change:
  - **Future qubit-returning values or other qubit escape channels** → add "not returned / does not
    escape" (Silq: return consumes; Bennett: outputs are copied out of scratch). Current functions are
    classical-only, so the shipped scalar return feature does not create this escape channel.
  - **Future general qubit aliasing beyond the current checked call contracts** → add "no live alias";
    long-lived borrowed aliases split ancillas into clean (end |0⟩, value-verifiable) vs **dirty**
    (end = original unknown state — verifiable only by structural compute/uncompute pairing à la
    within/apply, never by value reasoning). Q# precedent.
  - **Measure-reset reuse** (`MResetZ` idiom) → "never measured" must become per-LIFETIME-SEGMENT
    (Reset ends one segment, starts a fresh |0⟩ one). Today: whole-register disqualification = sound
    over-rejection.
  - **Closures** → forbid qubit capture (Silq's choice) or add escape analysis.
  - Note on principle: "measured ⟹ not scratch" is a correct LIVENESS test today (user `M` outcomes flow
    into program data; deferred-measurement principle makes the register an output wire), but it is NOT
    part of the literature's ancilla definition — Gidney-style measurement-based uncomputation MEASURES
    the ancilla as its cleanup (X-basis + classical fixup). If rung ④ ever adopts that gadget, the
    compiler-emitted cleanup measurement must not disqualify the register it is cleaning.
    **DECIDED (2026-07-12): rung ④ v1 = adjoint injection ONLY.** Measure-and-fixup is a fault-tolerant
    cost-model optimization (zero T-gates) with no benefit on Qora's current targets (simulators — no
    T-gate premium) and three costs (mid-circuit measurement + real-time classical feedback requirement,
    nondeterministic execution path, a whole new semantics to verify). Revisit only when targeting FT
    backends; until then this entire bullet is dormant.
  - Escape hatch precedent: Silq ships explicit unsafe `forget(x := expr)` (witness-based, opt-in, loud)
    for legitimately-uncomputable-by-witness patterns the checker rejects — the acceptable shape if
    rule-(B) errors ever prove too strict in practice; a silent skip stays forbidden.
- **#15 — Y/CY taint refinement (precision, not soundness).** Rung ③ now blocks EVERY Y/CY write as
  non-qfree (matching Silq), which conservatively over-rejects a Y/CY whose target is a *definite basis
  value* (a |0⟩-rooted classical chain), where the phase is global and the adjoint undoes it cleanly. A
  taint pass over the qubit graph — a node is superposition-tainted iff it is a param seed, born of an
  H/Rx/Ry write, or has a tainted parent — could re-admit a Y/CY write whose parents are all untainted.
  Sound (untainted ⟹ definite basis value ⟹ global phase) and validatable by the round-5 fuzzer (its
  Y/CY-removed control already ran clean). Deferred deliberately: matching Silq is the correct minimal
  fix, and a new taint subsystem is bug surface better added under test than unsupervised.
- **#18 — facts require revision-qualified identities; names never cross IR domains (invariant,
  2026-07-14, architecture hardened 2026-07-27).** A rung-③ regression keyed the
  non-invertible-call set by operation name. Names change across specialization and target mangling, so
  a fact built in one tree could silently miss when queried against another. The durable rule is now
  stronger than “use a node Id”: a reference crossing HIR generations uses
  `HirNodeRef(HirSnapshotId, NodeId)`, a semantic symbol uses
  `HirSymbolRef(HirSemanticArtifactId, SymbolId)`, and callable-local MIR identities use composite
  references such as `MirValueRef(MirSnapshotId, MirCallableId, MirValueId)`. HIR lineage lives in
  `HirCompilation.Lineage`, outside `HirSemanticModel`, while `Compilation.Links.Hir` exposes that same
  exact instance and OpenQASM emitted names live only in
  `OpenQasmSymbolMap`. Source names remain lookup/diagnostic data at the edge; they are never analysis
  identity.
- **#19 — bind calls to their callee by node reference, not by name — ✅ SHIPPED (2026-07-14, working tree).**
  Statement calls (`QGate`) and expression calls (`QCallNode`) carry `int? CalleeOpId`. Resolver binds a
  user call to its callee's stable node Id once, and monomorphization re-points the reference to the selected
  specialization. `AdjointMaterializer` first synthesizes and re-Ids the inverse operation, then rewrites
  `Adjoint Foo` to the inverse name **and** its minted `CalleeOpId`; built-ins remain null. Validation,
  effect analysis, MIR lowering, target referential checks, name mangling, and emission follow the typed
  reference and fail loudly on a missing or dangling Id. `IrPrinter` may display the readable call name,
  but that spelling is no longer semantic identity.
- **#20 — immutable compilation snapshots and explicit stage ownership — ✅ SHIPPED
  (2026-07-27); incremental invalidation remains future work.** `QoraParser.Parse` is syntax-only and
  `QoraCompiler.Compile` returns one immutable `Compilation` whose authoritative top-level owners are
  `Sources`, `Hir`, optional `Mir`, `Links`, `Targets`, and stage-qualified `Diagnostics`.
  `CompilationSession` owns branch-safe monotonic revision allocation, and each result records its
  actual `ParentRevision`; sibling recompiles therefore cannot collide. `CompilationOutputPlan`
  separately fixes the exact canonical HIR goal, MIR request, and backend set, and a successful result
  must match that plan without unsolicited artifacts.
  `SourceGraphLoader` preserves every root/import document as an exact
  `SourceDocumentSnapshot` + immutable Qora-owned `SyntaxSnapshot`, records revision-qualified
  `ImportGraph` edges, and lowers each successfully parsed document with a document-qualified
  `SourceSpan`. Mutable Janglim AST exists only in a transient internal `SyntaxParseProduct` and is
  discarded immediately after lowering. `ModuleLoader` performs only the deterministic merge of that
  prepared graph and returns a `HirNodeIntroduction` for every imported HIR node.
  A `HirSnapshot` owns structural `QProgram` state, while validation and effect facts are separate
  snapshot-qualified semantic results. A validation artifact owns one authoritative
  `HirValidationOutcome` with accepted/rejected status and immutable diagnostics; effect analysis can
  consume only the exact accepted validation artifact for the same snapshot and records that
  `ValidationBasis`. `HirCompilation` owns both its exact snapshot history and
  `HirLineage`; `Compilation.Links.Hir` is the same lineage instance. Every newly appearing HIR node is
  classified exactly once as an identity-preserving `NodeDerivation`, provenance-only `NodeSynthesis`,
  or source `HirNodeIntroduction`. Analysis never creates a fake structural HIR stage, and canonical
  milestones cannot move backwards.
  `MirSnapshot` owns its structural index and lazily cached analysis store; shared dependencies such as
  CFG are reused within that snapshot. MIR callables and entities use exact HIR/semantic references.
  Every HIR symbol has one `MirSymbolLoweringDisposition`, while every MIR value/storage/qubit has one
  `MirEntityOriginKind`, so non-lowering and compiler temporaries are explicit rather than missing.
  Target results live in the backend-keyed `TargetArtifactSet.Artifacts` map. `QasmBackend` consumes an
  exact `HirSemanticContext` and produces `OpenQasmTargetProgram`; `OpenQasmArtifact.Text` is derived
  from that model. The current artifact records both its exact HIR source and semantic basis instead of
  pretending to be MIR-backed, while the common `ITargetArtifact` contract does not force HIR
  provenance on future backends. Target diagnostics likewise carry a backend plus a typed HIR-or-MIR
  input union. The CLI renders `--stages` from this completed aggregate without rerunning passes.
  This revision-bound model is the required foundation for IDE queries and future incremental
  compilation, but document dependency invalidation, snapshot reuse, and separate compilation are
  **not implemented yet** and must not be described as unnecessary.

## Sequencing note

Updated 2026-07-27. The module system, typed functions and returns, expression calls, float/angle
types, effect analysis, ownership contracts, MIR, and immutable compilation snapshots have landed.
The main dependent track is now **automatic uncomputation**: the injector must consume the existing
effect/liveness facts, insert identity-bound adjoint calls, preserve the scope-exit |0⟩ guarantee, and
publish a new HIR snapshot with the correct typed lineage classification rather than mutate an earlier
snapshot. Local allocation lowering
depends on that guarantee. Incremental invalidation and IDE query indexes should extend the existing
document identities, typed cross-stage links, and source maps; they must not introduce a second mutable
ledger of semantic facts. `bool`, automatic controlled-operation generation, and the target-erased
`Result` / `Pauli` abstractions remain later language work.
