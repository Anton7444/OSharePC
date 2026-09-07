param(
    [Parameter(Mandatory = $true)] [string]$GuiRelease,
    [Parameter(Mandatory = $true)] [string]$BackendPublish,
    [Parameter(Mandatory = $true)] [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $GuiRelease '*') -Destination $OutputDirectory -Recurse -Force
$engine = Join-Path $OutputDirectory 'engine'
New-Item -ItemType Directory -Path $engine -Force | Out-Null
Copy-Item -Path (Join-Path $BackendPublish '*') -Destination $engine -Recurse -Force
Get-ChildItem -Path $OutputDirectory -Recurse -Include '*.pdb', '*.log', 'gui_out.txt', 'gui_err.txt' | Remove-Item -Force

foreach ($required in @(
    (Join-Path $OutputDirectory 'catshare_gui.exe'),
    (Join-Path $OutputDirectory 'data\flutter_assets'),
    (Join-Path $OutputDirectory 'engine\CatShareSender.exe')
)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Portable runtime is incomplete; missing $required"
    }
}
