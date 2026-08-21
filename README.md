# VelaShell.Plugin.DockerPanel

[VelaShell](https://github.com/joesdu/VelaShell) 的 Docker 管理面板插件 —— 在**已经连上的
SSH 会话**上管理远端的容器、镜像、卷、网络与 Compose 项目,含实时统计、日志、容器内文件
编辑与内置控制台。

**服务器上什么都不用改**:面板经 SSH 会话开一条到远端 `/var/run/docker.sock` 的直连通道,
说的是 Docker Engine 的 HTTP API,但不需要把 daemon 暴露在 2375/2376 上,也不需要第二套凭据。
也能直接管本机 Docker。

命令面板(`Ctrl+P` / `Ctrl+K`)搜 **Docker** → *打开 Docker 管理面板*。

- 插件说明、设计取舍与已知边界:[`VelaShell.Plugin.DockerPanel/README.md`](VelaShell.Plugin.DockerPanel/README.md)
- 插件开发规范:VelaShell 仓库的 `docs/plugins/dev-guide.md`

## 构建

本仓依赖尚未发布的 **VelaShell.PluginSdk 1.2.0**(新增远程隧道能力),从
`G:\VelaShell\artifacts\nuget` 这个本地源解析 —— 见 [`nuget.config`](nuget.config)。
SDK 发到 nuget.org 之后那一条就可以删掉。

```bash
dotnet build                                  # 开发构建
dotnet test                                   # 62 个单测,不需要宿主
dotnet build -c Release -t:PackVpx            # → bin/vpx/velashell.dockerpanel-0.2.0.vpx
```

## 许可

MIT,见 [LICENSE.txt](LICENSE.txt)。
