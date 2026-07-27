# Qora module system + namespaces — design

Decided 2026-07-02. This feature establishes the unified HIR name-resolution graph that later
semantic passes reuse. Program declarations and lexical bindings share one containment tree, while
imports and future inheritance lookup remain explicit typed side edges.

## Goals

- Multi-file programs (`import`) — adoption basics; a language you cannot split across files caps out
  at toy scale, and no library ecosystem can form without it.
- Namespaces with `open` and qualified names — Q#-flavored, C#-familiar.
- One authoritative `HirScopeGraph` for program declarations and lexical bindings. Every declaration
  records its declaring scope, and every source construct that introduces a scope has a stable
  node-and-role site, preventing different stages from silently assigning different meanings to the
  same name.

## Surface syntax

```qora
import "gates_lib.qor";            // quoted relative path, resolved from this file's directory

namespace MyLib                     // a file may declare namespaces; ops outside any namespace
{
    open Qora.Intrinsic;            // (implicitly available) — see "builtins" below
    open OtherLib;                  // bring OtherLib's callables into unqualified scope

    function two(): int
    {
        return 2;
    }

    operation Bell(q: Qubit[])
    {
        ...
    }
}

operation Main()                    // files without namespaces keep working (global namespace)
{
    use q = Qubit[2];
    MyLib.Bell(q);                  // qualified call
    var n: int = MyLib.two();       // qualified expression call
}
```

- Backward compatible: a file with no `namespace` block lives in the global namespace; every existing
  `.qor` program compiles unchanged.
- At most one global `Main` may exist across the whole import graph. If it is absent, the first
  callable in deterministic merged order remains the entry, matching the existing entry rule.
- Imports may contain cycles. `SourceGraphLoader` resolves a cyclic back-edge to the already registered
  revision-qualified `SourceDocumentRef`, and `ModuleLoader` visits that document only once; `open`
  visibility remains non-transitive.

## Name resolution (the standard algorithm)

For an unqualified callable name used inside namespace `A.B`:

```
1. namespace A.B's direct callable declarations     ← exact namespace wins
2. namespace A's direct callable declarations       ← then each containing namespace
3. the global namespace
4. namespaces `open`ed by exact namespace A.B:
     - found in exactly one  → use it
     - found in two or more  → AMBIGUITY ERROR (list the candidates, tell the user to qualify)
     - found in none         → unknown-name error (QSEM007)
```

A qualified name (`A.B.f`, `MyLib.Bell`, `MyLib.two()`) is absolute: resolution starts at the
program scope and follows every dotted namespace scope before selecting the final callable. It does
not start from the caller's namespace and does not consult `open`; an unknown segment/member is an
error. This applies equally to statement calls and calls nested in expressions.

`open A` contributes only callable declarations owned directly by exact namespace `A`. It does not
expose callables in `A.B`, inherit an `open` written in a containing namespace, or re-export namespaces
that `A` opened itself. To expose `A.B.f` as bare `f`, write `open A.B;` in the caller's exact namespace.

A dotted declaration is a real containment chain. Declaring `namespace A.B` creates a namespace
scope for `A` and then one for `B`; each namespace symbol is declared in its parent scope. The
synthetic intermediate `A` is therefore a valid namespace and `open A;` is legal. However, opening
it still exposes only `A`'s direct callables, not `B`'s members.

Bindings are role-aware and may retain several symbols under the same spelling. For example, a
callable `A.B()` and namespace `A.B` can coexist: the final segment of `A.B()` selects the callable
role, while the intermediate `B` in `A.B.f()` selects the namespace role.

Callable lookup deliberately does not treat a local value with the same spelling as a function
declaration. A value reference and a call target occupy different semantic roles: `var value = 1;
var result = value();` still calls a declared function named `value`, while the bare expression
`value + 1` reads the local variable.

Ambiguity error message shape (teaching-first):
`` `H` is ambiguous here: it could be `MyLib.H` or `YourLib.H` — qualify the call (e.g. `MyLib.H(q)`) ``

## Built-in gates (Q#-style relaxation — SHIPPED, replaces the v1 reservation)

The built-ins live in the implicit **`Qora.Intrinsic`** namespace (Q#'s
`Microsoft.Quantum.Intrinsic` analogue), open everywhere. The rule is "declaration allowed,
ambiguous use is an error" — silent reinterpretation stays impossible:

- A **namespaced** operation MAY take a gate name (`namespace L { operation Rx … }`); call it
  qualified (`L.Rx(q)`).
