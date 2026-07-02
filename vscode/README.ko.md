[English](https://github.com/AJ-comp/Qora/blob/main/vscode/README.md) · **한국어** · [日本語](https://github.com/AJ-comp/Qora/blob/main/vscode/README.ja.md) · [Tiếng Việt](https://github.com/AJ-comp/Qora/blob/main/vscode/README.vi.md)

# Qora Language

**Qora**는 [Janglim](https://www.nuget.org/packages/Janglim) 파서 엔진 위에 만든 양자 토이 언어예요.
Q#/C# 느낌의 문법으로 회로를 적고 **OpenQASM 3**로 변환해요. 이 확장은 `.qor` 파일에 대해 문법
하이라이팅·설명·**실시간 오류 표시**·**변환 명령**을 제공해요.

## 기능

- **문법 하이라이팅** — 키워드(`operation`/`use`/`if`/`for`/…), 타입(`Qubit`/`int`/`bit`), 게이트(`H`/`CNOT`/`Rx`/…), `pi`, 숫자, 연산자
- **hover 설명** — 게이트·키워드에 마우스를 올리면 짧은 설명 (예: `Rx`, `CNOT`, `M`)
- **실시간 파스 오류** — 타이핑하는 동안 파서가 돌며 문제 토큰에 빨간 밑줄을 그어요
- **처음 시작하기 안내** — 설치 후 VS Code의 시작하기 화면에서 Qora 안내를 따라갈 수 있어요
- **편집기 버튼** — `.qor` 파일을 열면 오른쪽 위 버튼으로 변환, 단계 보기, 예제 열기를 바로 쓸 수 있어요
- **예제 바로 시작** — **`Qora: 예제 열기`** 또는 **`Qora: 새 Bell 예제 만들기`**로 작동하는 코드부터 열어봐요
- **OpenQASM으로 변환** — 명령 팔레트에서 **`Qora: OpenQASM으로 변환`** → 결과가 옆 편집기에 열려요
- **컴파일 단계 보기** — **`Qora: 컴파일 단계 보기`** 명령으로 파이프라인을 실시간 관찰: AST → QoraIR → 역 IR(`Adjoint` 사용 시 합성) → OpenQASM. 저장하면 갱신돼요
- **스니펫** — `operation`, `main`, `use`, `measure`, `for`, `if`, `bell`
- **괄호 자동 닫기 / 짝 맞추기**

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
