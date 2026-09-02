# VelaShell.Plugin.DockerPanel

[VelaShell](https://github.com/joesdu/VelaShell) 的 Docker 管理面板插件 —— 在**已经连上的
SSH 会话**上管理远端的容器、镜像、卷、网络与 Compose 项目,含实时统计、日志、容器内文件
编辑与内置控制台。

**服务器上什么都不用改**:面板经 SSH 会话开一条到远端 `/var/run/docker.sock` 的直连通道,
说的是 Docker Engine 的 HTTP API,但不需要把 daemon 暴露在 2375/2376 上,也不需要第二套凭据。
也能直接管本机 Docker。

命令面板(`Ctrl+P` / `Ctrl+K`)搜 **Docker** → *打开 Docker 管理面板*。

- 插件说明、设计取舍与已知边界:[`VelaShell.Plugin.DockerPanel/README.md`](VelaShell.Plugin.DockerPanel/README.md)
- 插件开发规范:[开发指南](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/dev-guide.md)
- SDK 契约:[SDK 参考](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/sdk/sdk-reference.md)

## 构建

只引 `VelaShell.PluginSdk.Build` 一个包(契约 + 与宿主一致的 Avalonia + 打包器全随它到位),
从 nuget.org 解析,不需要任何本地源。SDK 版本钉在
[`VelaShell.Plugin.DockerPanel.csproj`](VelaShell.Plugin.DockerPanel/VelaShell.Plugin.DockerPanel.csproj)
的那条 `PackageReference` 上。

Avalonia 的版本由 SDK 包导出的权威值锁定,
[`Directory.Build.targets`](Directory.Build.targets) 在构建期核对(**VELAD1000**):
测试工程的 `Avalonia` / `Avalonia.Headless`(单测没有宿主可回落,得自己提供运行时那份),
以及插件工程的 `Avalonia.AvaloniaEdit`。升 SDK 之后按它的报错把版本跟上即可 ——
后者尤其值得这道闸:它漂了在本仓库一个测试都不会红,要等用户打开一个 `compose.yaml` 才炸。

```bash
dotnet build                                  # 开发构建
dotnet test                                   # 144 个单测,不需要宿主
dotnet build -c Release -t:PackVpx            # → bin/vpx/velashell.dockerpanel-<版本>.vpx
```

需要 Docker 守护进程的那几条 compose 用例在没有 daemon 的机器上自动判为 Inconclusive,
不会让整轮测试变红。

> **apiLevel 2**:本插件编译在 SDK 2.x 上,只能装在 2.x 宿主里。清单里的
> `"apiLevel": 2` 就是干这个的 —— 装到 1.x 宿主上时它会在**发现期**给出
> 「需要更新 VelaShell」,而不是等到装载时抛一个看不懂的程序集绑定异常。

## 发版

`.vpx` 由 GitHub Actions 构建并签名,见
[`.github/workflows/release.yml`](.github/workflows/release.yml)。

**版本号不用手工改。** 标签解析出来之后,流水线第一件事就是跑 `scripts/Set-Version.ps1`,
把该版本写进两个落点;发布成功后再由 `sync-main` 任务开一个 PR 把改动回写 `main`(等你合)。
所以发版只剩两步:

1. 合进 `main`;
2. 在 GitHub 上发 Release,标签填 `v<版本>`(如 `v0.4.0`;预发布用 `v0.4.0-preview.1`)。

产出自动挂到该 Release 上:已签名的 `velashell.dockerpanel-<版本>.vpx` 与 `SHA256SUMS.txt`。

### 版本号的两个落点

| 落点 | 作用 | 漏改的后果 |
| --- | --- | --- |
| `VelaShell.Plugin.DockerPanel/plugin.json` 的 `version` | `.vpx` 文件名与宿主里显示的插件版本 | 发 `0.4.0` 出来的仍旧是 `velashell.dockerpanel-0.3.1.vpx` |
| `Directory.Build.props` 的 `VelaPluginVersion` | `AssemblyVersion` / `FileVersion`(MSBuild 读不了带注释的 JSONC,所以要存一份副本) | 程序集版本停在上一版,**什么都不会报错** |

**两处都别手改**,跑脚本:

```bash
pwsh scripts/Set-Version.ps1 0.4.0          # 一次写全两处
pwsh scripts/Set-Version.ps1 0.4.0 -Check   # 只体检不落盘(CI 用的就是它)
```

本地先跑一遍再提交也行,那样发版时流水线里的那次就是个空操作。CI 的「版本号同步体检」
在 PR 上只提醒,合进 `main` / `dev` 后判红。

### 签名密钥:怎么生成、怎么放进 GitHub Secret

`.vpx` 必须签名 —— 它是用户手工安装与插件商店分发的形态,没有签名就没法说明
"这个包确实来自我"。流水线缺密钥时**直接失败**,不会悄悄发一个未签名的包。

**① 生成一对密钥**(只需一次,`vela-plugin` 随 SDK 包分发,也可 `dotnet tool install -g VelaShell.Plugin.Cli`):

```bash
vela-plugin keygen          # → 当前目录下 velashell-plugin-key.pem(P-256 PKCS#8)
```

它同时打印**公钥 base64** 与**指纹**(`SHA256:…`)—— 指纹是插件商店登记用的,
私钥文件请自己备份好,丢了就换不回同一个身份了。

> ⚠️ `keygen` 没有 `--help`:敲 `vela-plugin keygen --help` 会**直接生成一把密钥**,
> 而不是打印用法。

**② 把私钥转成 base64**(GitHub Secret 存不了二进制/多行文件,所以存它的 base64):

```powershell
# Windows PowerShell —— 一行输出,可直接全选复制
[Convert]::ToBase64String([IO.File]::ReadAllBytes("velashell-plugin-key.pem")) | Set-Clipboard
```

```bash
# Linux
base64 -w0 velashell-plugin-key.pem
# macOS(其 base64 没有 -w)
base64 < velashell-plugin-key.pem | tr -d '\n'
```

> ⚠️ **别用 `certutil -encode`**:它会在结果里加上 `-----BEGIN CERTIFICATE-----` 头尾和换行,
> 解出来不是原始 PEM,流水线那步会报 "did not decode to a PEM private key"。
> 要的是**文件字节**的 base64,一整行,没有别的东西。

**③ 存进仓库机密**:GitHub 仓库 → Settings → Secrets and variables → Actions →
New repository secret,名字填 **`KEY_PEM_FILE`**,值粘贴第 ② 步那一整行。

流水线会把它解回 `velashell-signing.pem`、校验首行确实是 `BEGIN … PRIVATE KEY`、
用它签包,跑完(含失败路径)立即删除。仓库的 `.gitignore` 也排除了 `*.pem` 作为兜底。

**本地想签一个包**:

```bash
dotnet build -c Release -t:PackVpx -p:VelaSigningKey=/path/to/velashell-plugin-key.pem
vela-plugin info bin/vpx/velashell.dockerpanel-<版本>.vpx   # flags 应含 Signed、signature 为 Valid
```

## 许可

MIT,见 [LICENSE.txt](LICENSE.txt)。