- An **unqualified** use that could mean both a user op and the built-in (own namespace or an `open`
  provides the name) is **QSEM018** — the message lists the user candidate(s) and the built-in, and
  shows both qualifications (`L.Rx(...)` / `Qora.Intrinsic.Rx(...)`). The "local namespace wins" rule
  applies to user-vs-user names only; it never silently overrides a gate.
- With **no user candidate in scope**, the bare gate name is the built-in — what the book teaches is
  what the program means.
- Still fully reserved (QSEM013): the measurement family (`M`/`Measure`/`measure`) and
  `pi`/`tau`/`euler` (expression-position tokens the resolver never sees), gate names for **global**
  operations (a global user declaration has no qualification that can distinguish it from the built-in), and
  declaring `Qora.Intrinsic` itself.
- `open Qora.Intrinsic;` is legal and a no-op (it is already open); `Qora.Intrinsic.H(q)` resolves to
  the bare built-in and emits `h q;`.

`Qora`, `Intrinsic`, and the built-in gate/function declarations are synthetic symbols in the same
`HirScopeGraph` used for source declarations. Their origin and role remain explicit, so qualified
lookup follows ordinary containment scopes while validation and emission still apply built-in policy.

## Lowering to OpenQASM (no module system there)

Namespaces flatten while MIR is lowered into the typed OpenQASM target model:

- `MyLib.Bell` → `def MyLib_Bell(...)`, resolved call sites emit the mangled name.
- If the flattened name collides with a keyword, stdgates gate, or another emitted name,
  `MirOpenQasmLowering` appends `_` until the target identifier is unique.
- Global-namespace ops keep their plain names unless a real emitted-name collision forces a rename.

## Pipeline changes

```
QoraParser.Parse
   ─▶ SyntaxSnapshot (Qora-owned immutable tokens / tree projections / text / diagnostics)
QoraCompiler.Compile
   ─▶ CompilationSession (branch-safe revision allocator)
       └─ immutable Compilation (Revision + ParentRevision + OutputPlan)
       ├─ Sources
       │   ├─ SourceDocumentSnapshot[] + per-document SyntaxSnapshot
       │   └─ ImportGraph (revision-qualified SourceDocumentRef edges)
       ├─ Hir
       │   ├─ Snapshots + structural stage Milestones
       │   ├─ Lineage
       │   │   ├─ identity-preserving NodeDerivation
       │   │   ├─ provenance-only NodeSynthesis
       │   │   └─ imported-source HirNodeIntroduction
       │   └─ SemanticArtifacts[(HirSnapshotId, HirSemanticPhase)]
       │       ├─ Validation
       │       └─ EffectAnalysis
       ├─ Mir (optional typed SSA/CFG snapshot + revision-bound analyses)
       ├─ Links
       │   ├─ Hir (the exact Hir.Lineage instance)
       │   └─ Mir (typed HIR ↔ MIR provenance)
       ├─ Diagnostics
       │   └─ typed Source / HIR / MIR / Target(backend, HIR-or-MIR input) origin
       └─ Targets.Artifacts[TargetBackend]
           └─ OpenQasmArtifact
               ├─ exact materialized MirSnapshot
               ├─ MirOpenQasmTargetProgram
               └─ OpenQASM 3 text derived from that target model
```

- `QoraParser` is deliberately syntax-only. A published `SyntaxSnapshot` retains no Janglim object:
  it owns only immutable Qora projections and captured text. The internal transient
  `SyntaxParseProduct` carries the mutable `LoweringAst` just long enough for immediate per-document
  HIR lowering. `SourceGraphLoader` is the sole owner of import I/O: it
  reads each canonical document once, preserves one `SourceDocumentSnapshot` and `SyntaxSnapshot` per
  `SourceDocumentRef`, lowers every successfully parsed document with a document-qualified
  `SourceSpan`, and records resolved and unresolved edges in an immutable `ImportGraph`.
  `ModuleLoader.Expand(LoadedSourceGraph)` performs no I/O or parsing; it purely merges the prepared
  `SourceDocumentRef -> QProgram` map by those graph edges and returns an exact
  `HirNodeIntroduction` for every HIR node imported from another document.
- `CompilationSession` is the only revision-allocation authority for one logical compilation.
  Recompiling the same parent twice produces distinct sibling revisions, while each child records that
  parent in `Compilation.ParentRevision`. `CompilationOutputPlan` independently fixes the exact
  requested HIR goal, MIR presence, and backend set. A successful result must match that plan exactly;
  it cannot contain an unsolicited MIR or target artifact.
