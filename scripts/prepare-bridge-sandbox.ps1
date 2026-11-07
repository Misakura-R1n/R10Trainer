param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$TargetDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z][A-Za-z0-9_-]{2,63}$')][string]$SandboxName,
    [string]$SourceSaveDirectory,
    [string]$PythonPath = 'python',
    [string]$BridgeDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/bridge/full'),
    [string]$BridgeDll = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/bridge-native/R10Bridge.dll')
)
$ErrorActionPreference = 'Stop'
$documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
if (-not $documents) { throw 'Windows MyDocuments folder is unavailable.' }
$arguments = @('-B', "$PSScriptRoot/bridge/prepare_sandbox.py", '--source', $SourceDirectory,
    '--target', $TargetDirectory, '--game-name', $SandboxName, '--save-root', (Join-Path $documents 'AliceSoft'),
    '--bridge-directory', $BridgeDirectory, '--bridge-dll', $BridgeDll)
if ($SourceSaveDirectory) { $arguments += @('--save-source', $SourceSaveDirectory) }
& $PythonPath @arguments
if ($LASTEXITCODE -ne 0) { throw 'Bridge sandbox preparation failed; see the reported paths. No game was launched.' }
