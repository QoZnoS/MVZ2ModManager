# =====================================================================
# MVZ2 Mod Manager —— 构建脚本
#
#   .\build.ps1                  构建（Debug）
#   .\build.ps1 -Release         构建 Release
#   .\build.ps1 -Pack            构建 + 打 zip 到 <仓库根>\dist\（框架依赖单文件，约 2MB）
#   .\build.ps1 -Pack -SelfContained
#                                免安装版（自包含单文件，约 63MB，目标机器不用装 .NET）
#   .\build.ps1 -Exe             只输出**裸 exe** 到 <仓库根>\dist\，不打 zip
#                                （可叠加 -SelfContained；写 -Pack -Exe 则两者都要）
#   .\build.ps1 -Run             构建后启动
#   .\build.ps1 -SelfTest        构建后跑无界面自检（写 _selftest.txt）
#   .\build.ps1 -SelfTest -ApplyRoundtrip
#                                自检再加上"模组包还原往返"，会在**临时合成的游戏目录**上
#                                真的跑一遍破坏性路径（清理根目录 / 恢复文件），不碰真实安装
#   .\build.ps1 -UiSelfTest      界面自检：把所有窗体/面板都构造出来并切一遍标签页，
#                                然后退出（写 _ui-selftest.txt），不需要人盯着看
#   .\build.ps1 -Screenshot      每个标签页画一张 PNG + 控件树到 screenshots\，
#                                用来核对版面（文字偏下 / 超宽溢出 / 列宽漂移）
#   .\build.ps1 -Clean           先删 bin/ obj/
#
# 注：直接运行 .ps1 若被 ExecutionPolicy 挡住（仓库根的 build.ps1 也一样），用
#   powershell -ExecutionPolicy Bypass -File .\build.ps1 ...
#
# 这是个**独立的桌面程序**，不参与仓库根的 mod 构建链 ——
# 它不需要 runtime\、不需要游戏目录、也不引用 DSHCore。
# 游戏目录只在**运行时**由程序自己找（设置里的选择 / MVZ2_GAME_DIR）。
# =====================================================================
[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$Pack,
    [switch]$SelfContained,
    [switch]$Exe,
    [switch]$Run,
    [switch]$SelfTest,
    [switch]$ApplyRoundtrip,
    [switch]$UiSelfTest,
    [switch]$Screenshot,
    [switch]$Clean
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$proj = Join-Path $here 'MVZ2ModManager.csproj'

function Write-Step($m) { Write-Host ""; Write-Host ("== " + $m) -ForegroundColor White }
function Write-Ok($m)   { Write-Host ("   " + $m) -ForegroundColor Green }
function Write-Info($m) { Write-Host ("   " + $m) -ForegroundColor Gray }

# 单文件 publish 到 $StageDir。两种模式产出的**都是单个 exe**，
# 差别只在要不要带上 .NET 运行时（体积差 ~400 倍）。
function Publish-OneFile($StageDir) {
    if ($SelfContained) {
        # 自包含单文件：目标机器不用装 .NET（~63MB 压缩后）
        Write-Info "自包含单文件（体积大，但换机器就能跑，不需要 .NET）"
        & dotnet publish $proj -c $config -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -o $StageDir --nologo
    }
    else {
        # 框架依赖单文件：~0.4MB，需要目标机器有 .NET 8 Desktop Runtime
        Write-Info "框架依赖单文件（需要目标机器装 .NET 8 Desktop Runtime；加 -SelfContained 出免安装版）"
        & dotnet publish $proj -c $config -r win-x64 --self-contained false `
            -p:PublishSingleFile=true `
            -o $StageDir --nologo
    }
    if ($LASTEXITCODE -ne 0) { throw "publish 失败。" }
    Remove-Item (Join-Path $StageDir '*.pdb') -Force -ErrorAction SilentlyContinue
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "找不到 dotnet。需要 .NET SDK 8 或更高（本工程目标 net8.0-windows）。"
}

if ($Clean) {
    Write-Step "清理"
    foreach ($d in 'bin', 'obj') {
        $p = Join-Path $here $d
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force; Write-Info ("删除 " + $d) }
    }
}

