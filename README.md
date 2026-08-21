# VelaShell.Plugin.DockerPanel

[VelaShell](https://github.com/joesdu/VelaShell) 的 Docker 管理面板插件 —— 在**已经连上的
SSH 会话**上管理远端的容器、镜像、卷、网络与 Compose 项目,含日志、统计、进程表与空间回收。

不需要在服务器上暴露 daemon 端口,也不需要第二套凭据:面板复用宿主已建立的会话,
用服务器自带的 `docker` CLI 干活。

命令面板(`Ctrl+P` / `Ctrl+K`)搜 **Docker** → *打开 Docker 管理面板*。

- 插件说明、设计取舍与已知边界:[`VelaShell.Plugin.DockerPanel/README.md`](VelaShell.Plugin.DockerPanel/README.md)
- 插件开发规范:VelaShell 仓库的 `docs/plugins/dev-guide.md`

## 构建

```bash
dotnet build                                  # 开发构建
dotnet test                                   # 单测,不需要宿主
dotnet build -c Release -t:PackVpx            # → bin/vpx/velashell.dockerpanel-0.1.0.vpx
```

## 许可

MIT,见 [LICENSE.txt](LICENSE.txt)。
