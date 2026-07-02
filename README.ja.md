[English](README.md) · [한국어](README.ko.md) · **日本語**

<div align="center">

<img src="docs/images/quantum-language-icon-128.png" alt="Qora" width="128" height="128">

# Qora

**学ぶために作られた量子プログラミング言語。**
Q#/C# 風の構文で書くと、検証済みの **OpenQASM 3** が出力されます。

[![VS Code Marketplace](https://img.shields.io/visual-studio-marketplace/v/qora-lang.qora-language?style=flat-square&label=VS%20Code&labelColor=111827&color=6b3fd4)](https://marketplace.visualstudio.com/items?itemName=qora-lang.qora-language)
[![Docs](https://img.shields.io/badge/docs-GitHub%20Pages-111827?style=flat-square&labelColor=111827&color=6b3fd4)](https://aj-comp.github.io/Qora/ja/)
[![Built on Janglim](https://img.shields.io/badge/built%20on-Janglim%200.3.0--preview.1-111827?style=flat-square&labelColor=111827)](https://www.nuget.org/packages/Janglim)
[![Emits OpenQASM 3.0](https://img.shields.io/badge/emits-OpenQASM%203.0-111827?style=flat-square&labelColor=111827)](https://openqasm.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-111827?style=flat-square&labelColor=111827)](LICENSE)

</div>

---

**Qora は量子プログラミングを *学ぶための* 小さな言語です。** 量子コードは通常、プログラムが物理的に
意味を成すかを検査してくれない Python ライブラリ(Qiskit、Cirq)か、理論を既に知っていることを前提と
する研究用言語で書かれます。Qora はその中間にあります: 馴染みのある C# 風の構文で回路を書くと、
コンパイラがそのプログラムが実際に量子コンピュータにできることかを検査し、一行ずつ確認できる標準の
**OpenQASM 3** を出力します。

教育言語であることが、すべての設計判断を形作っています:

- **エラーは出力より先に出て、自らを説明します。** コンパイルが通ったなら、その OpenQASM は有効です
  — 静かに間違った出力はありません。
- **コンパイラは透明です。** コマンド一つで、ソースが AST、型付き IR、合成された逆演算、そして QASM
  になる過程を見られます。
- **難しい量子のルールは前提ではなく強制です** — 汚れたキュービットの誤った再利用、測定の逆転、
  単一キュービットの位置へのレジスタ渡しはできません。

## ひと目でわかる例

<table>
<tr>
<th>Qora</th>
<th>出力される OpenQASM 3</th>
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

## クイックスタート

最速の方法は VS Code 拡張機能です — コンパイラ全体が同梱されているため、**他に何もインストールする
必要はありません** (.NET も Python も不要):

1. マーケットプレイスから **[Qora Language](https://marketplace.visualstudio.com/items?itemName=qora-lang.qora-language)**
   をインストールします。
2. `bell.qor` というファイルを作り、上の例を貼り付けます。
3. これでツールチェーンは完成です:
   - **入力中に**エラーへ下線が引かれます (`CNOT(q[0], q[0])` を試して、ホバーで理由を確認してみてください);
   - ゲートやキーワードにホバーすると短い説明が出ます (`H`、`use`、`M`、…);
   - **`Ctrl+Shift+P` → `Qora: Transpile to OpenQASM`** — コンパイル結果が横のエディターに開きます;
   - **`Qora: コンパイル段階を表示`** — ソース → AST → IR → (逆 IR) → QASM のパイプライン全体を、
     保存のたびに更新しながらライブ表示します。

リポジトリから始めるなら .NET 10 SDK で:

```bash
git clone https://github.com/AJ-comp/Qora.git
cd Qora
dotnet run --project src/Qora          # デモプログラムをパースして OpenQASM を出力
```

(同じバイナリが `qora --json` で拡張機能を駆動します — コンパイルごとに JSON 一行:
`{success, qasm, errors[]}`。)

## 5分でわかる言語ツアー

**キュービットとゲート。** キュービットは `use` で確保し、配列のようにアクセスします。ゲートは
関数呼び出しの形です:

```
use q = Qubit[3];              // キュービット3個、すべて |0⟩
H(q[0]);                       // 重ね合わせ
CNOT(q[0], q[1]);              // もつれ
Rx(pi/2, q[2]);                // 回転は角度が先
Reset(q[2]);                   // |0⟩ に戻す
```

組み込みゲート: `H X Y Z S T` · `CNOT/CX CY CZ SWAP CCX` · `Rx Ry Rz` · `Reset ResetAll`。

**測定**はキュービットを古典 `bit` に収縮させます — そして bit を得る唯一の方法です:

```
bit r = M(q[0]);               // 測定 (元に戻せません!)
r = M(q[0]);                   // 同じ bit に再測定
```

**古典制御フロー**は期待どおりで、測定結果に反応できます:

```
const int n = 2;
var count = 0;
for i in 0..n {  H(q[i]);  }
if (r == 1) {  X(q[1]);  } else {  Z(q[1]);  }
while (count < 2) {  count = count + 1;  }
repeat {  H(q[0]);  r = M(q[0]);  } until (r == 1);
```

**operation** はサブルーチンで(`Main` がエントリポイント)、**ファンクタ**がそれを変形します:

```
operation Prep(Qubit[2] q) {
    H(q[0]);
    Controlled X(q[0], q[1]);   // 任意のゲートに制御を追加
    T(q[1]);
}

Adjoint Prep(q);                // ★ コンパイラが Prep の逆演算を合成します:
                                //   ゲートは逆順+逆に、for ループは逆走、
                                //   if 分岐は内部反転、ネスト呼び出しは推移的に
```

この `Adjoint` の一行が Qora の看板機能です: 「この計算を元に戻して」が一語で済み、これが
[自動 uncomputation](docs/TODO.md) への最初の一歩です。

**間違えたときは、コンパイラが理由を教えてくれます** — 一度のコンパイルですべての違反を、出力の前に:

```
[QSEM001] in `Main`: `Adjoint Meas` cannot be compiled: it measures a qubit,
          and measurement is irreversible
[QSEM006] in `Main`: `Bell` expects 1 argument(s) but got 2
[QSEM016] in `Main`: index `q[5]` is out of range; `q` has 2 qubit(s) (valid: 0..1)
```

**出力の実行**: 出力される QASM は標準の OpenQASM 3 です。サブルーチンのないプログラムは Qiskit に
そのままロードできます(`qiskit.qasm3.loads(...)`)。`def` サブルーチンの対応は処理系によって異なるため、
フラットなプログラムが最も移植性が高いです。

## ドキュメント

すべてのドキュメントは GitHub Pages で **英語 · 韓国語 · 日本語** で公開されています — ブラウザの言語を自動検出し(フォールバックは英語)、どのページでも言語を切り替えられます:

| | |
|---|---|
| **[Qora で学ぶ量子ゲート](https://aj-comp.github.io/Qora/ja/book/)** | 全11章の本: キュービット状態、振幅、重ね合わせ、干渉、そして X/H/Z/CNOT ゲート — すべて実行可能な Qora コードで読みます |
| **[H ゲートを矢印で理解する](https://aj-comp.github.io/Qora/ja/)** | 矢印ベースの入門: 振幅はなぜ干渉するのか、H を2回かけるとなぜ \|0⟩ に戻るのか |
| **[Adjoint コンパイルパイプライン](https://aj-comp.github.io/Qora/ja/adjoint-pipeline.html)** | コンパイラの完全ウォークスルー — 一つの例をソース → AST → IR → 逆 IR → OpenQASM まで実際の出力で追跡します |

## コンパイラの仕組み

```
ソース ─パース─▶ AST ─lowering─▶ 型付き IR ─検証─▶ (エラー? すべて収集して報告し停止)
                                          └─ クリーン ─▶ 逆合成(Adjoint) ─▶ OpenQASM 3
```

- **型付き IR**(`src/Qora.Core/Ir/`)はコンパイラが最後まで所有します; パースエンジンは lowering の
  境界で止まります。
- **バリデータ**(QSEM001–017)は収集型です: 一度のコンパイルがすべての問題を報告します — 引数の種類と
  個数、レジスタサイズ、インデックス範囲、予約名、再帰、誤った位置の `use`、逆転できない `Adjoint`
  対象(理由が呼び出しグラフを通って伝播)など。
- **インバータ**は純粋な IR→IR パスです — 将来、自動 uncomputation を注入するパスがそのまま再利用
  する機構です。
- **モジュールシステム**(`import` / `namespace` / `open`)が進行中です: 文法は既にパースされ、次は
  リゾルバです([設計ドキュメント](docs/namespaces-design.md))。

## リポジトリ構成

| パス | 内容 |
|---|---|
| `src/Qora.Core` | コンパイラ: 文法、型付き IR、検証、逆合成、OpenQASM 出力 |
| `src/Qora` | コンソールランナー + 拡張機能が使う `--json` / `--stages` CLI 契約 |
| `src/Qora.Playground` | Blazor WASM プレイグラウンド (Monaco エディター、ライブ パース → AST → QASM) |
| `vscode/` | VS Code 拡張機能 (独立したバージョン管理、自己完結型コンパイラを同梱) |
| `docs/` | GitHub Pages コンテンツ: 本、ガイド、設計ドキュメント |

## ロードマップとリリース

- 方向性と優先順位: [docs/TODO.md](docs/TODO.md) — 次はモジュールシステムのリゾルバ、その後は効果
  解析(`qfree`/`mfree`)を経て **自動 uncomputation** へ向かいます。
- 言語のリリースノート: [CHANGELOG.md](CHANGELOG.md) (v0.1 → v0.11)。VS Code 拡張機能は別に
  バージョン管理されます: [vscode/CHANGELOG.md](vscode/CHANGELOG.md)。

## ライセンス

[MIT](LICENSE) — [Janglim](https://www.nuget.org/packages/Janglim) パーサーエンジンの上に構築されています。
