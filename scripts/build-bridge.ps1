param([string]$Toolchain = 'D:\software\mingw64\bin', [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repoRoot 'native/R10Bridge'
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repoRoot 'artifacts/bridge-native' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
& "$Toolchain/dlltool.exe" -m i386 --as "$Toolchain/as.exe" --as-flags '--32' -k -d "$sourceRoot/kernel32.def" -l "$output/kernel32.lib"
if ($LASTEXITCODE -ne 0) { throw 'x86 import library generation failed.' }
& "$Toolchain/gcc.exe" -m32 -std=c11 -Os -Wall -Wextra -Werror -Wno-misleading-indentation -ffreestanding -fno-builtin -fno-stack-protector -c "$sourceRoot/bridge.c" -o "$output/bridge.o"
if ($LASTEXITCODE -ne 0) { throw 'x86 bridge compilation failed.' }
& "$Toolchain/ld.exe" -m i386pe --dll --entry '_DllMain@12' --dynamicbase --nxcompat --no-insert-timestamp "$output/bridge.o" "$output/kernel32.lib" "$sourceRoot/R10Bridge.def" -o "$output/R10Bridge.dll"
if ($LASTEXITCODE -ne 0) { throw 'x86 bridge link failed.' }
Get-FileHash -LiteralPath "$output/R10Bridge.dll" -Algorithm SHA256
