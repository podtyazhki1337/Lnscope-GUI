$ErrorActionPreference = 'Stop'
$localDotnet = Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$runtime = if (Test-Path -LiteralPath $localDotnet) { $localDotnet }
    elseif ($dotnetCommand) { $dotnetCommand.Source }
    else { throw '.NET 10 SDK is required to build LNscope.' }

$taskConfigPath = Join-Path $PSScriptRoot 'artifacts/LNscope/devices.json'
$taskConfigBytes = if (Test-Path -LiteralPath $taskConfigPath) { [IO.File]::ReadAllBytes($taskConfigPath) } else { $null }
try {
    & $runtime publish (Join-Path $PSScriptRoot 'GUI/GUI.csproj') -c Release -r win-x64 --self-contained true `
        -o (Join-Path $PSScriptRoot 'artifacts/LNscope') -m:1 -v:minimal -p:UseSharedCompilation=false -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
}
finally {
    if ($null -ne $taskConfigBytes) { [IO.File]::WriteAllBytes($taskConfigPath, [byte[]]$taskConfigBytes) }
}
Write-Host 'Ready: artifacts/LNscope/GUI.exe. Copy the entire LNscope folder to the target PC.'
