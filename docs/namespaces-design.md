# Qora module system + namespaces — design

Decided 2026-07-02. This is the next major feature (see TODO.md sequencing): it builds the
symbol-table / scoped-resolution machinery that the effect-analysis step (qfree/mfree/const →
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
import gates_lib;                  // bare module name -> gates_lib.qor, relative to this file
                                    // (dots map to directories later: import lib.gates -> lib/gates.qor;
                                    //  string paths hit a Janglim lexer limitation: -wrapped regexes
                                    //  cannot match quote-delimited tokens)

namespace MyLib {                   // a file may declare namespaces; ops outside any namespace
    open Qora.Intrinsic;            // (implicitly available) — see "builtins" below
    open OtherLib;                  // bring OtherLib's ops into unqualified scope

    operation Bell(Qubit[2] q) { ... }
}

operation Main() {                  // files without namespaces keep working (global namespace)
    use q = Qubit[2];
    MyLib.Bell(q);                  // qualified call
}
```

- Backward compatible: a file with no `namespace` block lives in the global namespace; every existing
  `.qor` program compiles unchanged.
- Exactly one `Main` across the whole import graph (entry rules unchanged).
- Imports are acyclic (cycle ⇒ semantic error) and non-transitive for `open` visibility.

## Name resolution (the standard algorithm)

For an unqualified name at a use site:

```
1. op-local scope        — parameters, variables, loop vars (innermost first)
2. the enclosing namespace's own declarations       ← local namespace wins
3. the namespaces `open`ed in this file/namespace:
     - found in exactly one  → use it
     - found in two or more  → AMBIGUITY ERROR (list the candidates, tell the user to qualify)
     - found in none         → unknown-name error (QSEM007)
```

A qualified name (`MyLib.Bell`) bypasses steps 1–3 and resolves directly (unknown namespace/member ⇒
error). `open` is NOT transitive: what A opens is invisible to files that open A.

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
  operations (the global namespace shares one scope with the built-ins and has no qualifier), and
  declaring `Qora.Intrinsic` itself.
- `open Qora.Intrinsic;` is legal and a no-op (it is already open); `Qora.Intrinsic.H(q)` resolves to
  the bare built-in and emits `h q;`.

## Lowering to OpenQASM (no module system there)

Namespaces flatten by name mangling at emit, C++-style:

- `MyLib.Bell` → `def MyLib__Bell(...)`, resolved call sites emit the mangled name.
- The existing uniquify machinery (built for `__adj` collisions) guards mangled-name collisions.
- Global-namespace ops keep their plain names (zero diff for existing programs).

## Pipeline changes

```
files ─parse each─▶ ASTs ─lower each─▶ QProgram fragments
   ─merge + RESOLVE (new pass: symbol table, opens, qualification, ambiguity)─▶ resolved QProgram
   ─validate (existing QSEM rules now consult resolution, + new codes below)─▶ invert ─▶ emit (mangled)
```

- The resolver produces: a symbol table (namespace → op signatures) and, per call site, the resolved
  fully-qualified target. Later passes stop matching raw name strings.
- The same symbol table is the foundation the effect-analysis pass (qfree/mfree/const) will extend.

New semantic codes:

| Code | Meaning |
|---|---|
| QSEM018 | ambiguous unqualified reference (lists candidates) |
| QSEM019 | unknown namespace / unknown member in a qualified name |
| QSEM020 | import file not found / unreadable |
| QSEM021 | cyclic imports |
| QSEM022 | duplicate operation name within one namespace (QSEM008 becomes per-namespace) |
| QSEM023 | names that would collide in the EMITTED program: two op names meeting at one mangled def name (`A.F` vs `A__F` → `A__F_`); an entry-op declaration landing on a def name (entry locals are QASM top-level globals); a def-local shadowing an operation that def calls |

## Tooling contract changes

- CLI: `--json <entryFile>` resolves imports relative to the entry file's directory. For stdin input
  (the extension's live-diagnostics path) a new `--base-dir <dir>` flag supplies the resolution root.
- VS Code extension: passes `--base-dir` of the open document; diagnostics stay per-keystroke on the
  lean contract. The stages panel gains nothing new (the resolved IR simply shows qualified names).
- Playground: single-file for now (imports error with a clear message there).

## Increments

1. **Grammar + IR** — DONE: `namespace`/`open`/`import` statements, dotted qualified names; IR nodes carry the
   namespace; no resolution yet (single file, single namespace still works).
2. **Resolver pass** — DONE (single file, multiple namespaces): `Resolver.cs` builds the symbol table,
   runs the resolution algorithm above, and rewrites every op/callee name to its FQN; QSEM018 (ambiguous),
   QSEM019 (unknown namespace/member — including `open` of a nonexistent namespace), QSEM022 (duplicate
   within one namespace; global duplicates stay QSEM008). Pipeline: Lower → Resolve → Validate → Mangle →
   Emit; resolver errors preempt validation. `NameMangler` encodes FQN dots as `__` (`MyLib.Bell` →
   `MyLib__Bell_`); stages (`ast`/`ir`/`irInverse`) show original/FQN names, only QASM shows mangled ones.
   `import` remains QSEM099-gated until increment 3.
3. **Multi-file** — DONE: `ModuleLoader.cs` expands the import graph into one merged program before
   resolution. `import gates_lib;` → `gates_lib.qor` next to the importing file, `import lib.gates;` →
   `lib/gates.qor`, `import "a b.qor";` → literal path. Transitive with diamond-sharing (canonicalized,
   case-insensitive paths load once); QSEM020 missing/unreadable file, QSEM021 cycle (chain shown);
   parse errors in an imported file surface with the file name prefixed, span -1. CLI: entry-file
   imports resolve next to the file; stdin takes `--base-dir` (extension passes the document's dir —
   imports resolve live in unsaved buffers). No file context ⇒ clear QSEM020 (playground stays
   single-file). Merged order keeps the entry file's ops first, so the entry-op rule is unchanged;
   namespaces merge across files, opens union per namespace.
4. **Mangled emission + docs + adversarial review** — DONE. README×3 and the adjoint-pipeline doc×3
   now show real mangled output plus a namespaces/import tour section. The adversarial review found and
   fixed three real bugs: (1) dot→`__` broke mangling injectivity — `A.F` vs `A__F` silently emitted
   two `def A__F_`s, now QSEM023; (2) an entry-op local named like an operation emitted a top-level
   global colliding with the def (`qubit[2] Bell_;` vs `def Bell_`), now QSEM023 (def-locals only
   error when they shadow an op that def CALLS — plain shadowing stays legal, matching the QSEM013
   relaxation philosophy); (3) `open` of a declared-but-empty namespace was a false QSEM019 — the
   known-namespace set now includes every declared block, not just ones containing ops.
