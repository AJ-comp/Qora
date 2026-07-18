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
import "gates_lib.qor";            // quoted relative path, resolved from this file's directory

namespace MyLib                     // a file may declare namespaces; ops outside any namespace
{
    open Qora.Intrinsic;            // (implicitly available) — see "builtins" below
    open OtherLib;                  // bring OtherLib's ops into unqualified scope

    operation Bell(Qubit[] q)
    {
        ...
    }
}

operation Main()                    // files without namespaces keep working (global namespace)
{
    use q = Qubit[2];
    MyLib.Bell(q);                  // qualified call
}
```

- Backward compatible: a file with no `namespace` block lives in the global namespace; every existing
  `.qor` program compiles unchanged.
- Exactly one `Main` across the whole import graph (entry rules unchanged).
- Imports may contain cycles. A cyclic back-edge reaches a canonical path already registered in `loaded`
  and is skipped without an error; `open` visibility remains non-transitive.

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

Namespaces flatten by name mangling at emit:

- `MyLib.Bell` → `def MyLib_Bell(...)`, resolved call sites emit the mangled name.
- If the flattened name collides with a keyword, stdgates gate, or another emitted name, `NameMangler`
  appends `_` until the emitted identifier is unique and records a `// Qora:` note.
- Global-namespace ops keep their plain names unless a real emitted-name collision forces a rename.

## Pipeline changes

```
files ─parse each─▶ ASTs ─lower each─▶ QProgram fragments
   ─ModuleLoader merge─▶ merged QProgram
   ─Resolver (namespace op table, opens, call-target FQNs, ambiguity)─▶ resolved QProgram
   ─Validate + SymbolTableBuilder─▶ semantic errors or scoped symbols
   ─Monomorphize─▶ concrete QProgram
   ─AdjointMaterializer─▶ inverse ops
   ─NameMangler─▶ collision-free emitted identifiers
   ─ReferentialCheck─▶ final safety check
   ─QasmEmitter─▶ OpenQASM 3
```

- The resolver produces a namespace operation table and, per operation call site, the resolved
  fully-qualified target. It does not resolve local variables, loop variables, indices, or condition names.
- `SymbolTableBuilder` runs during validation and owns lexical scope: parameters, registers, measurement
  bits, variables, constants, loop variables, and their use sites.

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
2. **Resolver pass** — DONE (single file, multiple namespaces): `Resolver.cs` builds the namespace operation table,
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
   def, now auto-renamed by the same emitted-scope machinery; (3) `open` of a declared-but-empty namespace was a false QSEM019 — the
   known-namespace set now includes every declared block, not just ones containing ops.
