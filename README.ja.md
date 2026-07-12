# SULFUR Together

[SULFUR](https://store.steampowered.com/app/2124120/SULFUR/) の**協力マルチプレイMOD**で、[BepInEx 5](https://github.com/BepInEx/BepInEx) プラグインとして作られています。

**言語：** [English](README.md) · [简体中文](README.zh-CN.md) · **日本語**

> **Version 1.0.1 — パブリックベータ修正版（Public Beta bugfix release）。** 協力プレイの流れは一通り動作しますが、多くのシステムはまだ調整中で、**最適化すべき点が数多く残っています**。バグを想定し、プレイ前にセーブをバックアップし、全プレイヤーが**同じバージョン**を使ってください。

---

SULFUR Together は原版の上にホスト権威型のネットワークプレイを追加します：ステージ生成／シードの同期、シーン遷移、リモートプレイヤー、敵状態のミラーリング、ボス戦の権威、ダウン／蘇生フロー、破壊物とワールドアイテムの同期 など。ネットワークは [LiteNetLib](https://github.com/RevenantX/LiteNetLib) を使用し、**ゲーム内**から**ダイレクトIP**または **Steam** でホスト／参加できます（設定ファイルの編集は不要）。

> これは非公式のファンメイドMODであり、Perfect Random や SULFUR 開発者と提携・承認されたものでは**ありません**。SULFUR およびその素材は各権利者に帰属します。

## パブリックベータの状態

これは**公開テスト版**です。メインの流れは最初から最後まで遊べますが、粗い部分・表示のずれ・一部経路での大量のデバッグログが残り、ボスや遷移の不具合がまれに起きます。**最適化すべき点が数多く残っています。****セーブをバックアップしてください。** ホストと全クライアントは接続に**同じバージョン**が必要です。

## 動作環境

- **SULFUR**（Steam版）
- SULFUR 用の **BepInEx 5**——依存として自動インストール
- **SULFUR Native UI Lib 0.10.1**（Thunderstore：`ryuka_labs-SULFUR_Native_UI_Lib`）——依存として自動インストール。ゲーム内接続メニュー・通知・14言語のローカライズを担います。（*ソフト依存*のため無くても動作しますが、ゲーム内UIは失われます。）

## インストール（プレイヤー向け）

遊ぶだけならビルドは不要です。

**推奨——[Gale](https://github.com/Kesomannen/gale)（初心者に優しいMODマネージャー）：**

1. **https://github.com/Kesomannen/gale** から Gale をダウンロード（**Releases** から最新版を入手）。
2. インストールして起動し、管理するゲームに **SULFUR** を選択。
3. **Browse mods** で **SULFUR Together** を検索して **Install**。Gale が BepInEx と SULFUR Native UI Lib を自動導入します。
4. Gale 内の **Launch game（MOD適用）** を押します。初回起動は少し遅くなります。
5. 全プレイヤーが**同じバージョン**を導入してください。

**手動：** BepInEx 5 を導入し、Thunderstore から **SULFUR Native UI Lib** と **SULFUR Together** をダウンロード、`SULFUR Together.dll`・`LiteNetLib.dll`・`lang/` フォルダを `BepInEx/plugins/SULFUR Together/` に入れます（UI Lib はそれ専用のプラグインフォルダへ）。

## 接続方法

Co-op は**セーブをロードした状態**でホスト／参加します（タイトル画面不可）：

1. セーブをロードし、**Options → SULFUR Together** を開いて **Player name** を設定。
2. **ホスト：** **Create game** を押し、**Invite Friends via Steam**（ポート開放不要）またはページに表示の **LANアドレス**（ダイレクトIP）を共有。全員で1つの **Connection key** を共有します。
3. **参加：** Steam招待を承認／ホストの **Steam ID** を貼って **Join via Steam**、またはホストの**アドレス＋ポート＋接続キー**を入力して **Join game**。
4. **Close room（ホスト）** ／ **Leave（クライアント）** でセッション終了。

設定は入力に応じて自動保存されます。ショートカット：**Page Down** でホストにリンクして追従、**Page Up** で解除しソロへ。キー割り当てと診断トグルは引き続き `BepInEx/config/com.ryuka.sulfur.together.cfg` にありますが、接続設定はゲーム内ページが管理します（`coop.json` に保存、外部設定マネージャーには出しません）。

## ソースからのビルド

ビルド手順とプロジェクト構成は英語 README の [Building from source](README.md#building-from-source) と [Project layout](README.md#project-layout) を参照してください：`LocalPaths.props.example` を `LocalPaths.props`（gitignore 済み・コミット禁止）へコピーして自分の環境パスを記入し、`dotnet build "SULFUR Together.csproj" -c Release` を実行します。任意の `NativeUiLibDll` はゲーム内UIのコンパイル用です。Release ビルドでは `Thunderstore/` 配布フォルダも自動更新されます。

## フィードバックと不具合報告

- **[GitHub Issues](https://github.com/ryuka-dev/SULFUR-Together/issues)（推奨）。**
- **Nexus Mods**——MODの Bug Reports セクション。

Thunderstore に報告先はないので、上記のいずれかをご利用ください。ホストとクライアント**両方**の `LogOutput.log`、誰がホストか、発生場所、誰が起こしたか、導入している他のMODを添えてください。

## ライセンス

**GNU General Public License v3.0（GPLv3）** の下で提供されます——[LICENSE](LICENSE) を参照。GPL は本MODのソースコードを対象とします。ゲーム素材（SULFUR由来のスプライトを含む）は原権利者に帰属し、GPL の対象では**ありません**。
