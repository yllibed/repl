param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$script:endpoint = $null
$inspectorVersion = '2.6.0'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$sampleDirectory = Join-Path $repoRoot 'samples/05-hosting-remote'
$assembly = Join-Path $sampleDirectory "bin/$Configuration/net10.0/HostingRemoteSample.dll"
if (-not (Test-Path -LiteralPath $assembly)) {
    throw "Build samples/05-hosting-remote/HostingRemoteSample.csproj -c $Configuration first."
}

function Invoke-Inspector {
    param([string[]] $InspectorArguments)

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = if ($IsWindows) { 'cmd.exe' } else { 'npx' }
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if ($IsWindows) {
        $start.ArgumentList.Add('/d')
        $start.ArgumentList.Add('/c')
        $start.ArgumentList.Add('npx.cmd')
    }
    foreach ($argument in @('--yes', "@modelcontextprotocol/inspector@$inspectorVersion", '--cli',
            $script:endpoint, '--transport', 'http', '--format', 'json', '--stored-auth-only') + $InspectorArguments) {
        $start.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        try {
            $process.WaitForExitAsync().WaitAsync([TimeSpan]::FromMinutes(3)).GetAwaiter().GetResult()
        }
        catch {
            $process.Kill($true)
            $process.WaitForExit(10000) | Out-Null
            throw "Inspector timed out. $($errors.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult())"
        }
        $run = [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = $output.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
            Errors = $errors.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
        }
        return $run
    }
    finally {
        $process.Dispose()
    }
}

function Read-Result {
    param($Run)
    if ($Run.ExitCode -ne 0) {
        throw "Inspector exited $($Run.ExitCode).`n$($Run.Output)`n$($Run.Errors)"
    }
    return ($Run.Output | ConvertFrom-Json).result
}

$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.WorkingDirectory = $sampleDirectory
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.Environment['ASPNETCORE_ENVIRONMENT'] = 'Development'
$start.Environment['Logging__LogLevel__Default'] = 'Information'
$start.ArgumentList.Add($assembly)
$start.ArgumentList.Add('--urls')
$start.ArgumentList.Add('http://127.0.0.1:0')
$server = [Diagnostics.Process]::Start($start)
$serverErrors = $server.StandardError.ReadToEndAsync()
$startupLog = [Text.StringBuilder]::new()
$serverOutput = $null
try {
    $startupDeadline = [DateTime]::UtcNow.AddSeconds(60)
    while (-not $script:endpoint) {
        $remaining = $startupDeadline - [DateTime]::UtcNow
        if ($remaining -le [TimeSpan]::Zero) { throw 'The sample did not start within 60 seconds.' }
        $line = $server.StandardOutput.ReadLineAsync().WaitAsync($remaining).GetAwaiter().GetResult()
        if ($null -eq $line) { throw 'The sample exited before reporting its listening address.' }
        $startupLog.AppendLine($line) | Out-Null
        if ($line -match 'Now listening on: (http://127\.0\.0\.1:\d+)') {
            $script:endpoint = "$($Matches[1])/mcp"
        }
    }
    $serverOutput = $server.StandardOutput.ReadToEndAsync()

    $listed = Read-Result (Invoke-Inspector -InspectorArguments @('--method', 'tools/list', '--strict'))
    $names = @($listed.tools.name | Sort-Object)
    if (($names -join ',') -cne 'settings_show,who') {
        throw "Unexpected public tools: $($names -join ',')"
    }
    $read = Read-Result (Invoke-Inspector -InspectorArguments @('--method', 'tools/call', '--tool-name', 'settings_show',
                '--tool-arg', 'key=maintenance'))
    if ($read.isError -or (($read.content.text -join '') -notmatch 'maintenance = off')) {
        throw "Unexpected tool result: $($read | ConvertTo-Json -Depth 20)"
    }
    $blocked = Invoke-Inspector -InspectorArguments @('--method', 'tools/call', '--tool-name', 'settings_set',
        '--tool-arg', 'key=maintenance', 'value=on')
    if ($blocked.ExitCode -eq 0 -or (($blocked.Output + $blocked.Errors) -notmatch 'settings_set')) {
        throw "The excluded command was not rejected as expected: $($blocked.Output) $($blocked.Errors)"
    }
    Write-Output "MCP Inspector ${inspectorVersion}: HTTP discovery, schema portability, invocation and command filtering passed."
}
catch {
    Write-Host $startupLog.ToString()
    throw
}
finally {
    if (-not $server.HasExited) { $server.Kill($true) }
    $server.WaitForExit(10000) | Out-Null
    if ($serverOutput) { Write-Verbose $serverOutput.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult() }
    $diagnostics = $serverErrors.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
    if ($diagnostics) { Write-Host $diagnostics }
    $server.Dispose()
}
