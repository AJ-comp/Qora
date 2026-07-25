# Qora module system + namespaces — design

Decided 2026-07-02. This is the next major feature (see TODO.md sequencing): it builds the
program-symbol-graph / lexical-scope machinery that the effect-analysis step (qfree/mfree/const →
automatic uncomputation) will reuse.

## Goals

- Multi-file programs (`import`) — adoption basics; a language you cannot split across files caps out
  at toy scale, and no library ecosystem can form without it.
- Namespaces with `open` and qualified names — Q#-flavored, C#-familiar.
- One name-resolution pass that EVERY pipeline stage consults (lowering, validation, inversion,
  emission) — the half-shadowing class of bug (one name meaning different things in different stages)
  becomes structurally impossible.

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
- Exactly one `Main` across the whole import graph (entry rules unchanged).
- Imports may contain cycles. A cyclic back-edge reaches a canonical path already registered in `loaded`
  and is skipped without an error; `open` visibility remains non-transitive.

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
program root and follows every dotted namespace segment before selecting the final callable. It does
not start from the caller's namespace and does not consult `open`; an unknown segment/member is an
error. This applies equally to statement calls and calls nested in expressions.

`open A` contributes only callable declarations owned directly by exact namespace `A`. It does not
expose callables in `A.B`, inherit an `open` written in a containing namespace, or re-export namespaces
that `A` opened itself. To expose `A.B.f` as bare `f`, write `open A.B;` in the caller's exact namespace.

A dotted declaration is a real ownership chain. Declaring `namespace A.B` creates namespace symbols
`A` then `B`, so the synthetic intermediate `A` is itself a valid namespace and `open A;` is legal.
However, opening it still exposes only `A`'s direct callables, not `B`'s members.

Namespace and callable members with the same spelling occupy different roles and may coexist. For
example, a callable `A.B()` and namespace `A.B` can coexist: the final segment of `A.B()` selects the
callable, while the intermediate `B` in `A.B.f()` selects the namespace.

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
`ProgramSymbolGraph` used for source declarations. Their origin and role remain explicit, so qualified
lookup follows ordinary ownership edges while validation and emission still apply built-in policy.

## Lowering to OpenQASM (no module system there)

Namespaces flatten by name mangling at emit:

- `MyLib.Bell` → `def MyLib_Bell(...)`, resolved call sites emit the mangled name.
- If the flattened name collides with a keyword, stdgates gate, or another emitted name, `NameMangler`
  appends `_` until the emitted identifier is unique and records a `// Qora:` note.
- Global-namespace ops keep their plain names unless a real emitted-name collision forces a rename.

## Pipeline changes

```
files ─parse each─▶ ASTs ─lower each─▶ QProgram fragments
   ─ModuleLoader merge─▶ merged QProgram
   ─Resolver (ProgramSymbolGraph, opens, call-target FQNs/Ids, ambiguity)─▶ resolved QProgram
   ─Validate + SymbolTableBuilder─▶ semantic errors or lexical scopes
   ─Monomorphize─▶ concrete QProgram
   ─AdjointMaterializer─▶ inverse ops
   ─NameMangler─▶ collision-free emitted identifiers
   ─ReferentialCheck─▶ final safety check
   ─QasmEmitter─▶ OpenQASM 3
```

- `SymbolTableBuilder.BuildProgramSymbols` creates the `ProgramSymbolGraph`. Its root symbol owns global
  callables and the first namespace segment; every dotted segment owns the next, and the final namespace
  owns its callables. `Symbol.OwnerSymbolId` is the authoritative edge. Child/name indexes are derived
  lookup accelerators, not a second ownership ledger.
- The resolver reads that graph. A bare call walks the caller's namespace ownership chain outward
  (`A.B → A → global`) before inspecting the direct callable members of namespaces opened by the exact
  caller namespace. A qualified call starts at the graph root. Successful user-callable bindings write
  the fully-qualified target and declaring operation Id to both statement `QGate` calls and expression
  `QCallNode`s.
- Resolver recursively rebuilds declaration/assignment/return values, conditions, loop bounds, gate
  arguments, array elements, and nested call arguments, so no expression position keeps an unresolved
  short spelling.
- Lexical `Scope` is separate from program ownership. It contains only parameters, registers,
  measurement bits, variables, constants, loop variables, and their use sites inside one callable.
  A callable body's root scope has `ParentScopeId = null`; only nested body/block scopes link to an
  enclosing lexical scope. `EnclosingSymbolId` joins the lexical tree back to its callable symbol.

