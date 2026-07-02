```qora
operation Bell(Qubit[2] q) {
    H(q[0]);
    CNOT(q[0], q[1]);
}

operation Main() {
    use q = Qubit[2];
    Bell(q);

    bit r0 = M(q[0]);
    bit r1 = M(q[1]);
}
```
