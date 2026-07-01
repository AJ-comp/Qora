[English](README.md) · [한국어](README.ko.md) · **日本語** · [Tiếng Việt](README.vi.md)

# Qora Language

**Qora** は [Janglim](https://www.nuget.org/packages/Janglim) パーサーエンジンの上に作られた、量子の
学習用トイ言語です。Q# / C# 風の構文で回路を書き、**OpenQASM 3** に変換します。この拡張機能は `.qor`
ファイルに対して、シンタックスハイライト・ホバー説明・**リアルタイムのエラー表示**・**変換コマンド**を提供します。

## 機能

- **シンタックスハイライト** — キーワード(`operation`/`use`/`if`/`for`/…)、型(`Qubit`/`int`/`bit`)、ゲート(`H`/`CNOT`/`Rx`/…)、`pi`、数値、演算子
- **ホバー説明** — ゲートやキーワードにマウスを重ねると短い説明が出ます(例: `Rx`, `CNOT`, `M`)
- **リアルタイム構文エラー** — 入力中にパーサーが動き、問題のトークンに下線を引きます
- **OpenQASM への変換** — コマンドパレットから **`Qora: Transpile to OpenQASM`** を実行 → 結果が横のエディターに開きます
- **スニペット** — `operation`, `main`, `use`, `measure`, `for`, `if`, `bell`
- **括弧の自動補完 / 対応表示**

## パーサーは拡張機能に同梱

リアルタイムエラーと変換は Qora パーサー(.NET)が担当し、**自己完結型(self-contained)バイナリとして拡張機能に
同梱**されています — .NET を別途インストールする必要はありません。プラットフォーム別のビルド(Windows x64 /
macOS Apple Silicon / Linux x64)が含まれており、拡張機能が自動的に適切なものを実行します。

> 対応していないプラットフォームでは、ハイライト・ホバー・スニペットはそのまま動作し、エラー/変換だけが無効になります。
> その場合は `qora.command`(自分でビルドした Qora 実行ファイル)または `qora.args`(+`dotnet` で `Qora.dll` を実行)を指定できます。

## 設定

| 設定 | 既定値 | 説明 |
|---|---|---|
| `qora.command` | *(空 → 同梱パーサーを使用)* | Qora 実行ファイルを直接指定(上級者向け) |
| `qora.args` | `[]` | `qora.command` に渡す追加引数(例: `Qora.dll` のパス) |

## ライセンス

MIT — ソース: [github.com/AJ-comp/Qora](https://github.com/AJ-comp/Qora)。
