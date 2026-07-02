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

## Built-in gates (v1 decision)

Built-in gate/measurement names (`QoraGates`) stay **reserved** — QSEM013's "an operation may not
shadow a built-in" rule is kept even inside namespaces. Consequences:

- The "local namespace wins" rule can never silently override `H`/`Rx`/`M` — what the book teaches is
  what every program means. Ambiguity handling therefore only ever applies to user-vs-user collisions.
- Revisit after the resolution machinery has settled: the Q#-style relaxation ("declaration allowed,
  ambiguous use is an error") is compatible with this design and can be turned on later without
  breaking programs.

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

## Tooling contract changes

- CLI: `--json <entryFile>` resolves imports relative to the entry file's directory. For stdin input
  (the extension's live-diagnostics path) a new `--base-dir <dir>` flag supplies the resolution root.
- VS Code extension: passes `--base-dir` of the open document; diagnostics stay per-keystroke on the
  lean contract. The stages panel gains nothing new (the resolved IR simply shows qualified names).
- Playground: single-file for now (imports error with a clear message there).

## Increments

1. **Grammar + IR**: `namespace`/`open`/`import` statements, dotted qualified names; IR nodes carry the
   namespace; no resolution yet (single file, single namespace still works).
2. **Resolver pass** (single file, multiple namespaces): symbol table, the resolution algorithm above,
   QSEM018/019/022.
3. **Multi-file**: import loading, cycle detection (QSEM020/021), CLI/extension `--base-dir`.
4. **Mangled emission** + stages/docs updates; adversarial review of the whole feature.
