#Requires -Version 7.0
<#
.SYNOPSIS
    把本插件的发行版本号写进所有落点。

.DESCRIPTION
    本仓库只有一个插件,版本号却有**两个**落点,而且它们的性质完全不同:

      VelaShell.Plugin.DockerPanel/plugin.json   "version"
          —— **分发形态的事实来源**。打包器出的是 <id>-<这个 version>.vpx,宿主的插件
             列表里显示的也是它。不写它,发 0.4.0 出来的仍旧是 velashell.dockerpanel-0.3.1.vpx。

      Directory.Build.props                      <VelaPluginVersion>
          —— 给 MSBuild 用的副本(AssemblyVersion / FileVersion 读不了带注释的 JSONC)。
             不写它,程序集版本停在上一版:崩溃栈与文件属性页里是个假数字,而**没有任何
             东西会报错**。

    两处必须一致,所以永远由本脚本一次写全,不要手改任何一处。

    发版流水线在解析出 Release 标签之后**第一件事**就是跑本脚本(见
    .github/workflows/release.yml),因此产物永远与标签一致,与仓库里当时提交了什么无关;
    发布成功后由 sync-main 任务开一个 PR 把改动回写 main,让仓库自己也保持诚实。

    也可以本地先跑一遍再提交,那样发版时脚本就是个空操作。

    > 兄弟仓库 velashell-plugins 的脚本同名同形,只是那边一个仓库装着四个插件,跑的是
    > 「统一发布列车」,落点里是 `plugins/*/plugin.json` 一整批。本仓库只有一个。

.PARAMETER Version
    目标版本,SemVer(0.4.0 或 0.4.0-preview.1)。

.PARAMETER Check
    只报告不落盘;有任何一处不同步就以退出码 1 结束。CI 用它做「仓库是否已同步」的体检。

.EXAMPLE
    pwsh scripts/Set-Version.ps1 0.4.0

.EXAMPLE
    pwsh scripts/Set-Version.ps1 0.4.0 -Check
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)] [string] $Version,
    [switch] $Check
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "'$Version' 不是合法 SemVer。用 0.4.0 或 0.4.0-preview.1 这种形式。"
}

$root = Split-Path -Parent $PSScriptRoot

# ── 落点清单 ────────────────────────────────────────────────────────────────
# 每一项:文件、正则、替换串。正则务必**锚定到唯一的上下文**,别用裸版本号 ——
# plugin.json 里 minSdkVersion 与 version 长得一模一样,认错了会把 SDK 门槛也改掉。
$targets = @(
    @{
        Path        = 'Directory.Build.props'
        Pattern     = '(?<pre><VelaPluginVersion[^>]*>)[^<]+(?<post></VelaPluginVersion>)'
        Replacement = "`${pre}$Version`${post}"
        What        = 'VelaPluginVersion(程序集版本)'
    },
    @{
        Path = 'VelaShell.Plugin.DockerPanel/plugin.json'
        # 只认顶层的 "version" 键。带引号前缀天然排除了 minSdkVersion 这类以 Version
        # 结尾的键名 —— 它们里面没有 `"version` 这个子串(大小写敏感,正则默认如此)。
        Pattern     = '(?<pre>"version"\s*:\s*")[^"]+(?<post>")'
        Replacement = "`${pre}$Version`${post}"
        What        = 'plugin.json 的 version(决定 .vpx 文件名与宿主里显示的版本)'
    }
)

$drift = @()
foreach ($target in $targets) {
    $path = Join-Path $root $target.Path
    if (-not (Test-Path $path)) { throw "落点文件不存在:$($target.Path)(脚本与仓库结构脱节了)" }

    $original = Get-Content -Raw $path
    $updated = [regex]::Replace($original, $target.Pattern, $target.Replacement)

    if ($updated -eq $original -and $original -notmatch $target.Pattern) {
        # 正则一处都没匹配上 = 文件结构变了,而不是「已经是目标版本」。这两种情况
        # 结果都是「没有改动」,但含义天差地别,不区分的话 -Check 会给出假绿灯。
        throw "在 $($target.Path) 里找不到 $($target.What) 的落点(正则没匹配上)。改了文件结构就要同步改本脚本。"
    }

    if ($updated -eq $original) { continue }   # 已经是目标版本

    $drift += "  $($target.Path) —— $($target.What)"
    if (-not $Check) {
        # 不带 BOM 写回:仓库里这两个文件本来就是无 BOM 的,写回时加上会让 diff 多一行噪声。
        [IO.File]::WriteAllText($path, $updated, [Text.UTF8Encoding]::new($false))
        Write-Host "已更新 $($target.Path) —— $($target.What) → $Version"
    }
}

if ($drift.Count -eq 0) {
    Write-Host "版本号已经是 $Version,无需改动。"
    exit 0
}

if ($Check) {
    Write-Host "以下落点与 $Version 不同步:"
    $drift | ForEach-Object { Write-Host $_ }
    Write-Host ""
    Write-Host "跑 ``pwsh scripts/Set-Version.ps1 $Version`` 修正。"
    exit 1
}

Write-Host "完成:$($drift.Count) 处已同步到 $Version。"

# 显式 exit 0,别靠「脚本正常结束」隐含成功。
# 调用方是 `& ./scripts/Set-Version.ps1 ...` 后面跟一句 if ($LASTEXITCODE) —— 而 .ps1
# **不调用 exit 就根本不会设置 $LASTEXITCODE**,它会原样保留调用方进程里的旧值。
# GitHub 的每个 pwsh 步骤都是全新进程,那里的旧值是 $null,于是 `$LASTEXITCODE -ne 0`
# 求值为真 —— 脚本明明改好了文件,步骤却报 exit code 1。
# (兄弟仓库 velashell-plugins 2026-08-22 发 1.0.0 时就是这么红的。)
exit 0
