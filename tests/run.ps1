$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

$testExe = Join-Path ([IO.Path]::GetTempPath()) ("RemotePlayForge.SteamPathsTests." + [Guid]::NewGuid().ToString("N") + ".exe")
try {
    & $csc /nologo /target:exe /platform:x64 "/out:$testExe" /r:System.dll /r:System.Core.dll `
        (Join-Path $root "src\SteamPaths.cs") (Join-Path $root "tests\SteamPathsTests.cs")
    if ($LASTEXITCODE -ne 0) { throw "Test compilation failed (csc exit code $LASTEXITCODE)" }

    & $testExe
    if ($LASTEXITCODE -ne 0) { throw "SteamPaths tests failed (exit code $LASTEXITCODE)" }
}
finally {
    if (Test-Path -LiteralPath $testExe) { Remove-Item -LiteralPath $testExe -Force }
}
