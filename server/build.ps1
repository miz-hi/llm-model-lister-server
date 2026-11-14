# Model Lister Server build script
# Uses the C# compiler bundled with Windows (.NET Framework 4.x). No install required.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

# --- embed www\index.html into a generated C# source file ---
$htmlPath = Join-Path $PSScriptRoot 'www\index.html'
if (-not (Test-Path $htmlPath)) { throw "www\index.html not found" }
$html = [IO.File]::ReadAllText($htmlPath, [Text.Encoding]::UTF8)
$esc = $html.Replace('\', '\\').Replace('"', '\"').Replace("`r`n", '\n').Replace("`n", '\n').Replace("`r", '\n').Replace("`t", '\t')
$cs = "internal static class Embedded { internal const string IndexHtml = `"$esc`"; }"
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'index_html.cs'), $cs, [Text.Encoding]::UTF8)

# --- compile ---
$out = Join-Path $PSScriptRoot 'model-lister-server.exe'
& $csc /nologo /optimize /target:exe /out:"$out" /r:System.dll /r:System.Core.dll `
    /r:System.Web.Extensions.dll `
    (Join-Path $PSScriptRoot 'server.cs') (Join-Path $PSScriptRoot 'index_html.cs')
if ($LASTEXITCODE -ne 0) { throw "compile failed" }

Remove-Item (Join-Path $PSScriptRoot 'index_html.cs') -ErrorAction SilentlyContinue
Write-Host "OK: built $out" -ForegroundColor Green
Write-Host "run: .\model-lister-server.exe --port 8787"
