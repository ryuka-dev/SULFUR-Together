# SULFUR Together

[SULFUR](https://store.steampowered.com/app/2124120/SULFUR/) 的**联机合作模组**，以 [BepInEx 5](https://github.com/BepInEx/BepInEx) 插件形式实现。

**语言：** [English](README.md) · **简体中文** · [日本語](README.ja.md)

> **Version 1.0.1 — 公开测试版修复版（Public Beta bugfix release）。** 完整的合作流程已经跑通，但许多系统仍在打磨，**还有大量待优化的部分**。请预期会有 Bug，游玩前备份存档，并确保所有玩家运行**相同版本**。

---

SULFUR Together 在原版之上加入主机权威的联机：关卡生成/种子同步、场景切换、远程玩家、敌人状态镜像、Boss 战权威、倒地/救援流程、可破坏物与世界掉落同步等等。网络层基于 [LiteNetLib](https://github.com/RevenantX/LiteNetLib)，并可**在游戏内**通过**直连 IP** 或 **Steam** 创建/加入房间，无需改配置文件。

> 这是一个非官方的粉丝制作模组，**并非**由 Perfect Random 或 SULFUR 开发者授权或认可。SULFUR 及其素材归各自权利人所有。

## 公开测试版状态

这是一个**公开测试版**。主体流程已能从头玩到尾，但仍会有粗糙之处、画面不同步、部分路径存在大量调试日志，偶尔会出现 Boss 或切换异常，**还有大量待优化的部分**。**请备份存档。** 房主与所有客机必须运行**相同版本**才能连接。

## 环境要求

- **SULFUR**（Steam 版）
- 用于 SULFUR 的 **BepInEx 5**——作为依赖自动安装
- **SULFUR Native UI Lib 0.10.1**（Thunderstore：`ryuka_labs-SULFUR_Native_UI_Lib`）——作为依赖自动安装。它提供游戏内联机菜单、通知与 14 种语言的本地化。（模组以*软依赖*方式在缺少它时仍可运行，但会失去游戏内 UI。）

## 安装（玩家）

无需自行编译即可游玩。

**推荐——使用 [Gale](https://github.com/Kesomannen/gale)（对新手友好的模组管理器）：**

1. 从 **https://github.com/Kesomannen/gale** 下载 Gale（打开 **Releases** 取最新安装包）。
2. 安装并打开，选择 **SULFUR** 作为要管理的游戏。
3. 在 **Browse mods** 中搜索 **SULFUR Together** 并点击 **Install**。Gale 会自动装好 BepInEx 与 SULFUR Native UI Lib。
4. 在 Gale 内点击 **Launch game（已加载模组）**。首次启动会稍慢。
5. 每位玩家都需安装**相同版本**。

**手动安装：** 安装 BepInEx 5，从 Thunderstore 下载 **SULFUR Native UI Lib** 与 **SULFUR Together**，把 `SULFUR Together.dll`、`LiteNetLib.dll` 与 `lang/` 文件夹放进 `BepInEx/plugins/SULFUR Together/`（UI Lib 放进它自己的插件文件夹）。

## 如何联机

联机需在**已载入的存档中**进行（非标题界面）：

1. 载入存档，打开 **Options → SULFUR Together**，设置 **Player name（玩家名）**。
2. **房主：** 点击 **Create game**，然后用 **Invite Friends via Steam**（无需端口转发）或分享页面上显示的 **LAN 地址**（直连 IP）。所有人共用一个 **Connection key（连接密钥）**。
3. **加入：** 接受 Steam 邀请／粘贴房主 **Steam ID** 后点 **Join via Steam**，或填入房主的**地址 + 端口 + 连接密钥**再点 **Join game**。
4. **Close room（房主）** ／ **Leave（客机）** 结束会话。

设置随输入自动保存。快捷键：**Page Down** 链接并跟随房主，**Page Up** 取消链接回到单人。按键绑定与调试开关仍在 `BepInEx/config/com.ryuka.sulfur.together.cfg`，但连接设置由游戏内页面管理（存于 `coop.json`，不进外部配置管理器）。

## 从源码编译

编译方法与项目结构详见英文 README 的 [Building from source](README.md#building-from-source) 与 [Project layout](README.md#project-layout) 小节：将 `LocalPaths.props.example` 复制为 `LocalPaths.props`（已被 gitignore，切勿提交）填入本机路径，再执行 `dotnet build "SULFUR Together.csproj" -c Release`。可选的 `NativeUiLibDll` 用于编译游戏内 UI。Release 构建还会自动刷新 `Thunderstore/` 发布文件夹。

## 反馈与错误报告

- **[GitHub Issues](https://github.com/ryuka-dev/SULFUR-Together/issues)（推荐）。**
- **Nexus Mods**——模组的 Bug Reports 栏目。

Thunderstore 没有反馈渠道，请使用上述之一。请附上房主与客机**双方**的 `LogOutput.log`、谁是房主、发生位置、由谁触发，以及你安装的其他模组。

## 许可证

采用 **GNU 通用公共许可证 v3.0（GPLv3）**——见 [LICENSE](LICENSE)。GPL 覆盖本模组源代码。游戏素材（包括任何取自 SULFUR 的贴图）仍归其原权利人所有，**不**在 GPL 授权范围内。
