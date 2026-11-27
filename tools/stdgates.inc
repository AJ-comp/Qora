// OpenQASM 3 standard gates for the Braket runner. Braket's U already gives
// X, Y, H, Rx and Ry below their standard phases; no gphase correction is needed.
gate p(lambda) a { ctrl @ gphase(lambda) a; }
gate x a { U(pi, 0, pi) a; }
gate y a { U(pi, pi/2, pi/2) a; }
gate z a { p(pi) a; }
gate h a { U(pi/2, 0, pi) a; }
// Fractional powers need a floating-point value: integer 1/2 evaluates to zero.
gate s a { pow(0.5) @ z a; }
gate sdg a { inv @ pow(0.5) @ z a; }
gate t a { pow(0.5) @ s a; }
gate tdg a { inv @ pow(0.5) @ s a; }
gate sx a { pow(0.5) @ x a; }
gate rx(theta) a { U(theta, -pi/2, pi/2) a; }
gate ry(theta) a { U(theta, 0, 0) a; }
// H Rx(lambda) H = Rz(lambda). Braket does not apply an enclosing ctrl
// to a bare gphase in a gate body, so retain both eigenphases on the target.
gate rz(lambda) a { h a; rx(lambda) a; h a; }
gate cx a, b { ctrl @ x a, b; }
gate cy a, b { ctrl @ y a, b; }
gate cz a, b { ctrl @ z a, b; }
gate cp(lambda) a, b { ctrl @ p(lambda) a, b; }
gate crx(theta) a, b { ctrl @ rx(theta) a, b; }
gate cry(theta) a, b { ctrl @ ry(theta) a, b; }
gate crz(theta) a, b { ctrl @ rz(theta) a, b; }
gate ch a, b { ctrl @ h a, b; }
gate swap a, b { cx a, b; cx b, a; cx a, b; }
gate ccx a, b, c { ctrl @ ctrl @ x a, b, c; }
gate cswap a, b, c { ctrl @ swap a, b, c; }
gate cu(theta, phi, lambda, gamma) a, b { p(gamma) a; ctrl @ U(theta, phi, lambda) a, b; }
gate CX a, b { ctrl @ U(pi, 0, pi) a, b; }
gate phase(lambda) q { U(0, 0, lambda) q; }
gate cphase(lambda) a, b { ctrl @ phase(lambda) a, b; }
gate id a { U(0, 0, 0) a; }
gate u1(lambda) q { U(0, 0, lambda) q; }
gate u2(phi, lambda) q { gphase(-(phi+lambda)/2); U(pi/2, phi, lambda) q; }
gate u3(theta, phi, lambda) q { gphase(-(phi+lambda)/2); U(theta, phi, lambda) q; }
