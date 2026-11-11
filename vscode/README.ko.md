[English](https://github.com/AJ-comp/Qora/blob/main/vscode/README.md) · **한국어** · [日本語](https://github.com/AJ-comp/Qora/blob/main/vscode/README.ja.md) · [Tiếng Việt](https://github.com/AJ-comp/Qora/blob/main/vscode/README.vi.md)

# Qora Language

**Qora**는 [Janglim](https://www.nuget.org/packages/Janglim) 파서 엔진 위에 만든 양자 토이 언어예요.
Q#/C# 느낌의 문법으로 회로를 적고 **OpenQASM 3**로 변환해요. 이 확장은 `.qor` 파일에 대해 문법
하이라이팅·설명·**실시간 오류 표시**·**변환 명령**과 반환 타입을 보존하는 로컬 실행을 제공해요.

현재 브랜치는 **Qora 0.40.0**을 포함하는 확장 **0.33.0**을 준비하며, 아직 배포 전입니다.
[확장 릴리스 노트](CHANGELOG.md#0330--unreleased)와
[언어 마이그레이션 안내](https://github.com/AJ-comp/Qora/blob/main/CHANGELOG.md#0400--unreleased)를 함께 확인해 주세요.

## 기능

- **문법 하이라이팅** — 키워드(`operation`/`use`/`new`/`if`/`for`/…), 타입(`Qubit`/`bool`/`int`/`float`/`bit`/`angle`), 게이트(`H`/`CNOT`/`Rx`/…), `pi`, 숫자, 연산자
- **hover 설명** — 게이트·키워드에 마우스를 올리면 짧은 설명 (예: `Rx`, `CNOT`, `M`)
- **실시간 파스 오류** — 타이핑하는 동안 파서가 돌며 문제 토큰에 빨간 밑줄을 그어요
- **상태바 피드백** — 활성 `.qor` 파일에 `Qora: OK`, 오류 개수, 파서 상태가 바로 보여요
- **Main CodeLens 액션** — `operation Main()` 위에서 OpenQASM 변환과 컴파일 단계 보기를 바로 실행할 수 있어요
- **처음 시작하기 안내** — 설치 후 VS Code의 시작하기 화면에서 Qora 안내를 따라갈 수 있어요
- **편집기 버튼** — `.qor` 파일을 열면 오른쪽 위 버튼으로 변환, 단계 보기, 예제 열기를 바로 쓸 수 있어요
- **예제 바로 시작** — **`Qora: 예제 열기`**로 `demo.qor`를 바로 열거나, **`Qora: 새 Bell 예제 만들기`**로 새 Bell 회로를 만들어봐요
- **프로그램 실행** — **`Qora: 프로그램 실행`**(또는 `Main` 위의 ▶ CodeLens)이 파일을 Amazon Braket 로컬 시뮬레이터에서 실행하고, 진입 operation의 반환값을 빈도별로 표시합니다. 반환값이 없는 `void` 진입점은 실행 성공과 shot 수만 표시합니다. 첫 실행 때 시뮬레이터를 자동으로 준비합니다(최초 1회 ~200MB 다운로드, 확장 전용 폴더).
- **OpenQASM으로 변환** — 명령 팔레트에서 **`Qora: OpenQASM으로 변환`** → 결과가 옆 편집기에 열려요
- **컴파일 단계 보기** — **`Qora: 컴파일 단계 보기`** 명령으로 AST, 소스 모양 HIR, SSA/CFG MIR와 분석 결과, 타입이 정해진 OpenQASM 타깃, 최종 OpenQASM까지 이어지는 파이프라인을 실시간으로 볼 수 있어요. 저장하면 갱신돼요
- **스니펫** — `operation`, `main`, `use`, `measure`, `for`, `if`, `bell`
- **괄호 자동 닫기 / 짝 맞추기**

## 실행 결과

실행 결과는 진입 operation이 명시적으로 반환한 `bool`, `bit`, `int`, `float`, `angle` 값입니다. `angle`은 라디안으로 표시합니다.
논리값인 `bool`은 `true`/`false`로, 측정 결과를 담는 `bit`는 `0`/`1`로 표시합니다.
예를 들어 `operation Main(): bit { use q = Qubit[1]; H(q[0]); return M(q[0]); }`는 shot마다 비트 하나를 반환합니다.
지역 변수에 저장한 측정값이나 helper의 반환값은 별도 실행 결과로 노출하지 않으며, 실행기가 마지막 측정을 추가하지도 않습니다.
큰 정수도 정확한 10진수로 보존합니다. 사용자 지정 컴파일러에는 현재 `entryResult` 메타데이터가 필요하므로, 구버전 번들을 사용하면 내부 측정값으로 대체하지 않고 업데이트를 안내합니다.

## 알려진 시뮬레이터 제한

자동 설치는 Braket SDK 1.127.8과 default simulator 1.43.1을 사용하며, 로컬 검증에 사용한 미배포 수정안은 포함하지 않습니다.
Qora 실행 검증 54개는 Braket 수정안 네 개를 결합한 환경에서 통과한 결과이므로, 일반 설치에서는 논리 배열 등 영향을 받는 기능에 제한이 남아 있습니다.
또한 수정안을 결합해도 `Qubit[]` helper 매개변수의 요소 측정과 일부 비연속 큐비트 배치의 제어 게이트 문제는 해결되지 않습니다.
영향을 받는 프로그램을 실행하기 전에 [백엔드 제한](https://github.com/AJ-comp/Qora/blob/main/CHANGELOG.md#known-execution-limitations)을 확인해 주세요.
컴파일 성공이 시뮬레이터 실행 결과의 정확성까지 보장하지는 않습니다.

## 파서는 확장에 포함돼 있어요

실시간 오류와 변환은 Qora 파서(.NET)가 담당하는데, **자체 포함(self-contained) 바이너리로 확장에 함께
배포**돼요 — 별도로 .NET을 설치할 필요가 없어요. 플랫폼별 빌드(Windows x64 / macOS Apple Silicon /
Linux x64)가 들어 있고, 확장이 자동으로 알맞은 것을 실행해요.

> 지원하지 않는 플랫폼이라면 하이라이팅·hover·스니펫은 그대로 동작하고 오류/변환만 꺼져요. 이때는
> `qora.command`(직접 만든 Qora 실행 파일) 또는 `qora.args`(+`dotnet`으로 `Qora.dll` 실행)로 연결할 수 있어요.

## 설정

| 설정 | 기본값 | 설명 |
|---|---|---|
| `qora.command` | *(비어 있음 → 번들 파서 사용)* | Qora 파서 실행 파일을 직접 지정(고급) |
| `qora.args` | `[]` | `qora.command`에 넘길 추가 인자(예: `Qora.dll` 경로) |

## 라이선스

MIT — 소스: [github.com/AJ-comp/Qora](https://github.com/AJ-comp/Qora).
