# SULFUR Together

Co-op multiplayer for **SULFUR**.

**Language:** **English** · [简体中文](#简体中文) · [日本語](#日本語)

> **Version 1.0 — Public Beta.** It works, but it is still being polished. Expect bugs, back up your saves, and make sure everyone runs the same version.

---

## English

**SULFUR Together** lets you play SULFUR co-op. Explore the same procedurally generated levels, fight enemies together, revive downed teammates, see each other's weapons and attacks, break objects in the world, and drop items for each other.

You connect **inside the game** now — there is a built-in menu for hosting and joining over **Direct IP** or **Steam**. No config-file editing.

### ⚠️ Public Beta notice

This is a **public test build**. The main loop works from start to finish, but many systems are still being optimized and there is a lot left to refine.

- Bugs, visual desyncs, and the occasional broken boss or level transition can happen.
- **Back up your save files** before playing.
- Every player must use the **same version of SULFUR** and the **same version of SULFUR Together**.

This release is for players who are happy to test unfinished multiplayer and report problems.

### Requirements

- **SULFUR** (Steam)
- **BepInEx 5** — installed automatically as a dependency
- **SULFUR Native UI Lib 0.10.1** — installed automatically as a dependency. It powers the in-game connect menu, notifications, and translations.

You do **not** have to install these by hand if you use a mod manager (below).

### How to install (recommended: Gale)

**Gale** is a free, beginner-friendly mod manager. If you have never modded a game before, use this.

1. Download **Gale** from its official page: **https://github.com/Kesomannen/gale** — open the **Releases** section and grab the latest installer for your system.
2. Install and open Gale. When it asks which game to manage, choose **SULFUR**.
3. Open the **Browse mods** tab and search for **SULFUR Together**.
4. Click **Install**. Gale automatically pulls in everything it needs (BepInEx and SULFUR Native UI Lib) — you do not need to install those separately.
5. Press the **Launch game (modded)** button inside Gale. The first launch takes a little longer while BepInEx sets itself up.
6. Every player who wants to join must do the same steps and install the **same version**.

That's it — you're ready to host or join from inside the game.

<details>
<summary>Manual install (advanced, without a mod manager)</summary>

1. Install **BepInEx 5** for SULFUR.
2. Download **SULFUR Native UI Lib** and **SULFUR Together** from Thunderstore.
3. Put `SULFUR Together.dll`, `LiteNetLib.dll`, and the `lang` folder into `BepInEx/plugins/SULFUR Together/`, and install the UI Lib into its own `BepInEx/plugins/` folder the same way.
4. Launch the game.

</details>

### How to connect

Co-op is hosted and joined **from inside a loaded save** (not from the title screen).

1. Load into your game (the hub or any level).
2. Open the **Options** menu and go to the **SULFUR Together** page.
3. Set your **Player name**.
4. **To host:** press **Create game**. Then either:
   - **Steam:** press **Invite Friends via Steam** — no port forwarding needed; or
   - **Direct IP:** share the **LAN address** shown on the page (and, over the internet, forward the UDP port or use a VPN LAN). Everyone must use the same **Connection key**.
5. **To join:**
   - **Steam:** accept the invite (it joins automatically once you load a save), or paste the host's **Steam ID** and press **Join via Steam**; or
   - **Direct IP:** enter the host's **address**, **port**, and **connection key**, then press **Join game**.
6. Use **Close room** (host) or **Leave** (client) to end the session.

Your settings save automatically as you type. Quick keys still exist: **Page Down** links/follows the host, **Page Up** unlinks and returns you to solo play.

### Features

- Host-authoritative multiplayer over LiteNetLib
- Synced procedural level generation, seeds, and level transitions
- Remote players, held weapons, and player projectiles
- Enemy movement/combat sync; host-authoritative enemy damage, deaths, and ranged attacks
- Co-op downed / revive / death flow with an on-screen rescue prompt
- Boss-encounter sync for several bosses (Cousin, Witch, Lucia, Desert boss, …)
- Synced destructible objects and player-dropped items
- Independent per-player character, inventory, equipment, progression, and save
- Friendly-fire session setting (chosen by the host)
- End-of-run stats cards and join/leave notifications
- 14-language interface

### Saves, inventory, and loot

SULFUR Together does **not** use a shared character save. Each player keeps their own character, inventory, equipment, progression, personal loot, and save data. Items you deliberately drop into the world can be picked up by others. Shared-loot options are planned for a future version.

### No-pause multiplayer

The world does **not pause** while multiplayer is active — opening the inventory, the pause menu, dialogue, or moving the window out of focus does not stop the game. This is intentional, so players don't desync. **Get to a safe spot before opening menus.**

### Known issues

- A client that loads a boss level ahead of the host can desync (e.g. Cousin "infinite dialogue").
- Some enemies may briefly snap, teleport, or animate incorrectly.
- Bosses beyond the tested set may have serious sync problems.
- Blood, particles, sounds, and some animations are not always synchronized.
- Player collision is experimental; remote sprites/weapons are still being refined.
- Sessions with 4+ players are untested — 2 players is the most predictable.

This list is not exhaustive.

### Feedback & bug reports

**Thunderstore does not support bug reports or comments**, so please report elsewhere:

- **GitHub Issues (recommended):** https://github.com/ryuka-dev/SULFUR-Together/issues
- **Nexus Mods:** the mod's **Bug Reports** section

A good report includes: both host **and** client `LogOutput.log` files, who hosted, where it happened (level/boss), who triggered the event, what each player saw, whether it reproduces, and your other installed mods. Reports with **both** logs are far more useful than one side alone.

### Disclaimer

This is an unofficial, fan-made mod. It is **not** affiliated with or endorsed by Perfect Random or the SULFUR developers. SULFUR and its assets are the property of their respective owners.

---

## 简体中文

**SULFUR Together** 是一个让你和好友**联机合作**游玩 SULFUR 的模组。一起探索相同的随机生成关卡、并肩作战、救起倒地的队友、看到彼此的武器和攻击、破坏场景物件，并为对方丢下物品。

现在**在游戏内即可联机**——内置菜单支持通过 **直连 IP** 或 **Steam** 创建/加入房间，**无需手动改配置文件**。

### ⚠️ 公开测试版说明

这是一个 **公开测试版（Public Beta）**。主体流程已能从头玩到尾，但许多系统仍在优化中，**还有大量待打磨的部分**。

- 可能出现 Bug、画面不同步，偶尔会有 Boss 或关卡切换异常。
- 游玩前请**备份你的存档**。
- 所有玩家必须使用**相同版本的 SULFUR** 和**相同版本的 SULFUR Together**。

本版本面向愿意测试未完成的联机功能并反馈问题的玩家。

### 环境要求

- **SULFUR**（Steam 版）
- **BepInEx 5**——作为依赖自动安装
- **SULFUR Native UI Lib 0.10.1**——作为依赖自动安装。它提供游戏内联机菜单、通知与多语言翻译。

如果使用下面推荐的模组管理器，你**无需**手动安装这些依赖。

### 安装教程（推荐使用 Gale）

**Gale** 是一个免费、对新手友好的模组管理器。如果你此前从未给游戏装过模组，请使用它。

1. 从官方页面下载 **Gale**：**https://github.com/Kesomannen/gale** ——打开 **Releases（发行版）** 一栏，下载适合你系统的最新安装包。
2. 安装并打开 Gale。当它询问要管理哪个游戏时，选择 **SULFUR**。
3. 打开 **Browse mods（浏览模组）** 标签页，搜索 **SULFUR Together**。
4. 点击 **Install（安装）**。Gale 会自动装好所需的一切（BepInEx 和 SULFUR Native UI Lib），你无需另行安装。
5. 在 Gale 内点击 **Launch game（启动游戏，已加载模组）** 按钮。首次启动会因为 BepInEx 初始化而稍慢一些。
6. 每一位想加入的玩家都要做同样的步骤，并安装**相同的版本**。

完成后，你就可以在游戏内创建或加入房间了。

<details>
<summary>手动安装（进阶，不使用模组管理器）</summary>

1. 为 SULFUR 安装 **BepInEx 5**。
2. 从 Thunderstore 下载 **SULFUR Native UI Lib** 和 **SULFUR Together**。
3. 把 `SULFUR Together.dll`、`LiteNetLib.dll` 和 `lang` 文件夹放进 `BepInEx/plugins/SULFUR Together/`，并用同样方式把 UI Lib 装进它自己的 `BepInEx/plugins/` 目录。
4. 启动游戏。

</details>

### 如何联机

联机需要在**已载入的存档中**进行（不能在标题界面）。

1. 载入你的游戏（大厅或任意关卡）。
2. 打开 **Options（设置）** 菜单，进入 **SULFUR Together** 页面。
3. 设置你的 **Player name（玩家名）**。
4. **作为房主：** 点击 **Create game（创建游戏）**，然后二选一：
   - **Steam：** 点击 **Invite Friends via Steam（通过 Steam 邀请好友）**——无需端口转发；或
   - **直连 IP：** 把页面上显示的 **LAN 地址** 分享给队友（跨公网时需转发 UDP 端口或使用虚拟局域网）。所有人必须使用相同的 **Connection key（连接密钥）**。
5. **作为加入者：**
   - **Steam：** 接受邀请（载入存档后会自动加入），或粘贴房主的 **Steam ID** 后点击 **Join via Steam**；或
   - **直连 IP：** 填入房主的**地址**、**端口**和**连接密钥**，再点击 **Join game（加入游戏）**。
6. 用 **Close room（关闭房间，房主）** 或 **Leave（离开，客机）** 结束会话。

设置会在你输入时自动保存。快捷键依然可用：**Page Down** 链接并跟随房主，**Page Up** 取消链接、回到单人游玩。

### 功能一览

- 基于 LiteNetLib 的主机权威联机
- 同步的随机关卡生成、种子与关卡切换
- 远程玩家、手持武器与玩家子弹同步
- 敌人移动/战斗同步；主机权威的敌人伤害、死亡与远程攻击
- 合作的倒地/救援/死亡流程，附带屏幕救援提示
- 多个 Boss 的战斗同步（大表哥、女巫、Lucia、沙漠 Boss……）
- 可破坏物件与玩家丢落物品的同步
- 每位玩家独立的角色、背包、装备、进度与存档
- 友军伤害（友伤）会话开关（由房主决定）
- 结算统计卡片与加入/离开通知
- 14 种语言界面

### 存档、背包与掉落

SULFUR Together **不使用**共享角色存档。每位玩家保留自己的角色、背包、装备、进度、个人掉落与存档数据。你**主动**丢到世界里的物品可以被其他人捡起。共享掉落等选项计划在未来版本加入。

### 联机不暂停

联机进行时游戏世界**不会暂停**——打开背包、暂停菜单、对话或把窗口切到后台都不会让游戏停下。这是**有意为之**，以避免玩家之间不同步。**打开菜单前请先到安全的位置。**

### 已知问题

- 客机若比房主提前进入 Boss 关，可能不同步（例如表亲“无限对话”）。
- 部分敌人可能出现瞬移、抽动或动画错误。
- 已测试之外的 Boss 可能存在严重同步问题。
- 血液、粒子、音效与部分动画不一定同步。
- 玩家碰撞仍是实验性的；远程贴图/武器仍在打磨中。
- 4 人及以上的会话未经测试——2 人最为稳定。

以上并非完整清单。

### 反馈与错误报告

**Thunderstore 不支持提交反馈或评论**，因此请通过以下渠道反馈：

- **GitHub Issues（推荐）：** https://github.com/ryuka-dev/SULFUR-Together/issues
- **Nexus Mods：** 模组页面的 **Bug Reports** 栏目

一份好的报告应包含：房主与客机**双方**的 `LogOutput.log` 日志、谁是房主、问题发生在哪里（关卡/Boss）、由谁触发、每位玩家各自看到了什么、能否复现，以及你安装的其他模组。**同时**提供双方日志的报告远比只有一方的有用。

### 免责声明

这是一个非官方的粉丝制作模组，**并非**由 Perfect Random 或 SULFUR 开发者授权或认可。SULFUR 及其素材归各自的权利人所有。

---

## 日本語

**SULFUR Together** は、SULFUR を**協力プレイ（Co-op）**で遊ぶためのMODです。同じ自動生成ステージを一緒に探索し、敵と共に戦い、倒れた仲間を蘇生し、お互いの武器や攻撃を確認し、オブジェクトを破壊し、アイテムを落として渡し合えます。

接続は**ゲーム内で完結**します——**ダイレクトIP**または**Steam**でホスト／参加できる専用メニューを内蔵。**設定ファイルを編集する必要はもうありません。**

### ⚠️ パブリックベータについて

これは**公開テスト版（Public Beta）**です。メインの流れは最初から最後まで動作しますが、多くのシステムはまだ調整中で、**改善すべき点が数多く残っています**。

- バグ、表示のずれ、ボスやステージ遷移の不具合が起きることがあります。
- プレイ前に**セーブデータをバックアップ**してください。
- 全プレイヤーが**同じバージョンの SULFUR** と**同じバージョンの SULFUR Together** を使用する必要があります。

本リリースは、未完成のマルチプレイをテストし、問題を報告してくださる方向けです。

### 動作環境

- **SULFUR**（Steam版）
- **BepInEx 5**——依存関係として自動でインストールされます
- **SULFUR Native UI Lib 0.10.1**——依存関係として自動でインストールされます。ゲーム内の接続メニュー・通知・翻訳を担います。

下記のMODマネージャーを使えば、これらを手動でインストールする必要は**ありません**。

### インストール方法（Gale を推奨）

**Gale** は無料で初心者にも扱いやすいMODマネージャーです。MODを入れるのが初めての方は、これを使ってください。

1. 公式ページから **Gale** をダウンロード：**https://github.com/Kesomannen/gale** ——**Releases** の項目を開き、お使いの環境向けの最新版を入手します。
2. Gale をインストールして起動します。管理するゲームを尋ねられたら **SULFUR** を選びます。
3. **Browse mods** タブを開き、**SULFUR Together** を検索します。
4. **Install** をクリックします。Gale が必要なもの（BepInEx と SULFUR Native UI Lib）を自動でまとめて導入するので、個別に入れる必要はありません。
5. Gale 内の **Launch game（MOD適用で起動）** ボタンを押します。初回起動は BepInEx の初期化のため少し時間がかかります。
6. 参加したいプレイヤー全員が同じ手順を行い、**同じバージョン**をインストールしてください。

これで、ゲーム内からホスト／参加できます。

<details>
<summary>手動インストール（上級者向け・マネージャーなし）</summary>

1. SULFUR に **BepInEx 5** をインストールします。
2. Thunderstore から **SULFUR Native UI Lib** と **SULFUR Together** をダウンロードします。
3. `SULFUR Together.dll`、`LiteNetLib.dll`、`lang` フォルダを `BepInEx/plugins/SULFUR Together/` に入れ、UI Lib も同様にそれ専用の `BepInEx/plugins/` フォルダへ入れます。
4. ゲームを起動します。

</details>

### 接続方法

Co-op は**セーブをロードした状態**でホスト／参加します（タイトル画面では不可）。

1. ゲームをロードします（ハブまたは任意のステージ）。
2. **Options（設定）** メニューを開き、**SULFUR Together** ページへ進みます。
3. **Player name（プレイヤー名）** を設定します。
4. **ホストする場合：** **Create game（ゲームを作成）** を押し、次のいずれかを選びます：
   - **Steam：** **Invite Friends via Steam（Steamでフレンドを招待）** を押す——ポート開放は不要；または
   - **ダイレクトIP：** ページに表示される **LANアドレス** を共有します（インターネット越しの場合はUDPポートの開放、またはVPN LANを使用）。全員が同じ **Connection key（接続キー）** を使う必要があります。
5. **参加する場合：**
   - **Steam：** 招待を承認する（セーブをロードすると自動で参加）か、ホストの **Steam ID** を貼り付けて **Join via Steam** を押す；または
   - **ダイレクトIP：** ホストの**アドレス**・**ポート**・**接続キー**を入力し、**Join game（参加）** を押します。
6. **Close room（ホスト）** または **Leave（クライアント）** でセッションを終了します。

入力した設定は自動保存されます。ショートカットも利用可能です：**Page Down** でホストにリンクして追従、**Page Up** でリンク解除してソロに戻ります。

### 主な機能

- LiteNetLib によるホスト権威型マルチプレイ
- ステージ自動生成・シード・ステージ遷移の同期
- リモートプレイヤー、手持ち武器、プレイヤーの弾丸の同期
- 敵の移動／戦闘同期、ホスト権威の敵ダメージ・死亡・遠距離攻撃
- ダウン／蘇生／死亡の協力フロー（画面上の救助プロンプト付き）
- 複数ボスの戦闘同期（カズン、ウィッチ、Lucia、砂漠ボス ほか）
- 破壊可能オブジェクトとプレイヤーが落としたアイテムの同期
- プレイヤーごとに独立したキャラクター・インベントリ・装備・進行・セーブ
- フレンドリーファイア（同士討ち）のセッション設定（ホストが決定）
- リザルト統計カードと参加／退出の通知
- 14言語対応のインターフェース

### セーブ・インベントリ・戦利品

SULFUR Together は共有キャラクターセーブを**使いません**。各プレイヤーは自分のキャラクター・インベントリ・装備・進行・個人戦利品・セーブデータを保持します。あなたが**意図的に**ワールドへ落としたアイテムは他のプレイヤーが拾えます。共有戦利品などのオプションは今後のバージョンで予定しています。

### 一時停止しないマルチプレイ

マルチプレイ中はゲーム世界が**停止しません**——インベントリ、ポーズメニュー、会話を開いても、ウィンドウを非アクティブにしてもゲームは止まりません。これはプレイヤー間のずれを防ぐための**意図的な仕様**です。**メニューを開く前に安全な場所へ移動してください。**

### 既知の問題

- クライアントがホストより先にボスステージへ入ると同期がずれることがあります（例：カズンの「無限会話」）。
- 一部の敵が瞬間移動・カクつき・アニメーション不正を起こすことがあります。
- テスト済み以外のボスは深刻な同期問題を抱えている場合があります。
- 血・パーティクル・効果音・一部アニメーションは必ずしも同期しません。
- プレイヤー同士の当たり判定は実験的で、リモートのスプライト／武器は調整中です。
- 4人以上のセッションは未検証です——2人が最も安定します。

これは完全な一覧ではありません。

### フィードバックと不具合報告

**Thunderstore は不具合報告やコメントに対応していません**。以下からご報告ください：

- **GitHub Issues（推奨）：** https://github.com/ryuka-dev/SULFUR-Together/issues
- **Nexus Mods：** MODページの **Bug Reports** セクション

良い報告には、ホストとクライアント**両方**の `LogOutput.log`、誰がホストか、どこで起きたか（ステージ／ボス）、誰が何を起こしたか、各プレイヤーに何が見えたか、再現するか、導入している他のMODが含まれます。**両方**のログがある報告は、片方だけよりはるかに有用です。

### 免責事項

これは非公式のファンメイドMODであり、Perfect Random や SULFUR 開発者と提携・承認されたものでは**ありません**。SULFUR およびその素材は、それぞれの権利者に帰属します。
