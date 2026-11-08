param(
    [string]$DotNetPath = 'dotnet',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$Publish,
    [switch]$DeveloperTools,
    [switch]$Bridge,
    [switch]$Controls,
    [string]$SourceAin,
    [string]$PythonPath = 'python',
    [string]$AlicePath,
    [string]$BridgeToolchain = 'D:\software\mingw64\bin'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    if (($Bridge -or $Controls) -and (-not $SourceAin -or -not (Test-Path -LiteralPath $SourceAin -PathType Leaf))) {
        throw '-Bridge/-Controls requires -SourceAin pointing to the original supported Rance10.ain.'
    }
    & $DotNetPath build R10Trainer.slnx -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $DotNetPath run --project tests/R10.Tests -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Core verification failed.' }
    & $DotNetPath run --project tests/R10.Bridge.Tests -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Bridge client/runtime/MCP verification failed.' }
    & $DotNetPath run --project tests/R10.Install.Tests -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'HP component installation verification failed.' }
    if ($Bridge) {
        & "$PSScriptRoot/build-bridge.ps1" -Toolchain $BridgeToolchain
        & $DotNetPath publish tests/R10.NativeBridge.Tests -c $Configuration -r win-x86 --self-contained true -o artifacts/bridge-native-test --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Native bridge helper publish failed.' }
        & "$repoRoot/artifacts/bridge-native-test/R10.NativeBridge.Tests.exe" "$repoRoot/artifacts/bridge-native/R10Bridge.dll"
        if ($LASTEXITCODE -ne 0) { throw 'Native bridge helper verification failed.' }
        & $PythonPath -B scripts/bridge/test_build_instrumented_ain.py --source $SourceAin
        if ($LASTEXITCODE -ne 0) { throw 'AIN offline verification failed.' }
        & $PythonPath -B scripts/bridge/test_cards_ain.py --source $SourceAin
        if ($LASTEXITCODE -ne 0) { throw 'Card filter verification failed.' }
        & $PythonPath -B scripts/bridge/test_feature_ain.py --source $SourceAin
        if ($LASTEXITCODE -ne 0) { throw 'Component feature branch verification failed.' }
        & $PythonPath -B scripts/bridge/test_prepare_sandbox.py
        if ($LASTEXITCODE -ne 0) { throw 'Sandbox preparation verification failed.' }
        $ainArguments = @('-B', 'scripts/bridge/build_instrumented_ain.py', '--source', $SourceAin, '--profile', 'full', '--output-dir', 'artifacts/bridge/full')
        if ($AlicePath) { $ainArguments += @('--alice', $AlicePath) }
        & $PythonPath @ainArguments
        if ($LASTEXITCODE -ne 0) { throw 'AIN bridge construction failed.' }
        & $PythonPath -B scripts/bridge/export_portable_patch.py --source $SourceAin --dll artifacts/bridge-native/R10Bridge.dll --output-dir artifacts/bridge/portable
        if ($LASTEXITCODE -ne 0) { throw 'Portable HP patch export failed.' }
        & $DotNetPath run --project tests/R10.Tests -c $Configuration --no-build -- --bridge-ain artifacts/bridge/full/Rance10.ain
        if ($LASTEXITCODE -ne 0) { throw 'Instrumented AIN compatibility verification failed.' }
        & $DotNetPath run --project tests/R10.Install.Tests -c $Configuration --no-build -- --source-ain $SourceAin --patch artifacts/bridge/portable/hp-patch.json --dll artifacts/bridge-native/R10Bridge.dll
        if ($LASTEXITCODE -ne 0) { throw 'Portable HP real-file roundtrip verification failed.' }
    }
    if ($Controls) {
        & "$PSScriptRoot/build-bridge.ps1" -Toolchain $BridgeToolchain -OutputDirectory "$repoRoot/artifacts/bridge-controls-native"
        & $DotNetPath publish tests/R10.NativeBridge.Tests -c $Configuration -r win-x86 --self-contained true -o artifacts/native-controls-tests --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Native controls helper build failed.' }
        & "$repoRoot/artifacts/native-controls-tests/R10.NativeBridge.Tests.exe" "$repoRoot/artifacts/bridge-controls-native/R10Bridge.dll"
        if ($LASTEXITCODE -ne 0) { throw 'Native controls verification failed.' }
        $controlArguments = @('-B', 'scripts/bridge/build_instrumented_ain.py', '--source', $SourceAin, '--profile', 'controls', '--output-dir', 'artifacts/bridge/controls')
        if ($AlicePath) { $controlArguments += @('--alice', $AlicePath) }
        & $PythonPath @controlArguments
        if ($LASTEXITCODE -ne 0) { throw 'Controls AIN construction failed.' }
        & $PythonPath -B scripts/bridge/test_build_instrumented_ain.py --source $SourceAin
        if ($LASTEXITCODE -ne 0) { throw 'Controls AIN verification failed.' }
    }
    if ($DeveloperTools -or $Controls) {
        $mcpDirectory = if ($Controls) { 'artifacts/R10Developer-controls/mcp' } else { 'artifacts/R10Developer/mcp' }
        & $DotNetPath publish src/R10.Mcp -c $Configuration -r win-x86 --self-contained true -o $mcpDirectory --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Developer MCP publish failed.' }
        & "$PSScriptRoot/smoke-mcp.ps1" -Executable "$repoRoot/$mcpDirectory/R10.Mcp.exe"
    }
    if ($Publish) {
        if (-not (Test-Path -LiteralPath artifacts/bridge/portable/hp-patch.json) -or -not (Test-Path -LiteralPath artifacts/bridge-native/R10Bridge.dll)) {
            throw 'The user package requires the HP component. Build with -Bridge -SourceAin first.'
        }
        $portableIdentity = Get-Content -LiteralPath artifacts/bridge/portable/hp-patch.json -Raw | ConvertFrom-Json
        $bridgeDllHash = (Get-FileHash -LiteralPath artifacts/bridge-native/R10Bridge.dll -Algorithm SHA256).Hash
        if ($portableIdentity.dllSha256 -ne $bridgeDllHash) { throw 'HP patch and native DLL identities differ; rebuild with -Bridge.' }
        $appProject = [xml](Get-Content -LiteralPath src/R10.App/R10.App.csproj -Raw)
        $version = [string]$appProject.Project.PropertyGroup.Version
        if ($version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Invalid package version.' }
        $publishDirectory = Join-Path $repoRoot "artifacts/R10Trainer-v$version-win-x86"
        # Build the distributable in its own versioned directory. MCP stays in R10Developer.
        if (Test-Path -LiteralPath (Join-Path $publishDirectory 'mcp')) { throw 'User package contains a developer MCP folder; choose a clean output directory.' }
        & $DotNetPath publish tests/R10.Tests -c $Configuration -r win-x86 --self-contained true -o artifacts/verification --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Test publish failed.' }
        & "$repoRoot/artifacts/verification/R10.Tests.exe" --native
        if ($LASTEXITCODE -ne 0) { throw 'Native verification failed.' }
        & $DotNetPath publish src/R10.App -c $Configuration -r win-x86 --self-contained true -o $publishDirectory --nologo
        if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
        $bridgePackage = Join-Path $publishDirectory 'bridge'
        New-Item -ItemType Directory -Path $bridgePackage -Force | Out-Null
        Copy-Item -LiteralPath artifacts/bridge-native/R10Bridge.dll -Destination "$bridgePackage/R10Bridge.dll" -Force
        Copy-Item -LiteralPath artifacts/bridge/portable/hp-patch.json -Destination "$bridgePackage/hp-patch.json" -Force
        $bridgeIdentity = [ordered]@{ dllSha256=$bridgeDllHash; patchSha256=(Get-FileHash -LiteralPath "$bridgePackage/hp-patch.json" -Algorithm SHA256).Hash; ainPlainSha256=$portableIdentity.targetPlainSha256; originalAinSha256=$portableIdentity.sourceSha256 }
        $assets = Get-Content -LiteralPath src/R10.App/obj/project.assets.json -Raw | ConvertFrom-Json
        $runtimeConfig = Get-Content -LiteralPath "$publishDirectory/R10Trainer.runtimeconfig.json" -Raw | ConvertFrom-Json
        New-Item -ItemType Directory -Path "$publishDirectory/licenses" -Force | Out-Null
        foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
            $packageId = $framework.name.ToLowerInvariant() + '.runtime.win-x86'
            $packageDirectory = $null
            foreach ($cache in $assets.packageFolders.PSObject.Properties.Name) {
                $candidate = Join-Path $cache "$packageId/$($framework.version)"
                if (Test-Path -LiteralPath $candidate) { $packageDirectory = $candidate; break }
            }
            if (-not $packageDirectory) { throw "Runtime license source missing: $packageId" }
            Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object { $_.Name -match '^(LICENSE.*|THIRD-PARTY-NOTICES.*)$' } | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination "$publishDirectory/licenses/$packageId-$($_.Name)" -Force
            }
        }
        $head = & git rev-parse --verify -q HEAD
        if (-not $head) { $head = 'uncommitted' }
        $sourceFiles = @(& git ls-files --cached --others --exclude-standard) | Sort-Object -Unique
        $sourceHashes = [ordered]@{}
        foreach ($relative in $sourceFiles) {
            $full = Join-Path $repoRoot $relative
            if (Test-Path -LiteralPath $full -PathType Leaf) { $sourceHashes[$relative] = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash }
        }
        $metadata = [ordered]@{ version=$version; builtAtUtc=[DateTime]::UtcNow.ToString('o'); commit=$head; dirty=[bool](& git status --porcelain); rid='win-x86'; bridgeIncluded=$true; bridge=$bridgeIdentity; mcpIncluded=$false; gameProfile='Chinese BugFix'; validation='automated + independent x86 helper; this script does not run real-game tests; see README for separately recorded live checks'; sources=$sourceHashes }
        $metadata | ConvertTo-Json | Set-Content -LiteralPath "$publishDirectory/build-info.json" -Encoding utf8
        Copy-Item -LiteralPath README.md -Destination "$publishDirectory/README.md" -Force
        $gameFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File | Where-Object { $_.Extension -in @('.ain', '.afa', '.asd', '.vsf', '.qnt') -or $_.Name -in @('Rance10.exe', 'AliceStart.ini', 'R10.Mcp.exe', 'R10.Mcp.dll', 'settings.json') })
        if ($gameFiles.Count -ne 0) { throw 'Publish directory contains game data or saves; refusing to create a package.' }
        Compress-Archive -LiteralPath $publishDirectory -DestinationPath "$publishDirectory.zip" -Force
    }
}
finally { Pop-Location }