- A `HirSnapshot` always owns one published `QProgram` generation and an immutable
  `nodeId -> SourceSpan` source map. Semantic facts are separate `HirSemanticArtifact` values qualified
  by that exact snapshot and `HirSemanticPhase`. Each validation artifact owns the authoritative
  `HirValidationOutcome`: accepted means an empty diagnostic set, while rejected retains the exact
  immutable diagnostics. Compilation diagnostics project that outcome with stage/origin metadata
  rather than duplicating validation state. Effect analysis requires the exact accepted validation
  artifact for the same snapshot and records it as `ValidationBasis`. Structural milestones may alias
  one snapshot when a pass performs no rewrite. Validation and `EffectAnalysis` therefore do not
  manufacture another structural HIR stage; callers query `Compilation.Hir.EffectAnalysis` for the
  exact semantic artifact.
  Milestones follow the canonical order `Lowered -> ImportsExpanded -> MeasurementLowered -> Resolved
  -> Specialized`. Adjoint and conjugation are absent from Qora source and HIR.
- HIR lineage is owned by `HirCompilation`; `Compilation.Links.Hir` is the same instance, not another
  ledger. `NodeDerivation` means same semantic identity, `NodeSynthesis` records only provenance for a
  new entity, and `HirNodeIntroduction` records a source node entering through import expansion.
  Every newly appearing node must have exactly one classification. An exact `HirSemanticContext`
  translates only identity-preserving paths from a current HIR snapshot back to the analyzed semantic
  basis before querying the scope graph. Target renaming therefore never mutates source semantics, and
  a synthesized temporary can never be mistaken for its owner's symbol.
- `SymbolTableBuilder.BuildHirScopeGraph` creates one `HirScopeGraph`. Its current containment chain is
  `Program → Namespace → Callable → Block`. When nominal types arrive, `Type` becomes another
  containment scope below a namespace and can contain callable scopes without introducing another
  ownership structure.
- `Scope.ParentScopeId` is the authoritative containment edge. `Symbol.DeclaringScopeId` independently
  states where each symbol is declared, while `Scope.DeclaringSymbolId` joins a namespace, type, or
  callable declaration to the environment it introduces. Child, namespace-path, callable-root, and
  declaration-node maps are derived indexes owned by the graph.
- A scope binding is `spelling → [SymbolId…]`, not a single untyped slot. Lookup selects the required
  role, which preserves namespace/callable same-name coexistence while ordinary value lookup excludes
  namespace and callable declarations.
- `open` does not alter containment. It adds a typed `Import` lookup edge from the exact namespace
  scope to the opened namespace. Future base-class and interface search use their own `BaseType` and
  `Interface` edges rather than overloading `ParentScopeId`.
- The resolver reads the graph. A bare call walks the caller's namespace containment chain outward
  (`A.B → A → program`) before inspecting the direct callable members reached by that exact namespace's
  `Import` edges. A qualified call starts at the program scope. Successful user-callable bindings write
  the fully-qualified target and declaring operation Id to both statement `QGate` calls and expression
  `QCallNode`s.
- Resolver recursively rebuilds declaration/assignment/return values, conditions, loop bounds, gate
  arguments, array elements, and nested call arguments, so no expression position keeps an unresolved
  short spelling.
- Every HIR construct that introduces an environment is indexed by
  `HirScopeSite(ownerNodeId, role)`. The role distinguishes multiple scopes introduced by one node,
  such as `IfCondition`, `IfThen`, and `IfElse`, or `ForBinder` and `ForBody`.
- An exact HIR `HirSemanticModel` retains this one `ScopeGraph`; it does not mirror separate program-symbol and lexical
  scope tables. Declaration, callable-root, namespace-path, and source-site queries delegate to indexes
  owned by the graph.
- `MirOpenQasmLowering` assigns emitted names and builds an immutable
  `MirOpenQasmTargetProgram`; it never writes a target spelling into `HirSemanticModel` or MIR.
  `TargetArtifactSet.Artifacts` is the authoritative backend-keyed map, while the current convenience
  view is `Targets.OpenQasm`. `OpenQasmArtifact.Source` identifies the exact materialized
  `MirSnapshot` consumed by this backend. `QasmBackend` accepts only that exact MIR snapshot, and
  `OpenQasmArtifact.Text` is emitted from the typed target program rather than supplied as a second
  authority. Target diagnostics record `TargetDiagnosticInput.Mir`, so provenance follows the
  backend's real input domain without reaching back into HIR.
- MIR links are exact-reference maps. Every HIR semantic symbol has one
  `MirSymbolLoweringDisposition`, including explicit namespace, builtin, and unreachable non-lowering
  reasons. Every MIR value, storage, and qubit has one `MirEntityOriginKind`, distinguishing a
  source-backed entity from a compiler temporary even when no direct symbol link exists.

New semantic codes:

