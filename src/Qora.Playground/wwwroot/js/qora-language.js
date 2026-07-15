// Registers a "qora" language with Monaco: syntax highlighting + hover docs.
//   keywords (operation/use/const/var/if/for/…) -> blue
//   types    (Qubit/int/float/bit/angle)          -> teal
//   gates    (H/X/CNOT/Rx/…/M)                    -> purple
//   pi -> constant (green);  numbers / identifiers / operators
//   hover a gate/keyword -> a short Korean description tooltip
// Polls until Monaco has loaded (editor.main.js may finish after this script runs).
(function register() {
    if (typeof monaco === 'undefined' || !monaco.languages || !monaco.editor) {
        setTimeout(register, 50);
        return;
    }

    monaco.languages.register({ id: 'qora' });

    monaco.languages.setMonarchTokensProvider('qora', {
        keywords: ['operation', 'use', 'new', 'const', 'var', 'if', 'for', 'in', 'while', 'repeat', 'until'],
        types: ['Qubit', 'int', 'float', 'bit', 'angle'],
        gates: ['H', 'X', 'Y', 'Z', 'S', 'T', 'CNOT', 'CX', 'CZ', 'SWAP', 'CCX', 'Rx', 'Ry', 'Rz', 'M'],
        constants: ['pi'],
        tokenizer: {
            root: [
                [/[A-Za-z_][A-Za-z0-9_]*/, {
                    cases: {
                        '@keywords': 'keyword',
                        '@types': 'type',
                        '@gates': 'gate',
                        '@constants': 'number',
                        '@default': 'identifier'
                    }
                }],
                [/\d+\.\d+/, 'number'],
                [/\d+/, 'number'],
                [/==|\.\.|[-+*\/]/, 'operator'],
                [/[=(){}\[\],;]/, 'delimiter'],
                [/\s+/, 'white'],
            ]
        }
    });

    monaco.editor.defineTheme('qora-theme', {
        base: 'vs',
        inherit: true,
        rules: [
            { token: 'keyword', foreground: '0000FF', fontStyle: 'bold' },  // blue
            { token: 'type', foreground: '267F99' },                         // teal   (Qubit/int/float/bit/angle)
            { token: 'gate', foreground: 'AF00DB', fontStyle: 'bold' },      // purple (gates)
            { token: 'number', foreground: '098658' },                       // green  (numbers, pi)
            { token: 'identifier', foreground: '001080' },                   // dark blue
            { token: 'operator', foreground: '808080' },                     // grey
            { token: 'delimiter', foreground: '808080' },                    // grey
        ],
        colors: {}
    });

    // --- hover docs: keyed by the exact word under the cursor ---
    const QORA_DOCS = {
        // single-qubit gates
        'H': '**H** — 하다마드 게이트\n\n큐비트를 **중첩**(50/50)으로 만들어요.\n|0⟩ → (|0⟩+|1⟩)/√2\n\n`H(q[0]);` → `h q[0];`',
        'X': '**X** — 파울리-X (NOT)\n\n큐비트를 **뒤집어요**. |0⟩ ↔ |1⟩\n\n`X(q[0]);` → `x q[0];`',
        'Y': '**Y** — 파울리-Y\n\n비트와 위상을 함께 뒤집는 회전.\n\n`Y(q[0]);` → `y q[0];`',
        'Z': '**Z** — 파울리-Z (위상 뒤집기)\n\n|1⟩의 **위상만** 뒤집어요(부호 −). |0⟩은 그대로.\n\n`Z(q[0]);` → `z q[0];`',
        'S': '**S** — 위상 게이트 (√Z)\n\nZ축으로 90° 회전 (Z의 절반).\n\n`S(q[0]);` → `s q[0];`',
        'T': '**T** — π/8 게이트 (√S)\n\nZ축으로 45° 회전 (S의 절반).\n\n`T(q[0]);` → `t q[0];`',
        // multi-qubit gates
        'CNOT': '**CNOT** — 제어-NOT (2큐비트)\n\n**제어**가 |1⟩일 때만 **대상**을 뒤집어요. **얽힘**을 만드는 핵심 게이트.\n\n`CNOT(q[0], q[1]);` → `cx q[0], q[1];`',
        'CX': '**CX** — 제어-NOT (CNOT과 같음)\n\n제어가 |1⟩일 때 대상을 뒤집어요.\n\n`CX(q[0], q[1]);` → `cx q[0], q[1];`',
        'CZ': '**CZ** — 제어-Z (2큐비트)\n\n두 큐비트가 모두 |1⟩일 때 위상을 뒤집어요.\n\n`CZ(q[0], q[1]);` → `cz q[0], q[1];`',
        'SWAP': '**SWAP** — 교환 (2큐비트)\n\n두 큐비트의 상태를 서로 **맞바꿔요**.\n\n`SWAP(q[0], q[1]);` → `swap q[0], q[1];`',
        'CCX': '**CCX** — 토폴리 (3큐비트)\n\n**제어 2개**가 모두 |1⟩일 때만 대상을 뒤집어요. 양자판 AND.\n\n`CCX(q[0], q[1], q[2]);` → `ccx q[0], q[1], q[2];`',
        // rotation gates
        'Rx': '**Rx(θ, q)** — X축 회전\n\n큐비트를 X축으로 **각도 θ만큼** 회전. θ는 `pi/2`, `0.5` 등.\n\n`Rx(pi/2, q[0]);` → `rx(pi/2) q[0];`',
        'Ry': '**Ry(θ, q)** — Y축 회전\n\n큐비트를 Y축으로 각도 θ만큼 회전.\n\n`Ry(0.5, q[0]);` → `ry(0.5) q[0];`',
        'Rz': '**Rz(θ, q)** — Z축 회전\n\n큐비트의 **위상**을 각도 θ만큼 회전.\n\n`Rz(pi/4, q[0]);` → `rz(pi/4) q[0];`',
        // measurement
        'M': '**M(q)** — 측정\n\n큐비트를 측정해서 **0 또는 1로 확정**(붕괴)시키고, 그 값을 비트로 돌려줘요.\n\n`bit r = M(q[0]);` → `r = measure q[0];`',
        // constants & keywords
        'pi': '**pi** — 원주율 π ≈ 3.14159\n\n회전 각도에 써요: `Rx(pi/2, q)`(90°), `Rz(pi/4, q)`(45°).',
        'operation': '**operation** — 함수(서브루틴) 정의\n\n`operation 이름(파라미터) { … }`\n`Main`이 진입점, 나머지는 OpenQASM `def`가 돼요.',
        'use': '**use** — 큐비트 할당\n\n`use q = Qubit[n];` — 큐비트 n개를 빌려 q로.\n\n→ `qubit[n] q;`',
        'Qubit': '**Qubit** — 양자 비트 타입\n\n연산 파라미터는 `Qubit[] q`, 할당은 `use q = Qubit[2];`처럼 써요. 파라미터의 크기는 `q.Count`로 확인해요.',
        'new': '**new T[N]** — 고전 배열 만들기\n\n`int[] values = new int[3];`처럼 쓰면 0으로 초기화된 원소 N개를 만들어요.',
        'const': '**const** — 불변 변수\n\n`const int n = 3;` / `const k = 5`. 한 번 정하면 못 바꿔요.',
        'var': '**var** — 가변 변수\n\n`var i = 0;` 후 `i = i + 1;`로 바꿀 수 있어요.',
        'bit': '**bit** — 고전 비트 (0/1)\n\n측정 결과를 담아요. `bit r = M(q[0]);`',
        'int': '**int** — 정수\n\n`int i = 0;` / `const int n = 3;`',
        'float': '**float** — 실수\n\n`float[] values = [0.25, 0.5];`처럼 스칼라나 배열 원소에 써요.',
        'angle': '**angle** — 회전 각도\n\n`angle theta = pi/2;`처럼 양자 게이트의 각도를 나타내요.',
        'Count': '**Count** — 배열 길이\n\n`q.Count`나 `values.Count`처럼 읽으며, 인덱스 반복 범위는 보통 `0..array.Count-1`로 써요.',
        'if': '**if** — 조건 분기\n\n`if (r == 1) { … }`. 보통 측정 결과 r로 분기(측정 피드백).',
        'for': '**for** — 범위 반복\n\n`for i in 0..2 { … }` — i가 0,1,2 (양끝 포함).',
        'while': '**while** — 조건 반복\n\n`while (i == 0) { … }`. 조건이 맞는 동안 반복.',
        'repeat': '**repeat** — 반복-until\n\n`repeat { … } until (r == 1);` — 조건 만족까지 반복(최소 한 번 실행).',
    };

    monaco.languages.registerHoverProvider('qora', {
        provideHover(model, position) {
            const word = model.getWordAtPosition(position);
            if (!word) return null;
            const md = QORA_DOCS[word.word];
            if (!md) return null;
            return {
                range: new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn),
                contents: [{ value: md }]
            };
        }
    });
})();
