# build.ps1 — compiles RemotePlayForge with the in-box .NET Framework compiler (no SDK required).
# Produces two executables in bin/:
#   RemotePlayForge.exe   the WinForms GUI (from src/*.cs)
#   RPForgeSession.exe    the out-of-process session helper (from helper/RPForgeSession.cs)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root "src"
$out  = Join-Path $root "bin"
New-Item -ItemType Directory -Force $out | Out-Null

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc (in-box .NET Framework 4.x missing)" }

# --- GUI: RemotePlayForge.exe ---
$sources = Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName }
$exe = Join-Path $out "RemotePlayForge.exe"
$cscArgs = @(
    "/nologo", "/target:winexe", "/platform:x64", "/optimize+", "/out:$exe",
    "/r:System.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll", "/r:System.Core.dll"
) + $sources

Write-Host "Compiling GUI ($($sources.Count) files) ..." -ForegroundColor Cyan
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "GUI build FAILED (csc exit code $LASTEXITCODE)" }
Write-Host "Built: $exe" -ForegroundColor Green

# --- Session helper: RPForgeSession.exe ---
# Remote Play Together's group-creation sequence only succeeds in a fresh, dedicated process, so the
# GUI shells out to this helper to host the session.
$helperSrc = Join-Path $root "helper\RPForgeSession.cs"
$helperExe = Join-Path $out "RPForgeSession.exe"
$hArgs = @(
    "/nologo", "/target:exe", "/platform:x64", "/optimize+", "/out:$helperExe",
    "/r:System.dll", "/r:System.Windows.Forms.dll", "/r:System.Core.dll", $helperSrc
)
& $csc @hArgs
if ($LASTEXITCODE -ne 0) { throw "Helper build FAILED (csc exit code $LASTEXITCODE)" }
Write-Host "Built session helper: $helperExe" -ForegroundColor Green

Write-Host ""
Write-Host "Done. Run it with:  `"$exe`"" -ForegroundColor Cyan