$config = if ($Release) { 'Release' } else { 'Debug' }
$version = (& dotnet msbuild $proj -getProperty:Version -nologo 2>$null | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) { $version = '0.0.0' }
$version = $version.Trim()

Write-Step ("构建 (" + $config + " v" + $version + ")")
& dotnet build $proj -c $config --nologo
if ($LASTEXITCODE -ne 0) { throw "构建失败。" }
Write-Ok "构建完成"

$outDir = Join-Path $here ("bin\" + $config + "\net8.0-windows")
# 别把这个变量叫 $exe —— PowerShell 变量名大小写不敏感，
# 它会和脚本参数 [switch]$Exe 变成同一个变量，赋值时直接抛类型转换错误。
$builtExe = Join-Path $outDir 'MVZ2ModManager.exe'
if (-not (Test-Path -LiteralPath $builtExe)) { throw ("构建产物缺失：" + $builtExe) }

if ($SelfTest) {
    Write-Step "自检"
    $report = Join-Path $here '_selftest.txt'
    $args = @('--selftest', $report)
    if ($ApplyRoundtrip) { $args += '--apply-roundtrip' }
    # 这是 GUI 子系统程序：PowerShell 的 & 不会等它，$LASTEXITCODE 会是上一次的残留值。
    $proc = Start-Process -FilePath $builtExe -ArgumentList $args -PassThru -Wait
    Write-Info ("报告：" + $report)
    if ($proc.ExitCode -ne 0) { Write-Host "自检未全部通过。" -ForegroundColor Yellow } else { Write-Ok "自检全部通过" }
}

if ($UiSelfTest) {
    Write-Step "界面自检"
    $report = Join-Path $here '_ui-selftest.txt'
    # 同样必须用 Start-Process -Wait：GUI 子系统 + & 的组合不会等进程。
    $proc = Start-Process -FilePath $builtExe -ArgumentList @('--ui-selftest', $report) -PassThru -Wait
    Write-Info ("报告：" + $report)
    if ($proc.ExitCode -ne 0) { Write-Host "界面自检未全部通过。" -ForegroundColor Yellow } else { Write-Ok "界面自检全部通过" }
}

if ($Screenshot) {
    Write-Step "界面截图"
    $shotDir = Join-Path $here 'screenshots'
    $proc = Start-Process -FilePath $builtExe -ArgumentList @('--screenshot', $shotDir) -PassThru -Wait
    Write-Info ("输出目录：" + $shotDir)
    if ($proc.ExitCode -ne 0) { Write-Host "截图失败。" -ForegroundColor Yellow } else { Write-Ok "截图完成" }
}

if ($Pack -or $Exe) {
    Write-Step "打包"
    $dist = Join-Path (Split-Path $here -Parent) 'dist'
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $base = "MVZ2ModManager_v" + $version + $(if ($SelfContained) { '_standalone' } else { '' })

    $stage = Join-Path $env:TEMP ('mvz2mm_pack_' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    try {
        Publish-OneFile $stage

        if ($Exe) {
            # 裸 exe：把这一个文件拷出去就能双击运行，不需要 README/LICENSE 陪着
            $bare = Join-Path $dist ($base + '.exe')
            Copy-Item (Join-Path $stage 'MVZ2ModManager.exe') $bare -Force
            Write-Ok ("单体 exe：" + $bare + "  (" + [math]::Round((Get-Item $bare).Length / 1MB, 2) + " MB)")
        }

        if ($Pack) {
            # zip 只是把说明与许可和 exe 放在一起，方便分发；exe 本身已经是单文件
            Copy-Item (Join-Path $here 'README.md') $stage -ErrorAction SilentlyContinue
            Copy-Item (Join-Path $here 'LICENSE.txt') $stage -ErrorAction SilentlyContinue

            $zip = Join-Path $dist ($base + '.zip')
            if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
            Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
            Write-Ok ("包体：" + $zip + "  (" + [math]::Round((Get-Item $zip).Length / 1MB, 2) + " MB)")
        }
    }
    finally {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($Run) {
    Write-Step "启动"
    & $builtExe
}

Write-Host ""
Write-Ok "全部完成。"
if (-not $Pack -and -not $Exe -and -not $Run) { Write-Info ("产物：" + $builtExe) }
