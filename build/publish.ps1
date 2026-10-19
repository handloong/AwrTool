# AwrTool 发布脚本
#
# 产物：单文件 + 自包含 + ReadyToRun。目标机器不需要安装 .NET 运行时，
#       也不需要 Oracle 客户端（驱动是 100% 托管的）。
#
#   pwsh build/publish.ps1
#   pwsh build/publish.ps1 -Rid linux-x64

[CmdletBinding()]
param(
    [string]$Rid = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src/AwrTool/AwrTool.csproj'

Write-Host "-> 单文件自包含 ($Rid)" -ForegroundColor Cyan

& dotnet publish $proj -c Release -r $Rid --self-contained true --nologo
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$out = Join-Path $root "src/AwrTool/bin/Release/net8.0/$Rid/publish"
Write-Host ""
Write-Host "发布完成：$out" -ForegroundColor Green
Get-ChildItem $out -File |
    Where-Object { $_.Extension -in @('.exe', '') } |
    Select-Object Name, @{ n = 'SizeMB'; e = { [math]::Round($_.Length / 1MB, 2) } } |
    Format-Table
