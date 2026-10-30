param([switch]$Check)

$ErrorActionPreference = 'Stop'
if ($Check) {
    & (Join-Path $PSScriptRoot 'GUI/ThirdParty/python/python.exe') -I -X utf8 (Join-Path $PSScriptRoot 'GUI/PythonBackend/check.py')
    if ($LASTEXITCODE -ne 0) { throw 'Python motion checks failed.' }
    & (Join-Path $PSScriptRoot 'GUI/ThirdParty/python/python.exe') -I -X utf8 (Join-Path $PSScriptRoot 'GUI.SmokeTests/python_checks.py')
    if ($LASTEXITCODE -ne 0) { throw 'Python checks failed.' }
}
$localDotnet = Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$runtime = if (Test-Path -LiteralPath $localDotnet) { $localDotnet }
    elseif ($dotnetCommand) { $dotnetCommand.Source }
    else { throw '.NET 10 SDK is required to build LNscope.' }

$project = if ($Check) { 'GUI.SmokeTests/GUI.SmokeTests.csproj' } else { 'GUI/GUI.csproj' }
& $runtime build (Join-Path $PSScriptRoot $project) -m:1 -v:minimal -p:UseSharedCompilation=false -nr:false
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$assembly = if ($Check) { 'GUI.SmokeTests/bin/Debug/net10.0-windows/GUI.SmokeTests.dll' }
    else { 'GUI/bin/Debug/net10.0-windows/GUI.dll' }
& $runtime (Join-Path $PSScriptRoot $assembly)
if ($LASTEXITCODE -ne 0) { throw 'LNscope exited with an error.' }
