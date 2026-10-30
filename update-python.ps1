param([string]$Destination = (Join-Path $PSScriptRoot 'artifacts/LNscope'))
$ErrorActionPreference = 'Stop'
$taskDestination = [IO.Path]::GetFullPath($Destination)
if (-not (Test-Path -LiteralPath (Join-Path $taskDestination 'GUI.exe'))) { throw 'Destination must be an existing LNscope application folder.' }
$taskRunning = Get-Process GUI -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $taskDestination 'GUI.exe') }
if ($taskRunning) { throw 'Close the destination GUI.exe before updating Python.' }
& (Join-Path $PSScriptRoot 'GUI/ThirdParty/python/python.exe') -I -X utf8 (Join-Path $PSScriptRoot 'GUI/PythonBackend/check.py')
if ($LASTEXITCODE -ne 0) { throw 'Python checks failed; destination was not modified.' }
$taskBackup = Join-Path $taskDestination ('PythonBackend-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
if (Test-Path -LiteralPath (Join-Path $taskDestination 'PythonBackend')) {
    Copy-Item -LiteralPath (Join-Path $taskDestination 'PythonBackend') -Destination $taskBackup -Recurse
}
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'GUI/PythonBackend') -File -Recurse |
    Where-Object { $_.Extension -in '.py', '.md' } | ForEach-Object {
        $taskRelative = [IO.Path]::GetRelativePath((Join-Path $PSScriptRoot 'GUI/PythonBackend'), $_.FullName)
        $taskTarget = Join-Path (Join-Path $taskDestination 'PythonBackend') $taskRelative
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($taskTarget)) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $taskTarget -Force
    }
Write-Host "Python updated without rebuilding C#. Previous files: $taskBackup"
