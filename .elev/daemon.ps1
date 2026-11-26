# Elevated job runner: polls .elev\job.ps1, executes it, marks done.
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$job = Join-Path $dir 'job.ps1'
$done = Join-Path $dir 'job.done'
$stop = Join-Path $dir 'stop'
while ($true) {
    if (Test-Path $stop) { Remove-Item $stop -Force; break }
    if (Test-Path $job) {
        try {
            & $job
        } catch { $_ | Out-File (Join-Path $dir 'job.err') }
        Remove-Item $job -Force
        Set-Content $done 'done'
    }
    Start-Sleep -Milliseconds 500
}