| Code | Meaning |
|---|---|
| QSEM018 | ambiguous unqualified reference (lists candidates) |
| QSEM019 | unknown namespace / unknown member in a qualified name |
| QSEM020 | import file not found / unreadable |
| QSEM021 | retired/reserved; cyclic back-edges are skipped and do not emit a diagnostic |
| QSEM022 | duplicate operation name within one namespace (QSEM008 becomes per-namespace) |
| QSEM023 | reserved; emitted-name collisions are auto-renamed by `MirOpenQasmLowering` |
| QSEM025 | identifier not declared in scope here: an unknown name, or a classical name used before declaration |

## Tooling contract changes

- CLI: `--json <entryFile>` resolves imports relative to the entry file's directory. For stdin input
  `--base-dir <dir>` supplies the resolution root and `--source-path <path>` identifies the live entry
  document. JSON diagnostics include the exact source path and revision-qualified document identity.
- VS Code extension: passes both `--base-dir` and `--source-path` for the open document; diagnostics stay per-keystroke on the
  lean contract. The stages panel reads the snapshots already present in the same `Compilation`; it
  does not rerun resolver, validation, MIR lowering, or a backend to reconstruct a view.
- Playground: single-file for now (imports error with a clear message there).

## Increments

1. **Grammar + IR** — DONE: `namespace`/`open`/`import` statements, dotted qualified names; IR nodes carry the
   namespace; no resolution yet (single file, single namespace still works).
2. **Resolver pass** — DONE (single file, multiple namespaces): `Resolver.cs` reads the unified HIR scope graph,
   runs the resolution algorithm above, and rewrites every op/callee name to its FQN; QSEM018 (ambiguous),
   QSEM019 (unknown namespace/member — including `open` of a nonexistent namespace), QSEM022 (duplicate
   within one namespace; global duplicates stay QSEM008). In the current pipeline, document lowering and
   module merge precede measurement lowering and resolution; resolver errors still preempt validation,
   specialization, analysis, MIR, and target work. `MirOpenQasmLowering` later encodes FQN dots as `_` (`MyLib.Bell` →
   `MyLib_Bell`) and appends more `_` only on real emitted-name collisions; stages
   (`ast`/`ir`/`symbols`/`uncompute`/`mir`/`mirEffects`) show source/FQN identities, while only the
   OpenQASM target artifact contains mangled names.
3. **Multi-file** — DONE: `SourceGraphLoader` prepares the complete immutable source graph before
   `ModuleLoader.cs` merges it for resolution. `import "gates_lib.qor";`,
   `import "lib/gates.qor";`, and `import "a b.qor";` all use the quoted relative path exactly as
   written, including the extension. Canonical paths map to one revision-qualified document identity,
   so diamond sharing and cyclic back-edges reuse the same `SourceDocumentSnapshot` without repeated
   I/O or parsing. QSEM020 covers missing/unreadable files or missing path context; QSEM021 is
   retired/reserved and is no longer emitted. Imported parse diagnostics retain their exact
   document-qualified `SourceSpan`, rather than losing the location or borrowing the entry document's
   offsets. CLI entry-file imports resolve next to the file; stdin takes `--base-dir` and optionally
   `--source-path` (the extension supplies both for the live document). No file context yields a clear
   QSEM020. `ModuleLoader.Expand(LoadedSourceGraph)` then performs a pure graph merge: entry operations
   remain first, imported subtrees follow deterministic depth-first post-order, namespaces merge across
   files, and opens union per namespace.
4. **Mangled emission + docs + adversarial review** — DONE. README×3
   now show real mangled output plus a namespaces/import tour section. The adversarial review found and
   fixed three real bugs: (1) dot flattening could collide (`A.F` vs `A_F`), now auto-renamed by
   target lowering; (2) an entry-op local named like an operation could collide with a top-level
   def, now auto-renamed by the same emitted-scope machinery; (3) `open` of a declared-but-empty namespace
   was a false QSEM019. `BuildHirScopeGraph` registers empty namespace scopes and every intermediate
   segment of a dotted namespace, not just namespaces that directly contain callables.
5. **Function calls in namespace resolution** — DONE. The expression grammar accepts qualified call
   targets, `QCallNode` carries the same stable callee reference as `QGate`, and the resolver visits every
   expression-bearing IR position. Same-namespace, qualified, and `open`-visible functions now resolve
   through `HirScopeGraph`; ambiguity remains QSEM018, and monomorphization re-points both the name
   and Id when it selects a width-specialized `bit[]` function. Callable bodies and nested lexical
   environments remain in the same graph and are addressable through stable `HirScopeSite` keys.
