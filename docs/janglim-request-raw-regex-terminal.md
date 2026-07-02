# [Janglim 요청] 일반 터미널의 raw 정규식 지원 (문자열 리터럴용)

> **✅ 처리 완료 — Janglim `0.3.0-preview.1` (2026-07-03 확인).**
> 요청서의 "플래그" 안 대신 전용 토큰 타입으로 구현됨: `TokenType.Literal.StringLiteral` /
> `CharLiteral`의 Value는 `\b` 감싸기 없이 raw 정규식으로 사용된다 (엔진 커밋 `03c8180`).
> Qora 반영: 패키지 0.3.0-preview.1로 업그레이드, `StringLit` 터미널 복원
> (`"[^"]*"`), 문자열 경로 import(`import "gates lib.qor";`)가 파싱됨 — 공백 포함 이름까지 한
> 토큰으로. 수용 기준(나눗셈 `/`, `//` 주석, `0.5` Float, 기존 프로그램) 전부 회귀 없음 확인.

- 요청 프로젝트: **Qora** (Janglim NuGet 소비자)
- 날짜: 2026-07-02
- 우선순위: **낮음 / 비차단** — Qora 모듈 시스템 v1은 bare 이름 import(`import gates_lib;`)로 설계를
  전환해 당장 막힌 것은 없음. Qora가 진짜 문자열 값(메시지·경로 등)을 도입하는 시점에 필요.

## 배경

현재 `Terminal(type, value, caption, meaning, bWordPattern)`의 패턴 처리는 두 모드뿐:

| bWordPattern | 동작 |
|---|---|
| `false` | value를 **문자 그대로** 매칭 (정규식 아님) — 키워드/연산자용 |
| `true` | value를 정규식으로 쓰되 렉서가 **`\b` … `\b`로 감쌈** — 식별자/숫자용 |

`\b` 감싸기는 단어 모양 토큰(`[0-9]+`, `[_a-zA-Z][_a-zA-Z0-9]*`)에는 올바른 장치지만,
**비단어 문자로 시작/끝나는 토큰은 원천적으로 매칭 불가**하게 만든다.

## 문제 — 문자열 리터럴 토큰을 정의할 수 없음

```csharp
// Qora가 시도한 정의:
new Terminal(TokenType.Literal, "\"[^\"]*\"", "string", true, true);
```

렉서가 `\b"[^"]*"\b`로 감싸는데, 따옴표(`"`)는 비단어 문자라서 앞이 공백(비단어)인 일반 소스에서
`\b`가 성립하지 않음 → `import "lib.qor";`의 여는 따옴표에서 매칭 실패(파스 에러 CE0001).
`bWordPattern: false`로는 정규식 자체가 불가(문자 그대로 매칭)라 양쪽 다 막혀 있음.

## 선례 — 주석 특례의 일반화 요청

`0.2.0-preview.3`에서 **Comment 타입 토큰에 한해** value를 `\b` 없이 raw 정규식으로 쓰는 경로가
이미 추가됨(Qora의 `//.*$` 줄주석이 이걸로 동작). 다만 Comment 토큰은 파스 스트림에서 제거되므로,
**값을 유지해야 하는(meaning=true) 일반 토큰**은 이 경로를 쓸 수 없음.

**요청: 이 raw 경로를 일반 토큰으로 일반화** — 예시 API(형태는 자유):

```csharp
// 안 A) 새 플래그
new Terminal(TokenType.Literal, "\"[^\"]*\"", "string", meaning: true, bWordPattern: true, bRawPattern: true);
// 안 B) bWordPattern을 3값 enum으로: Literal / WordRegex / RawRegex
```

요구 동작:
1. value를 `\b` 감싸기 없이 정규식 그대로 사용
2. 토큰이 파스 스트림에 남고(meaning=true) 매칭된 원문이 AST 터미널 값으로 유지
3. 기존 longest-match 경쟁에 정상 참여 (아래 회귀 케이스 참고)

## 수용 기준 (테스트 케이스)

```
import "lib.qor";      → StringLit("\"lib.qor\"") 한 토큰
"a b c"                → 공백 포함 한 토큰
""                     → 빈 문자열 토큰
x = 1 / 2;             → `/` 나눗셈 유지 (주석/문자열과 충돌 없음)
// comment             → 줄주석 기존 동작 유지
0.5                    → Float 한 토큰 유지 (Dot 연산자와 경쟁 회귀 없음)
"미완성 문자열 (개행까지 닫는 따옴표 없음)  → 렉싱 에러 또는 명확한 실패 (무한/오매칭 금지)
```

## 참고

- 관련 기존 항목: AJPGS docs/TODO.md의 "true longest-match 렉서" (블록 주석 `/* */`용) — 본 요청과
  별개지만 같은 렉서 영역. 함께 설계하면 좋음.
- Qora 쪽 소비 지점: `src/Qora.Core/QoraGrammar.cs` (지원되면 StringLit 터미널 복원 예정).
