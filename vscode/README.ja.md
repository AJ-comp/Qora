[English](https://github.com/AJ-comp/Qora/blob/main/vscode/README.md) · [한국어](https://github.com/AJ-comp/Qora/blob/main/vscode/README.ko.md) · **日本語** · [Tiếng Việt](https://github.com/AJ-comp/Qora/blob/main/vscode/README.vi.md)

# Qora Language

**Qora** は [Janglim](https://www.nuget.org/packages/Janglim) パーサーエンジンの上に作られた、量子の
学習用トイ言語です。Q# / C# 風の構文で回路を書き、**OpenQASM 3** に変換します。この拡張機能は `.qor`
ファイルに対して、シンタックスハイライト・ホバー説明・**リアルタイムのエラー表示**・**変換コマンド**と、戻り値の型を保つローカル実行を提供します。

このブランチは **Qora 0.40.0** を同梱する拡張機能 **0.33.0** の準備段階で、まだ公開されていません。
[拡張機能のリリースノート](CHANGELOG.md#0330--unreleased)と
[言語の移行ガイド](https://github.com/AJ-comp/Qora/blob/main/CHANGELOG.md#0400--unreleased)も参照してください。

## 機能

- **シンタックスハイライト** — キーワード(`operation`/`use`/`new`/`if`/`for`/…)、型(`Qubit`/`bool`/`int`/`float`/`bit`/`angle`)、ゲート(`H`/`CNOT`/`Rx`/…)、`pi`、数値、演算子
- **ホバー説明** — ゲートやキーワードにマウスを重ねると短い説明が出ます(例: `Rx`, `CNOT`, `M`)
- **リアルタイム構文エラー** — 入力中にパーサーが動き、問題のトークンに下線を引きます
- **ステータスバー表示** — アクティブな `.qor` ファイルに `Qora: OK`、エラー数、またはパーサー状態を表示します
- **Main の CodeLens アクション** — `operation Main()` の上から OpenQASM 変換とコンパイル段階表示を実行できます
- **はじめにガイド** — VS Code の Getting Started 画面から Qora の最初の流れを確認できます
- **エディター上部のアクション** — `.qor` ファイルを開くと、変換・段階表示・サンプルをすぐ実行できます
- **すぐ使えるサンプル** — **`Qora: サンプルを開く`** で `demo.qor` を開くか、**`Qora: 新しい Bell サンプルを作成`** で小さな Bell 回路から始められます
- **プログラムの実行** — **`Qora: プログラムを実行`**(または `Main` 上の ▶ CodeLens)が Amazon Braket のローカルシミュレータで実行し、エントリー operation の戻り値を頻度別に表示します。`void` の場合は実行成功とショット数のみを表示します。初回にシミュレータを自動セットアップします(最初の1回のみ ~200MB を拡張機能専用フォルダへダウンロード)。
- **OpenQASM への変換** — コマンドパレットから **`Qora: Transpile to OpenQASM`** を実行 → 結果が横のエディターに開きます
- **コンパイル段階の表示** — **`Qora: コンパイル段階を表示`** で AST、ソース構造を保つ HIR、SSA/CFG MIR とその解析結果、型付けされた OpenQASM ターゲット、最終 OpenQASM まで進むパイプラインを確認できます。保存で更新されます
- **スニペット** — `operation`, `main`, `use`, `measure`, `for`, `if`, `bell`
- **括弧の自動補完 / 対応表示**

## 実行結果

実行結果はエントリー operation が明示的に返す `bool`、`bit`、`int`、`float`、`angle` (ラジアン) の値です。
論理値の `bool` は `true`/`false`、測定結果を保持する `bit` は `0`/`1` と表示します。
例えば `operation Main(): bit { use q = Qubit[1]; H(q[0]); return M(q[0]); }` は各ショットで1ビットを返します。
ローカル変数に保存した測定値や helper の戻り値は別の出力にはならず、実行器が最後の測定を追加することもありません。
大きな整数も正確な10進数で保持します。独自のコンパイラには現在の `entryResult` メタデータが必要です。古いバンドルでは内部測定値に置き換えず、更新を案内します。

## 既知のシミュレータ制限

標準セットアップは Braket SDK 1.127.8 と default simulator 1.43.1 を使用し、ローカル検証で使用した未リリースの修正は含みません。
Qora 実行検証54件は Braket の4つの修正を組み合わせた環境で成功した結果であり、通常のインストールでは論理配列などに制限が残ります。
修正を組み合わせても、`Qubit[]` の helper 引数の要素測定と、一部の非連続な量子ビット配置での制御ゲートの問題は未解決です。
該当するプログラムを実行する前に[バックエンドの制限](https://github.com/AJ-comp/Qora/blob/main/CHANGELOG.md#known-execution-limitations)を確認してください。
コンパイルの成功だけでは、シミュレータの結果の正しさまで保証されません。

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
