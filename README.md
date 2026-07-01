# Qora

A small **quantum toy language** for learning — it parses (via the
[Janglim](https://www.nuget.org/packages/Janglim) parser-generator) and emits **OpenQASM 3.0**.

```
operation Bell(Qubit[2] q) {
    H(q[0]);
    CNOT(q[0], q[1]);
}
operation Main() {
    use q = Qubit[2];
    Bell(q);
    bit r = M(q[0]);
}
```

## Layout

- `src/Qora.Core` — grammar + AST + OpenQASM emitter
- `src/Qora` — console runner (also the `--json` CLI the VS Code extension calls)
- `src/Qora.Playground` — Blazor WebAssembly playground (Monaco editor, live parse → AST → OpenQASM)
- `vscode/` — the **VS Code extension** (highlighting, hover, live parse-error squiggles, OpenQASM transpile)
- `docs/` — learning notes (GitHub Pages)

## VS Code extension

`vscode/` is the Qora editor extension. It bundles a per-platform, self-contained build of the Qora
CLI, so live errors and transpile work with no .NET install. See [vscode/README.md](vscode/README.md).
Release is automated: the **Publish Qora VS Code extension** GitHub Action builds one self-contained
binary per platform and publishes a platform-specific `.vsix` to the Marketplace (needs a `VSCE_PAT`
repository secret).

## Docs

- **[H 게이트를 화살표로 이해하기](docs/index.html)** — 큐비트·진폭·간섭을 화살표로 한 단계씩 (누가 봐도 따라올 수 있게)

`docs/index.html` is a self-contained page. To publish on GitHub Pages:
**Settings → Pages → Deploy from a branch → `main` / `/docs`.**
