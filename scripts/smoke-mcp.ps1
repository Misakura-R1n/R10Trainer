param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = 'Stop'
$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = [System.IO.Path]::GetFullPath($Executable)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
# initialize/tools/list are local protocol operations; this helper never opens a game pipe.
$start.Arguments = '--pid 2147483647'
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $start
$started = $false
try {
    $started = $process.Start()
    if (-not $started) { throw 'Could not start the published MCP executable.' }
    $outputTask = $process.StandardOutput.ReadToEndAsync()
    $errorTask = $process.StandardError.ReadToEndAsync()
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"package-check","version":"1"}}}')
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}')
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(15000)) { $process.Kill(); throw 'Published MCP smoke check timed out.' }
    $output = $outputTask.GetAwaiter().GetResult()
    $stderr = $errorTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw "Published MCP exited with $($process.ExitCode): $stderr" }
    $responses = @($output -split '\r?\n' | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    if ($responses.Count -ne 2 -or $responses[0].id -ne 1 -or -not $responses[0].result.serverInfo -or
        $responses[1].id -ne 2 -or @($responses[1].result.tools).Count -eq 0) {
        throw 'Published MCP initialize/tools/list response was invalid.'
    }
    Write-Host 'PASS published MCP initialize/tools/list (protocol only; no game connection)'
}
finally {
    if ($started -and -not $process.HasExited) { $process.Kill() }
    $process.Dispose()
}
