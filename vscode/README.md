# Qora Language

**Qora**는 [Janglim](https://www.nuget.org/packages/Janglim) 파서 엔진 위에 만든 양자 토이 언어예요.
Q#/C# 느낌의 문법으로 회로를 적고, **OpenQASM 3**로 변환합니다. 이 확장은 `.qor` 파일에 대해
문법 하이라이팅·설명·**실시간 오류 표시**·**변환 명령**을 제공합니다.

## 기능

- **문법 하이라이팅** — 키워드(`operation`/`use`/`if`/`for`/…), 타입(`Qubit`/`int`/`bit`), 게이트(`H`/`CNOT`/`Rx`/…), `pi`, 숫자, 연산자
- **hover 설명** — 게이트·키워드에 마우스를 올리면 한국어 설명 (예: `Rx`, `CNOT`, `M`)
- **실시간 파스 오류(스퀴글)** — 타이핑하는 동안 파서가 돌며 문제 토큰에 빨간 밑줄을 그어요
- **OpenQASM으로 변환** — 명령 팔레트에서 **`Qora: Transpile to OpenQASM`** → 결과가 옆 편집기에 열려요
- **스니펫** — `operation`, `main`, `use`, `measure`, `for`, `if`, `bell`
- **괄호 자동 닫기 / 짝 맞추기**

## 파서는 확장에 포함돼 있어요

실시간 오류와 변환은 Qora 파서(.NET)가 담당하는데, **자체 포함(self-contained) 바이너리로 확장에 함께
배포**돼요 — 별도로 .NET을 설치할 필요가 없습니다. 플랫폼별 빌드(Windows x64 / macOS Apple Silicon /
Linux x64)가 들어 있고, 확장이 자동으로 알맞은 것을 실행합니다.

> 지원하지 않는 플랫폼이라면 하이라이팅·hover·스니펫은 그대로 동작하고, 오류/변환만 꺼져요.
> 이때는 `qora.command`(직접 만든 Qora 실행 파일) 또는 `qora.args`(+`dotnet` 로 `Qora.dll` 실행)로
> 연결할 수 있습니다.

## 설정

| 설정 | 기본값 | 설명 |
|---|---|---|
| `qora.command` | *(비어 있음 → 번들 파서 사용)* | Qora 파서 실행 파일을 직접 지정(고급) |
| `qora.args` | `[]` | `qora.command`에 넘길 추가 인자(예: `Qora.dll` 경로) |

## 개발 중 실행 (F5)

이 폴더를 VS Code로 열고 **F5**("Run Qora Extension") → "Extension Development Host" 창에서
`.qor` 파일을 열면 하이라이팅·hover·스니펫·스퀴글·변환을 바로 확인할 수 있어요.

## 확장 빌드/패키징 (기여자용)

플랫폼별 `.vsix`를 만들려면 (각 .vsix에 해당 플랫폼 파서 바이너리가 포함됩니다):

```bash
npm run package            # 지원 플랫폼 전체 → dist/
npm run package:win        # win32-x64 하나만
```

## 라이선스

MIT
