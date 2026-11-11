```qora
operation Bell(q: Qubit[]) {
    H(q[0]);
    CNOT(q[0], q[1]);
}

operation Main(): int {
    use q = Qubit[2];
    Bell(q);

    var r0: bit = M(q[0]);
    var r1: bit = M(q[1]);
    var result: bit[] = [r0, r1];
    return AsInt(result);
}
```
