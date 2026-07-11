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
Rx/Ry/Rz), measurement (`bit r = M(q)`), classical vars (const/var/int/bit) + reassignment + arithmetic,
`for`-in-range, `while`, `repeat`-`until`. Emits OpenQASM 3.

## ⏸ Deferred to the ENGINE (not to be hacked around in Qora)

- **Comments `//` and `/* */`.** Blocked by the Janglim lexer, which only *fakes* longest-match: a single
  `/` operator out-prioritizes the `//` / `/*` comment pattern, so `//` lexes as `/` `/`. This is the
  documented "true longest-match" lexer TODO in `AJPGS/docs/TODO.md`. Fix it in the engine; do **not**
  work it around in Qora (e.g. pre-lexing string passes) — that would blur the engine/language boundary
  and make Qora fragile.
- **Parser crash on very large inputs.** Found incidentally during a 2026-07-10 adversarial review's
  performance probe: a generated source with tens of thousands of statements makes the Janglim parse-tree
  path throw a raw `InvalidOperationException` ("Sequence contains no elements" — an internal
  `First()`/`Last()` on an empty sequence at scale) at `QoraParser.cs`'s `result.ToParseTree`. Report to
  the engine with a minimized repro; no Qora-side workaround.

---

## 🟡 MEDIUM — gateway features (bigger work)

- **Module system + namespaces** ⚛️ — ✅ SHIPPED in v0.12 (2026-07-03): `namespace` / `open` /
  qualified call targets resolve (Resolver.cs, QSEM018/019/022), `import` loads real multi-file programs
  (ModuleLoader.cs, QSEM020/021, CLI `--base-dir`, extension passes the document dir), and emission
  flattens namespace names (`MyLib.Bell` → `MyLib_Bell`) while auto-renaming only real collisions. The symbol-table machinery the
  effect-analysis step needs now exists. The QSEM013 follow-up also shipped: built-in gate names are
  relaxed Q#-style ("declaration allowed, ambiguous use is an error") with the built-ins living in the
  implicit `Qora.Intrinsic` namespace; the measurement family, `pi`/`tau`/`euler`, and global
  gate-named ops stay reserved.

- **`function` vs `operation`** ⚛️ — a deterministic-classical `function` alongside the effectful quantum
  `operation` (Q#'s central classical/quantum boundary). Trivial as a keyword; the real value needs a
  light purity check (reject qubit ops inside a `function`). Both still lower to `def`. (workable)
- **Return values + typed returns** — `: Int` / `: bit` / `: Unit` and `return expr;` (ops are void
  today). This is the gateway to using calls as expressions. OpenQASM `def` supports typed `return`, but
  the emitter treats `Main` as the un-returnable top-level and hard-codes expression-calls as
  measurement, so it is real front-end + emitter rework. (workable)
- **Calls used as expressions** — `let x = Foo(a);` binding a returned value (today the only
  expression-position call is hard-wired to mean measurement). Sequenced right after return values.
  (workable)
- **`float` / `angle` / `bool` classical types** — rotation angles are real-valued but everything defaults
  to `int` today (misleading). Identity mapping to OpenQASM-3 native types; pure grammar + table work.
  (clean)
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
- **Range / expression bounds** — `start..stop` with variable/const bounds and const/param-sized
  `Qubit[N]` / `use` (today loop bounds and register sizes must be literal numbers). OpenQASM
  `for int i in [a:b]` already accepts expressions. Grammar relaxation from `Num` to `expr` + light const
  handling → scalable circuits. (workable)
- **Register measurement + `MResetZ`** ⚛️ — measure a whole register (`M(q)` → `c = measure q;`) and a
  measure-and-reset combinator, beyond the current single-`q[i]`-on-decl-RHS form. OpenQASM handles both
  natively; mostly relaxing Qora's restrictive call-RHS recognition. (clean)

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
- **General classical arrays** — `array[int, N]`, `bit[N]`, literals, indexed classical lvalues (today only
  qubit registers are indexable). OpenQASM supports arrays, so it is Qora front-end work; real effort for
  limited early-learning payoff. (workable)
- **Namespaces + `open` / import** — organizational familiarity, but OpenQASM has no module system (only
  textual `include`); a namespace can only be name-mangled/flattened and erases at the target. Cosmetic on
  a single-file toy. (not-expressible)

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
- **#14 — post-injection re-analysis.** The model's analysis stores are add-only (re-analysis throws
  QINTERNAL), so re-verifying an injected tree needs generation-keyed storage. Decide during rung-④
  design.
- **#16 — ancilla-identification conditions coupled to FUTURE features** (2026-07-11 literature
  cross-check: Silq PLDI'20, Q#, Unqomp PLDI'21/Reqomp, Twist POPL'22, Quipper, Bennett'73, Gidney'18 —
  20/20 key claims source-verified). `IsCleanupCandidate`'s two conditions (`IsAncilla` use-birth + never
  measured) are provably COMPLETE for today's feature set (void ops, no aliasing, no closures: measurement
  is the only value-escape channel). Future escape channels add conditions to the CANDIDACY layer
  (`IsCleanupCandidate`); the birth layer (`IsAncilla`) is feature-invariant. Add each in the SAME change:
  - **Return values** (roadmap) → add "not returned / does not escape" (Silq: return consumes; Bennett:
    outputs are copied out of scratch).
  - **Aliasing / borrowing** → add "no live alias"; and `borrow` splits ancillas into clean (end |0⟩,
    value-verifiable) vs **dirty** (end = original unknown state — verifiable only by structural
    compute/uncompute pairing à la within/apply, never by value reasoning). Q# precedent.
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

## Sequencing note

Updated 2026-07-03 (post-v0.12: the module system — resolver, multi-file imports, total name
mangling — landed on top of v0.10/0.11's typed IR pipeline and validation, now QSEM001–023). The next
chunk is the **effect analysis** (qfree/mfree/const + liveness), which reuses the resolver's symbol
table and in turn unlocks the end goal: **automatic uncomputation** (Silq-style, injected as the
synthetic `QConjugate` IR node the pipeline already carries). The return-value gateway
(function-vs-operation → typed returns → calls-as-expressions) and **float/angle types** slot in
alongside; whole-operation auto-`Controlled` and the erasing types (Result/Pauli) come last.