New semantic codes:

| Code | Meaning |
|---|---|
| QSEM018 | ambiguous unqualified reference (lists candidates) |
| QSEM019 | unknown namespace / unknown member in a qualified name |
| QSEM020 | import file not found / unreadable |
| QSEM021 | retired/reserved; cyclic back-edges are skipped and do not emit a diagnostic |
| QSEM022 | duplicate operation name within one namespace (QSEM008 becomes per-namespace) |
| QSEM023 | reserved; emitted-name collisions are auto-renamed by `NameMangler` and surfaced as `// Qora:` notes |
| QSEM025 | identifier not declared in scope here: an unknown name, or a classical name used before declaration |

## Tooling contract changes

- CLI: `--json <entryFile>` resolves imports relative to the entry file's directory. For stdin input
  (the extension's live-diagnostics path) a new `--base-dir <dir>` flag supplies the resolution root.
- VS Code extension: passes `--base-dir` of the open document; diagnostics stay per-keystroke on the
  lean contract. The stages panel gains nothing new (the resolved IR simply shows qualified names).
- Playground: single-file for now (imports error with a clear message there).

## Increments

1. **Grammar + IR** — DONE: `namespace`/`open`/`import` statements, dotted qualified names; IR nodes carry the
   namespace; no resolution yet (single file, single namespace still works).
2. **Resolver pass** — DONE (single file, multiple namespaces): `Resolver.cs` reads the program declaration graph,
   runs the resolution algorithm above, and rewrites every op/callee name to its FQN; QSEM018 (ambiguous),
   QSEM019 (unknown namespace/member — including `open` of a nonexistent namespace), QSEM022 (duplicate
   within one namespace; global duplicates stay QSEM008). Pipeline: Lower → Resolve → Validate → Mangle →
   Emit; resolver errors preempt validation. `NameMangler` encodes FQN dots as `_` (`MyLib.Bell` →
   `MyLib_Bell`) and appends more `_` only on real emitted-name collisions; stages
   (`ast`/`ir`/`irInverse`/`symbols`) show original/FQN names, only QASM shows mangled names.
   `import` remains QSEM099-gated until increment 3.
3. **Multi-file** — DONE: `ModuleLoader.cs` expands the import graph into one merged program before
   resolution. `import "gates_lib.qor";`, `import "lib/gates.qor";`, and `import "a b.qor";` all use
   the quoted relative path exactly as written, including the extension. Transitive with diamond-sharing;
   canonicalized, case-insensitive paths are registered before file I/O, so both diamond sharing and cyclic
   back-edges are skipped when `loaded.Add` returns false. QSEM020 covers missing/unreadable files;
   QSEM021 is retired/reserved and is no longer emitted.
   parse errors in an imported file surface with the file name prefixed, span -1. CLI: entry-file
   imports resolve next to the file; stdin takes `--base-dir` (extension passes the document's dir —
   imports resolve live in unsaved buffers). No file context ⇒ clear QSEM020 (playground stays
   single-file). Merged order keeps the entry file's ops first, so the entry-op rule is unchanged;
   namespaces merge across files, opens union per namespace.
4. **Mangled emission + docs + adversarial review** — DONE. README×3 and the adjoint-pipeline doc×3
   now show real mangled output plus a namespaces/import tour section. The adversarial review found and
   fixed three real bugs: (1) dot flattening could collide (`A.F` vs `A_F`), now auto-renamed by
   `NameMangler` with a note; (2) an entry-op local named like an operation could collide with a top-level
   def, now auto-renamed by the same emitted-scope machinery; (3) `open` of a declared-but-empty namespace
   was a false QSEM019. `BuildProgramSymbols` now registers empty namespace blocks and every intermediate
   segment of a dotted namespace, not just namespaces that directly contain callables.
5. **Function calls in namespace resolution** — DONE. The expression grammar accepts qualified call
   targets, `QCallNode` carries the same stable callee reference as `QGate`, and the resolver visits every
   expression-bearing IR position. Same-namespace, qualified, and `open`-visible functions now resolve
   through `ProgramSymbolGraph`; ambiguity remains QSEM018, and monomorphization re-points both the name
   and Id when it selects a width-specialized `bit[]` function. Lexical `Scope` remains independent and
   serves callable-body declarations only.
