```qora
H(q[0]);
CNOT(q[0], q[1]);
Rz(pi/4, q[1]);
```

```qasm
h q[0];
cx q[0], q[1];
rz(pi / 4) q[1];
```
