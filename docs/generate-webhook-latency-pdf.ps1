param(
    [ValidateSet('Both', 'Path', 'Latency', 'VotingFlow', 'VotingFlowGeo')]
    [string]$Document = 'Both'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$docsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$edgePath = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
if (-not (Test-Path $edgePath)) {
    throw "Microsoft Edge was not found at $edgePath"
}

$allTargets = @{
    Path = @{
        Html = Join-Path $docsDir 'fbserviceext-webhook-path-diagram.html'
        Pdf  = Join-Path $docsDir 'fbserviceext-webhook-path-diagram.pdf'
    }
    Latency = @{
        Html = Join-Path $docsDir 'fbserviceext-webhook-latency-breakdown.html'
        Pdf  = Join-Path $docsDir 'fbserviceext-webhook-latency-breakdown.pdf'
    }
    VotingFlow = @{
        Html = Join-Path $docsDir 'fbserviceext-voting-flow-diagram.html'
        Pdf  = Join-Path $docsDir 'fbserviceext-voting-flow-diagram.pdf'
    }
    VotingFlowGeo = @{
        Html = Join-Path $docsDir 'fbserviceext-voting-flow-diagram-geo.html'
        Pdf  = Join-Path $docsDir 'fbserviceext-voting-flow-diagram-geo.pdf'
    }
}

$targets = switch ($Document) {
    'Path'          { @($allTargets.Path) }
    'Latency'       { @($allTargets.Latency) }
    'VotingFlow'    { @($allTargets.VotingFlow) }
    'VotingFlowGeo' { @($allTargets.VotingFlowGeo) }
    default         { @($allTargets.Path, $allTargets.Latency, $allTargets.VotingFlow, $allTargets.VotingFlowGeo) }
}

foreach ($target in $targets) {
    if (-not (Test-Path $target.Html)) {
        throw "HTML document not found: $($target.Html)"
    }

    $runId = [guid]::NewGuid().ToString('N')
    $profileDir = Join-Path $docsDir ".edge-headless-profile-$runId"
    $tempPdf = Join-Path $docsDir ("$([IO.Path]::GetFileNameWithoutExtension($target.Pdf)).tmp.$runId.pdf")
    $uri = [Uri]::new($target.Html).AbsoluteUri

    try {
        & $edgePath --headless=new --disable-gpu --no-first-run --user-data-dir="$profileDir" --print-to-pdf-no-header --print-to-pdf="$tempPdf" $uri | Out-Null
        if (-not (Test-Path $tempPdf)) {
            throw "PDF was not created: $tempPdf"
        }

        Move-Item -Force $tempPdf $target.Pdf
        Get-Item $target.Pdf | Select-Object Name, Length, LastWriteTime
    }
    finally {
        if (Test-Path $tempPdf) {
            Remove-Item -Force $tempPdf -ErrorAction SilentlyContinue
        }
        if (Test-Path $profileDir) {
            Remove-Item -Recurse -Force $profileDir -ErrorAction SilentlyContinue
        }
    }
}
