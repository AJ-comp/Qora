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

---

## 🟡 MEDIUM — gateway features (bigger work)

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

## Sequencing note

The natural next chunk is the **return-value gateway** (function-vs-operation → typed returns →
calls-as-expressions), which unlocks real program structure, then **float/angle types** and
**range/expression bounds** for honest, scalable circuits, then **`within`/`apply`** (now unblocked by
single-gate `Adjoint`). Whole-operation auto-functors and the erasing types (Result/Pauli/namespaces)
come last. Comments wait on the engine's longest-match lexer.
