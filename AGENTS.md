# AGENTS.md

> 给 AI 代理与新加入者的操作约定。**动手之前先读完本文件,以及它指向的文档。**

## 一、开工前必读:velashell-docs

VelaShell 生态的**全部文档**集中在一个仓库:
**[VelaShellLabs/velashell-docs](https://github.com/VelaShellLabs/velashell-docs)**。
本仓库**不放** `docs/`、`docs-en/` —— 设计手册、开发规范与开发文档都在那边。

**在动任何代码之前**,先把下表中与你要改的部分相关的几篇读掉。跳过这一步直接改,
结果通常是两种:与既有设计冲突,或者重复实现一个已经存在的能力。

| 位置 | 内容 |
| --- | --- |
| [`zh/host/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/host) | 宿主分层架构与依赖方向、工程化重构蓝图、交互与界面规格、快捷键参考、设置项审计,以及 SFTP / FTP / Telnet / 串口 / Redis / S3 / 系统密钥链等可行性调研 |
| [`zh/plugins/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/plugins) | 插件系统设计蓝图 01–15(进程模型、IPC 协议、权限系统、UI 扩展、威胁模型、路线图)与[进度总览 STATUS](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/STATUS.md) |
| [`zh/sdk/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/sdk) | 插件契约 SDK 参考、SDK 仓库的发版流程 |
| [`zh/cli/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/cli) | `vela-plugin` 命令行手册、CLI 仓库的发版流程 |
| [`zh/templates/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/templates) | 插件开发指南、打包与发布、模板仓库的发版流程 |

英文镜像在 [`en/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/en),与 `zh/` 同构。
[仓库首页](https://github.com/VelaShellLabs/velashell-docs)有按「我想做什么」组织的快速入口表。

## 二、涉及文档的改动一律同步到 velashell-docs

**这是本文件最重要的一条。**

- 本仓库里**不新建** `docs/`、`docs-en/` 或任何成体系的文档目录。要写文档,去 velashell-docs 开 PR。
- 改了代码,而**行为、接口、配置项、命令行、构建流程或版本纪律**与现有文档对不上时,
  必须**同时**在 velashell-docs 提一个 PR 把文档改过来。两个 PR 在正文里互相引用,一起合。
  只改代码不改文档,等于让文档开始骗人 —— 而文档是别人照抄的。
- velashell-docs 的 `zh/` 与 `en/` 是**互为镜像**的两棵树,文件一一对应。改了中文就要改英文,
  反之亦然。漏一边,两棵树就开始漂。
- velashell-docs 内部的互相引用**一律走相对路径**(如 `../templates/dev-guide.md`),
  不要写回 GitHub 绝对 URL —— 文档集中到一个仓库,消掉的正是那种一改路径就断的跨仓库链接。
- **例外**:留在代码仓库里的少数几份文件不适用上述规则,因为它们服务的是「在这个仓库里写代码」
  这件事,搬走只会离使用场景更远。各仓库的例外清单见下面第三节。

## 三、本仓库:VelaShell.Plugin.DockerPanel(Docker / Compose 面板插件)

一个独立发布的 VelaShell 插件:容器、镜像、网络、卷与 Compose 项目的管理面板。

### 构建与测试

```bash
dotnet build                                  # 开发构建
dotnet test                                   # 单测,不需要起宿主
dotnet build -c Release -t:PackVpx            # → bin/vpx/velashell.dockerpanel-<版本>.vpx
```

### 写插件之前必须读的

- [开发指南](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/dev-guide.md) —— 清单、生命周期、能力 API、隔离模式、测试
- [SDK 参考](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/sdk/sdk-reference.md) —— 契约表面与能力域
- [权限系统](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/06-permission-system.md) —— 本插件要访问 Docker socket / 远端守护进程,属于敏感能力,申请与提示的口径以它为准
- [打包与发布](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/publishing.md) —— `.vpx`、签名与发到插件商店

### 视觉对齐宿主

面板几何与配色取自宿主的
[`DESIGN.md`](https://github.com/joesdu/VelaShell/blob/main/DESIGN.md),不发明新令牌。

宿主有**十几套具名主题**,不是只有明暗两套。因此:

- 不要在 `ThemeDictionaries` 的 `Dark` / `Light` 两格里写死配色 —— 那两格认的是
  `ThemeVariant`,七套暗色主题会共用同一份值,底色换了字没换。私有色一律映射到宿主的
  语义令牌(`VelaShell*` / `VelaText*` / `VelaBg*` / `Vela*Foreground` / `VelaScrim*`)。
- 界面取色用 `{DynamicResource VelaXxx}`。代码里一次性取到的画刷会停在旧主题上 ——
  `ActualThemeVariantChanged` **不覆盖**同明暗内部换肤(VelaDark → Tokyo Night 它不响),
  要用 `ThemeBrushes`(见 `Ui/ThemeBrushes.cs`)或 `Application.Current.ResourcesChanged`。
- 实心语义色上的文字不要写 `#FFFFFF`:暗色主题的语义色本身是亮色,白字压上去读不出来。
  用 `VelaErrorForeground` / `VelaWarningForeground` / `VelaSuccessForeground`。

### 插件版本号不要手改

本插件的版本有**两个**落点:`VelaShell.Plugin.DockerPanel/plugin.json` 的 `version`
(决定 `.vpx` 文件名与宿主里显示的版本)与 `Directory.Build.props` 的 `VelaPluginVersion`
(决定 `AssemblyVersion` / `FileVersion`)。只改一处**不会有任何报错**,后果是程序集版本
悄悄停在上一版。要改就跑 `pwsh scripts/Set-Version.ps1 <版本>`,两处一起动;
CI 的「版本号同步体检」兜底。

平时其实**根本不需要改**:发版流水线会按 Release 标签自动写这两处,发完再开 PR 回写 `main`。

### SDK 版本号不归你定

用到 SDK 新契约面时,**不要**自己改 `plugin.json` 的 `minSdkVersion`、
不要改 csproj 里 `VelaShell.PluginSdk.Build` 的版本、更不要为了编译通过去造本地包或加本地
NuGet 源。下一版 SDK 发什么号是排期决定的。正确做法是把话说清楚:
「用到了 SDK 的 X 能力,需要 SDK 发版后把 `minSdkVersion` 抬到那个号」。

### 留在本仓库的文档

`README.md`、`plan.md`(本插件自己的进展与待办)、`LICENSE`。
