# Ket

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

- `src/Ket.Core` — grammar + AST + OpenQASM emitter
- `src/Ket` — console runner
- `src/Ket.Playground` — Blazor WebAssembly playground (Monaco editor, live parse → AST → OpenQASM)
- `docs/` — learning notes (GitHub Pages)

## Docs

- **[H 게이트를 화살표로 이해하기](docs/index.html)** — 큐비트·진폭·간섭을 화살표로 한 단계씩 (누가 봐도 따라올 수 있게)

`docs/index.html` is a self-contained page. To publish on GitHub Pages:
**Settings → Pages → Deploy from a branch → `main` / `/docs`.**
