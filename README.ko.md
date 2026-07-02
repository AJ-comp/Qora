[English](README.md) · **한국어** · [日本語](README.ja.md)

<div align="center">

<img src="docs/images/quantum-language-icon-128.png" alt="Qora" width="128" height="128">

# Qora

**배우기 위해 만들어진 양자 프로그래밍 언어.**
Q#/C# 느낌의 문법으로 쓰면, 검증된 **OpenQASM 3**가 나옵니다.

[![VS Code Marketplace](https://img.shields.io/visual-studio-marketplace/v/qora-lang.qora-language?style=flat-square&label=VS%20Code&labelColor=111827&color=6b3fd4)](https://marketplace.visualstudio.com/items?itemName=qora-lang.qora-language)
[![Docs](https://img.shields.io/badge/docs-GitHub%20Pages-111827?style=flat-square&labelColor=111827&color=6b3fd4)](https://aj-comp.github.io/Qora/ko/)
[![Built on Janglim](https://img.shields.io/badge/built%20on-Janglim%200.3.0--preview.1-111827?style=flat-square&labelColor=111827)](https://www.nuget.org/packages/Janglim)
[![Emits OpenQASM 3.0](https://img.shields.io/badge/emits-OpenQASM%203.0-111827?style=flat-square&labelColor=111827)](https://openqasm.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-111827?style=flat-square&labelColor=111827)](LICENSE)

</div>

---

**Qora는 양자 프로그래밍을 *배우기 위한* 작은 언어입니다.** 양자 코드는 보통 프로그램이 물리적으로
말이 되는지 검사해주지 않는 Python 라이브러리(Qiskit, Cirq)로 쓰거나, 이론을 이미 안다고 가정하는
연구용 언어로 씁니다. Qora는 그 사이에 있습니다: 익숙한 C# 모양의 문법으로 회로를 쓰면, 컴파일러가
그 프로그램이 실제로 양자 컴퓨터가 할 수 있는 일인지 검사하고, 한 줄씩 들여다볼 수 있는 표준
**OpenQASM 3**를 방출합니다.

교육 언어라는 정체성이 모든 설계를 결정합니다:

- **에러는 출력보다 먼저 나오고, 스스로를 설명합니다.** 컴파일이 성공했다면 그 OpenQASM은 유효합니다
  — 조용히 틀린 출력은 없습니다.
- **컴파일러가 투명합니다.** 명령 하나로 소스가 AST, 타입 있는 IR, 합성된 역연산, 그리고 QASM이
  되는 과정을 볼 수 있습니다.
- **어려운 양자 규칙을 가정하지 않고 강제합니다** — 더러운 큐비트를 실수로 재사용하거나, 측정을
  되돌리거나, 단일 큐비트 자리에 레지스터를 넘길 수 없습니다.

## 맛보기

<table>
<tr>
<th>Qora</th>
<th>방출된 OpenQASM 3</th>
</tr>
<tr>
<td>

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

</td>
<td>

```
OPENQASM 3;
include "stdgates.inc";

def Bell(qubit[2] q) {
  h q[0];
  cx q[0], q[1];
}

qubit[2] q;
bit r;
Bell(q);
r = measure q[0];
```

</td>
</tr>
</table>

## 빠른 시작

가장 빠른 길은 VS Code 확장입니다 — 컴파일러 전체가 번들되어 있어 **아무것도 더 설치할 필요가
없습니다** (.NET도, Python도):

1. 마켓플레이스에서 **[Qora Language](https://marketplace.visualstudio.com/items?itemName=qora-lang.qora-language)**
   확장을 설치합니다.
2. `bell.qor` 파일을 만들고 위 예제를 붙여넣습니다.
3. 이걸로 도구 체인이 완성됐습니다:
   - **타이핑하는 동안** 에러에 밑줄이 그어집니다 (`CNOT(q[0], q[0])`을 쳐보고 hover로 이유를 확인해보세요);
   - 게이트나 키워드에 마우스를 올리면 짧은 설명이 뜹니다 (`H`, `use`, `M`, …);
   - **`Ctrl+Shift+P` → `Qora: Transpile to OpenQASM`** — 컴파일 결과가 옆 편집기에 열립니다;
   - **`Qora: 컴파일 단계 보기`** — 소스 → AST → IR → (역 IR) → QASM 파이프라인 전체를 실시간으로,
     저장할 때마다 갱신하며 보여줍니다.

저장소로 시작하려면 .NET 10 SDK로:

```bash
git clone https://github.com/AJ-comp/Qora.git
cd Qora
dotnet run --project src/Qora          # 데모 프로그램을 파싱해 OpenQASM을 출력
```

(같은 바이너리가 `qora --json`으로 확장을 구동합니다 — 컴파일당 JSON 한 줄:
`{success, qasm, errors[]}`.)

## 5분 언어 투어

**큐비트와 게이트.** 큐비트는 `use`로 할당하고 배열처럼 접근합니다. 게이트는 함수 호출 모양입니다:

```
use q = Qubit[3];              // 큐비트 3개, 전부 |0⟩
H(q[0]);                       // 중첩
CNOT(q[0], q[1]);              // 얽힘
Rx(pi/2, q[2]);                // 회전은 각도가 먼저
Reset(q[2]);                   // 다시 |0⟩으로
```

내장 게이트: `H X Y Z S T` · `CNOT/CX CY CZ SWAP CCX` · `Rx Ry Rz` · `Reset ResetAll`.

**측정**은 큐비트를 고전 `bit`로 붕괴시킵니다 — 그리고 bit를 얻는 유일한 방법입니다:

```
bit r = M(q[0]);               // 측정 (되돌릴 수 없습니다!)
r = M(q[0]);                   // 같은 bit에 재측정
```

**고전 제어흐름**은 기대하는 그대로이고, 측정 결과에 반응할 수 있습니다:

```
const int n = 2;
var count = 0;
for i in 0..n {  H(q[i]);  }
if (r == 1) {  X(q[1]);  } else {  Z(q[1]);  }
while (count < 2) {  count = count + 1;  }
repeat {  H(q[0]);  r = M(q[0]);  } until (r == 1);
```

**operation**은 서브루틴이고(`Main`이 진입점), **함자(functor)** 가 이를 변형합니다:

```
operation Prep(Qubit[2] q) {
    H(q[0]);
    Controlled X(q[0], q[1]);   // 어떤 게이트에든 제어를 추가
    T(q[1]);
}

Adjoint Prep(q);                // ★ 컴파일러가 Prep의 역연산을 합성합니다:
                                //   게이트는 역순+역으로, for 루프는 거꾸로,
                                //   if 분기는 내부 역전, 중첩 호출은 전이적으로
```

이 `Adjoint` 한 줄이 Qora의 대표 기능입니다: "이 계산을 되돌려줘"가 한 단어로 끝나고, 이것이
[자동 uncomputation](docs/TODO.md)으로 가는 첫 계단입니다.

**틀리면 컴파일러가 이유를 말해줍니다** — 한 번의 컴파일에 모든 위반을, 출력 전에:

```
[QSEM001] in `Main`: `Adjoint Meas` cannot be compiled: it measures a qubit,
          and measurement is irreversible
[QSEM006] in `Main`: `Bell` expects 1 argument(s) but got 2
[QSEM016] in `Main`: index `q[5]` is out of range; `q` has 2 qubit(s) (valid: 0..1)
```

**출력 실행하기**: 방출된 QASM은 표준 OpenQASM 3입니다. 서브루틴이 없는 프로그램은 Qiskit에 바로
로드됩니다(`qiskit.qasm3.loads(...)`); `def` 서브루틴 지원은 소비자마다 달라서, 평평한 프로그램이
가장 호환성이 좋습니다.

## 문서

모든 문서는 GitHub Pages에 **영어 · 한국어 · 일본어**로 공개되어 있습니다 — 브라우저 언어를 자동 감지하고(폴백은 영어), 모든 페이지에서 언어를 바꿀 수 있습니다:

| | |
|---|---|
| **[Qora로 배우는 양자 게이트](https://aj-comp.github.io/Qora/ko/book/)** | 11장짜리 책: 큐비트 상태, 진폭, 중첩, 간섭, 그리고 X/H/Z/CNOT 게이트 — 전부 실행 가능한 Qora 코드로 읽습니다 |
| **[H 게이트를 화살표로 이해하기](https://aj-comp.github.io/Qora/ko/)** | 화살표 기반 입문: 진폭은 왜 간섭하는지, H를 두 번 걸면 왜 \|0⟩으로 돌아오는지 |
| **[Adjoint 컴파일 파이프라인](https://aj-comp.github.io/Qora/ko/adjoint-pipeline.html)** | 컴파일러 완주 문서 — 예제 하나를 소스 → AST → IR → 역 IR → OpenQASM까지 실제 출력으로 추적합니다 |

## 컴파일러는 이렇게 동작합니다

```
소스 ─파싱─▶ AST ─lowering─▶ 타입 있는 IR ─검증─▶ (에러? 전부 수집해 보고하고 중단)
                                          └─ 깨끗함 ─▶ 역합성(Adjoint) ─▶ OpenQASM 3
```

- **타입 있는 IR**(`src/Qora.Core/Ir/`)은 컴파일러가 끝까지 소유합니다; 파스 엔진은 lowering
  경계에서 멈춥니다.
- **검증기**(QSEM001–017)는 수집형입니다: 한 번의 컴파일이 모든 문제를 보고합니다 — 인자의 종류와
  개수, 레지스터 크기, 인덱스 범위, 예약 이름, 재귀, 잘못 놓인 `use`, 되돌릴 수 없는 `Adjoint`
  대상(사유가 호출 그래프를 따라 전파됨) 등.
- **인버터**는 순수한 IR→IR 패스입니다 — 나중에 자동 uncomputation을 주입할 패스가 그대로
  재사용할 기계입니다.
- **모듈 시스템**(`import` / `namespace` / `open`)이 진행 중입니다: 문법은 이미 파싱되고, 다음은
  리졸버입니다 ([설계 문서](docs/namespaces-design.md)).

## 저장소 구조

| 경로 | 내용 |
|---|---|
| `src/Qora.Core` | 컴파일러: 문법, 타입 있는 IR, 검증, 역합성, OpenQASM 방출 |
| `src/Qora` | 콘솔 러너 + 확장이 소비하는 `--json` / `--stages` CLI 계약 |
| `src/Qora.Playground` | Blazor WASM 플레이그라운드 (Monaco 에디터, 실시간 파스 → AST → QASM) |
| `vscode/` | VS Code 확장 (독립 버저닝, 자체 포함 컴파일러 번들) |
| `docs/` | GitHub Pages 콘텐츠: 책, 가이드, 설계 문서 |

## 로드맵과 릴리스

- 방향과 우선순위: [docs/TODO.md](docs/TODO.md) — 다음은 모듈 시스템 리졸버, 그다음 효과
  분석(`qfree`/`mfree`)을 거쳐 **자동 uncomputation**으로 갑니다.
- 언어 릴리스 노트: [CHANGELOG.md](CHANGELOG.md) (v0.1 → v0.11). VS Code 확장은 별도로
  버저닝됩니다: [vscode/CHANGELOG.md](vscode/CHANGELOG.md).

## 라이선스

[MIT](LICENSE) — [Janglim](https://www.nuget.org/packages/Janglim) 파서 엔진 위에서 만들어졌습니다.
