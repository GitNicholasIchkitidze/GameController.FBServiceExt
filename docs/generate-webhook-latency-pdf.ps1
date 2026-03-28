param(
    [string]$OutputPath = "E:\GAME SHOW Project\GameShowControlSolution\GameController.FBServiceExtSolution\docs\fbserviceext-webhook-latency-breakdown.pdf"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Escape-PdfText([string]$Text) {
    return $Text.Replace('\', '\\').Replace('(', '\(').Replace(')', '\)')
}

function Add-TextLine {
    param(
        [System.Collections.Generic.List[string]]$Ops,
        [double]$X,
        [double]$Y,
        [double]$Size,
        [string]$Font,
        [double[]]$Color,
        [string]$Text
    )

    $escaped = Escape-PdfText $Text
    $Ops.Add('BT')
    $Ops.Add(('/{0} {1} Tf' -f $Font, ([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.##}', $Size))))
    $Ops.Add(([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.###} {1:0.###} {2:0.###} rg', $Color[0], $Color[1], $Color[2])))
    $Ops.Add(([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '1 0 0 1 {0:0.##} {1:0.##} Tm', $X, $Y)))
    $Ops.Add(('({0}) Tj' -f $escaped))
    $Ops.Add('ET')
}

function Add-Rect {
    param(
        [System.Collections.Generic.List[string]]$Ops,
        [double]$X,
        [double]$Y,
        [double]$Width,
        [double]$Height,
        [double[]]$Fill,
        [double[]]$Stroke,
        [double]$LineWidth = 1.2
    )

    $Ops.Add(([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.###} {1:0.###} {2:0.###} rg', $Fill[0], $Fill[1], $Fill[2])))
    $Ops.Add(([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.###} {1:0.###} {2:0.###} RG', $Stroke[0], $Stroke[1], $Stroke[2])))
    $Ops.Add(([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.##} w', $LineWidth)))
    $Ops.Add(([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.##} {1:0.##} {2:0.##} {3:0.##} re B', $X, $Y, $Width, $Height)))
}

function Add-Line {
    param(
        [System.Collections.Generic.List[string]]$Ops,
        [double]$X1,
        [double]$Y1,
        [double]$X2,
        [double]$Y2,
        [double[]]$Color,
        [double]$LineWidth = 3
    )

    $Ops.Add(([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.###} {1:0.###} {2:0.###} RG', $Color[0], $Color[1], $Color[2])))
    $Ops.Add(([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.##} w', $LineWidth)))
    $Ops.Add(([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.##} {1:0.##} m {2:0.##} {3:0.##} l S', $X1, $Y1, $X2, $Y2)))
}

function Add-ArrowHead {
    param(
        [System.Collections.Generic.List[string]]$Ops,
        [double]$X,
        [double]$Y,
        [double[]]$Color
    )

    $Ops.Add(([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.###} {1:0.###} {2:0.###} rg', $Color[0], $Color[1], $Color[2])))
    $Ops.Add(([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.##} {1:0.##} m {2:0.##} {3:0.##} l {4:0.##} {5:0.##} l f', $X, $Y, $X - 10, $Y + 5, $X - 10, $Y - 5)))
}

$pageWidth = 1520
$pageHeight = 840
$ops = [System.Collections.Generic.List[string]]::new()

$bg = @(0.051, 0.067, 0.090)
$panel = @(0.063, 0.094, 0.129)
$border = @(0.169, 0.212, 0.278)
$text = @(0.902, 0.929, 0.953)
$muted = @(0.851, 0.890, 0.933)
$low = @(0.180, 0.780, 0.631)
$med = @(1.000, 0.706, 0.329)
$high = @(1.000, 0.420, 0.420)
$arrow = @(0.486, 0.780, 1.000)
$warnFill = @(0.208, 0.153, 0.078)
$greenFill = @(0.078, 0.192, 0.153)

Add-Rect -Ops $ops -X 0 -Y 0 -Width $pageWidth -Height $pageHeight -Fill $bg -Stroke $bg -LineWidth 0
Add-TextLine -Ops $ops -X 36 -Y 798 -Size 30 -Font 'F2' -Color $text -Text 'FBServiceExt Webhook ACK Latency Breakdown'
Add-TextLine -Ops $ops -X 36 -Y 772 -Size 15 -Font 'F1' -Color @(0.624,0.690,0.780) -Text 'Horizontal view of the awaited path before 200 OK. Each block includes the primary source file and line range.'

Add-Rect -Ops $ops -X 36 -Y 726 -Width 208 -Height 30 -Fill @(0.078,0.106,0.141) -Stroke $border
Add-TextLine -Ops $ops -X 48 -Y 736 -Size 13 -Font 'F1' -Color $text -Text 'Low risk / usually cheap'
$ops.Add('0.180 0.780 0.631 rg 36 736 8 8 re f')
Add-Rect -Ops $ops -X 258 -Y 726 -Width 254 -Height 30 -Fill @(0.078,0.106,0.141) -Stroke $border
Add-TextLine -Ops $ops -X 270 -Y 736 -Size 13 -Font 'F1' -Color $text -Text 'Medium risk / environment dependent'
$ops.Add('1.000 0.706 0.329 rg 258 736 8 8 re f')
Add-Rect -Ops $ops -X 526 -Y 726 -Width 240 -Height 30 -Fill @(0.078,0.106,0.141) -Stroke $border
Add-TextLine -Ops $ops -X 538 -Y 736 -Size 13 -Font 'F1' -Color $text -Text 'High risk / can dominate p95-p99'
$ops.Add('1.000 0.420 0.420 rg 526 736 8 8 re f')

Add-TextLine -Ops $ops -X 48 -Y 684 -Size 23 -Font 'F2' -Color $text -Text 'ACK Path (everything below happens before HTTP 200)'
Add-Rect -Ops $ops -X 24 -Y 392 -Width 1470 -Height 276 -Fill $panel -Stroke $border

$boxes = @(
    @{ X = 42;  Y = 444; W = 215; H = 190; Fill = $greenFill; Stroke = $low; Title = '1. Body Read'; Risk = 'Latency Risk: LOW'; Lines = @('Request.Body -> string','Mostly payload-size bound','Current payloads are small','Usually cheap unless body stalls'); Ref = @('Controllers/FacebookWebhooksController.cs','Receive(): L74-L76') },
    @{ X = 281; Y = 444; W = 215; H = 190; Fill = $greenFill; Stroke = $low; Title = '2. Signature Check'; Risk = 'Latency Risk: LOW'; Lines = @('X-Hub-Signature-256','Pure CPU work','No external I/O','Should stay stable under load'); Ref = @('Controllers/FacebookWebhooksController.cs','Receive(): L78-L89') },
    @{ X = 520; Y = 444; W = 215; H = 190; Fill = $greenFill; Stroke = $low; Title = '3. Envelope Create'; Risk = 'Latency Risk: LOW'; Lines = @('AcceptWebhookCommand ->','RawWebhookEnvelope','Object allocation only','Negligible compared to I/O'); Ref = @('Application/Services/WebhookIngressService.cs','AcceptAsync(): L32-L39') },
    @{ X = 759; Y = 428; W = 350; H = 222; Fill = $warnFill; Stroke = $med; Title = '4. RabbitMQ Publish'; Risk = 'Latency Risk: MEDIUM to HIGH'; Lines = @('Ensure connection/channel + BasicPublish','Dominant ACK-path risk in normal operation','Sensitive to broker/network/warm-up/backpressure','Earlier cold-start spikes came from this area'); Ref = @('Infrastructure/Messaging/RabbitMqRawIngressPublisher.cs','PublishAsync(): L29-L55','EnsureChannelAsync(): L99-L119') },
    @{ X = 1133; Y = 444; W = 175; H = 190; Fill = $greenFill; Stroke = $low; Title = '5. return Ok()'; Risk = 'Latency Risk: NONE'; Lines = @('HTTP 200 returned','Only after all awaited steps finish','',''); Ref = @('Controllers/FacebookWebhooksController.cs','Receive(): L114-L115') }
)

foreach ($box in $boxes) {
    Add-Rect -Ops $ops -X $box.X -Y $box.Y -Width $box.W -Height $box.H -Fill $box.Fill -Stroke $box.Stroke -LineWidth 2
    $cx = $box.X + 14
    $right = $box.X + $box.W - 14
    Add-TextLine -Ops $ops -X $cx -Y ($box.Y + $box.H - 28) -Size 18 -Font 'F2' -Color $text -Text $box.Title
    Add-TextLine -Ops $ops -X $cx -Y ($box.Y + $box.H - 52) -Size 11.5 -Font 'F1' -Color $muted -Text $box.Lines[0]
    Add-TextLine -Ops $ops -X $cx -Y ($box.Y + $box.H - 76) -Size 12 -Font 'F2' -Color ($(if ($box.Risk -like '*MEDIUM*') { $med } else { $low })) -Text $box.Risk
    if ($box.Lines[1]) { Add-TextLine -Ops $ops -X $cx -Y ($box.Y + $box.H - 100) -Size 10.5 -Font 'F1' -Color $muted -Text $box.Lines[1] }
    if ($box.Lines[2]) { Add-TextLine -Ops $ops -X $cx -Y ($box.Y + $box.H - 118) -Size 10.5 -Font 'F1' -Color $muted -Text $box.Lines[2] }
    if ($box.Lines[3]) { Add-TextLine -Ops $ops -X $cx -Y ($box.Y + $box.H - 136) -Size 10.5 -Font 'F1' -Color $muted -Text $box.Lines[3] }
    Add-TextLine -Ops $ops -X $cx -Y ($box.Y + 42) -Size 10 -Font 'F2' -Color $text -Text 'Code Ref'
    Add-TextLine -Ops $ops -X $cx -Y ($box.Y + 24) -Size 9.2 -Font 'F1' -Color $muted -Text $box.Ref[0]
    Add-TextLine -Ops $ops -X $cx -Y ($box.Y + 10) -Size 9.2 -Font 'F1' -Color $muted -Text $box.Ref[1]
    if ($box.Ref.Count -gt 2) {
        Add-TextLine -Ops $ops -X $cx -Y ($box.Y - 4) -Size 9.2 -Font 'F1' -Color $muted -Text $box.Ref[2]
    }
}

Add-Line -Ops $ops -X1 257 -Y1 538 -X2 281 -Y2 538 -Color $arrow
Add-ArrowHead -Ops $ops -X 281 -Y 538 -Color $arrow
Add-Line -Ops $ops -X1 496 -Y1 538 -X2 520 -Y2 538 -Color $arrow
Add-ArrowHead -Ops $ops -X 520 -Y 538 -Color $arrow
Add-Line -Ops $ops -X1 735 -Y1 538 -X2 759 -Y2 538 -Color $arrow
Add-ArrowHead -Ops $ops -X 759 -Y 538 -Color $arrow
Add-Line -Ops $ops -X1 1109 -Y1 538 -X2 1133 -Y2 538 -Color $arrow
Add-ArrowHead -Ops $ops -X 1133 -Y 538 -Color $arrow

Add-Rect -Ops $ops -X 48 -Y 108 -Width 1440 -Height 240 -Fill $panel -Stroke $border
Add-TextLine -Ops $ops -X 72 -Y 314 -Size 24 -Font 'F2' -Color $text -Text 'What this means in practice'
Add-TextLine -Ops $ops -X 88 -Y 274 -Size 18 -Font 'F2' -Color $low -Text 'Good'
Add-TextLine -Ops $ops -X 160 -Y 274 -Size 16 -Font 'F1' -Color $muted -Text '~100-300 ms ACK is healthy for this architecture.'
Add-TextLine -Ops $ops -X 88 -Y 238 -Size 18 -Font 'F2' -Color $med -Text 'Watch'
Add-TextLine -Ops $ops -X 160 -Y 238 -Size 16 -Font 'F1' -Color $muted -Text '~500 ms to 1 s deserves attention, especially if p95 or p99 drifts there.'
Add-TextLine -Ops $ops -X 88 -Y 202 -Size 18 -Font 'F2' -Color $high -Text 'Bad'
Add-TextLine -Ops $ops -X 160 -Y 202 -Size 16 -Font 'F1' -Color $muted -Text 'Multi-second ACKs are dangerous because retries and duplicate deliveries become likely.'
Add-TextLine -Ops $ops -X 88 -Y 164 -Size 16 -Font 'F1' -Color $muted -Text 'Current example: 127.4902 ms is in the good zone.'
Add-TextLine -Ops $ops -X 88 -Y 138 -Size 16 -Font 'F1' -Color $muted -Text 'Primary performance focus remains step 4, because it is the only awaited network hop before 200 OK.'

Add-Rect -Ops $ops -X 48 -Y 28 -Width 708 -Height 60 -Fill @(0.070,0.098,0.133) -Stroke $border
Add-Rect -Ops $ops -X 780 -Y 28 -Width 708 -Height 60 -Fill @(0.070,0.098,0.133) -Stroke $border
Add-TextLine -Ops $ops -X 66 -Y 62 -Size 13 -Font 'F2' -Color $text -Text 'Why await matters:'
Add-TextLine -Ops $ops -X 66 -Y 42 -Size 12 -Font 'F1' -Color $muted -Text 'await AcceptAsync(...); return Ok(); means the response is sent only after publish completes.'
Add-TextLine -Ops $ops -X 798 -Y 62 -Size 13 -Font 'F2' -Color $text -Text 'How to use the refs:'
Add-TextLine -Ops $ops -X 798 -Y 42 -Size 12 -Font 'F1' -Color $muted -Text 'Each box points at the primary code location responsible for that latency segment.'

$content = ($ops -join "`n")
$contentBytes = [System.Text.Encoding]::ASCII.GetBytes($content)

$objects = [System.Collections.Generic.List[string]]::new()
$objects.Add("<< /Type /Catalog /Pages 2 0 R >>")
$objects.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
$objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1520 840] /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> /Contents 4 0 R >>")
$objects.Add(("<< /Length {0} >>`nstream`n{1}`nendstream" -f $contentBytes.Length, $content))
$objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
$objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>")

$builder = New-Object System.Text.StringBuilder
[void]$builder.Append("%PDF-1.4`n")
$offsets = New-Object System.Collections.Generic.List[int]

for ($i = 0; $i -lt $objects.Count; $i++) {
    $offsets.Add($builder.Length)
    [void]$builder.AppendFormat([System.Globalization.CultureInfo]::InvariantCulture, "{0} 0 obj`n{1}`nendobj`n", $i + 1, $objects[$i])
}

$xrefOffset = $builder.Length
[void]$builder.Append("xref`n0 ")
[void]$builder.Append($objects.Count + 1)
[void]$builder.Append("`n0000000000 65535 f `n")
foreach ($offset in $offsets) {
    [void]$builder.AppendFormat([System.Globalization.CultureInfo]::InvariantCulture, "{0:0000000000} 00000 n `n", $offset)
}
[void]$builder.AppendFormat([System.Globalization.CultureInfo]::InvariantCulture, "trailer`n<< /Size {0} /Root 1 0 R >>`nstartxref`n{1}`n%%EOF", $objects.Count + 1, $xrefOffset)

$dir = Split-Path -Parent $OutputPath
if (-not (Test-Path $dir)) {
    New-Item -ItemType Directory -Path $dir | Out-Null
}
[System.IO.File]::WriteAllText($OutputPath, $builder.ToString(), [System.Text.Encoding]::ASCII)
Get-Item $OutputPath | Select-Object Name,Length,LastWriteTime
