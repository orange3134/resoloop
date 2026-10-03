param(
    [Parameter(Mandatory)][string]$Url,
    [switch]$Probe,
    [string]$State = '.resoloop/state/inertial-marker.json',
    [string]$Report = 'artifacts/inertial-marker-study/motion.json'
)
$ErrorActionPreference = 'Stop'
if (-not $Probe) { throw 'Use -Probe explicitly, with nobody operating this example during the check.' }
if (Test-Path -LiteralPath $Report) { throw 'Choose a new report path; reports are not overwritten.' }
function Invoke-MarkerCli([string[]]$CliArgs) {
    $raw = & resoloop @CliArgs --url $Url --json
    $code = $LASTEXITCODE
    $result = $raw | ConvertFrom-Json
    if ($code -ne 0 -or -not $result.ok) { throw ($raw -join "`n") }
    return $result.data
}
function Read-Marker {
    $r = Invoke-MarkerCli @('observe', '$member:running.Value', '$member:uix-slider--force-slider.Value', '$member:position-output.Value', '$member:velocity.Value', '$member:run-label-text.Content', '--state', $State)
    return [pscustomobject]@{
        running = $r.values.'$member:running.Value'.member.value
        acceleration = $r.values.'$member:uix-slider--force-slider.Value'.member.value
        position = $r.values.'$member:position-output.Value'.member.value
        velocity = $r.values.'$member:velocity.Value'.member.value
        label = $r.values.'$member:run-label-text.Content'.member.value
    }
}
function Set-Marker([string]$Component, [string]$Value) {
    $null = Invoke-MarkerCli @('component', 'set', $Component, 'Value', $Value, '--state', $State)
}
$root = Invoke-MarkerCli @('inspect', '$slot:root', '--state', $State, '--depth', '0')
if ($root.name -ne 'ResoLoop_Example_InertialMarker' -or $root.parentId -ne 'Root') { throw 'Expected owned example was not resolved.' }
$before = Read-Marker
if ($before.running) { throw 'Stop the marker before running this probe; local motion history cannot be restored.' }
$checks = [Collections.Generic.List[object]]::new()
$samples = [Collections.Generic.List[object]]::new()
function Assert-Marker([string]$Name, [bool]$Passed, $Sample) {
    $checks.Add([pscustomobject]@{name=$Name; passed=$Passed; sample=$Sample})
    if (-not $Passed) { throw "Failed: $Name" }
}
$failure = $null
$restored = $false
try {
    # Keep the coast phase away from the ends despite separate CLI connections.
    Set-Marker '$component:uix-slider--force-slider' '0.04'
    Set-Marker '$component:running' 'true'
    $moving = Read-Marker
    Assert-Marker 'Positive acceleration produces positive velocity' ($moving.velocity -gt 0 -and $moving.position.x -gt 0 -and $moving.label -eq 'STOP / RESET') $moving
    Set-Marker '$component:uix-slider--force-slider' '0'
    $coasting = Read-Marker
    Assert-Marker 'Zero acceleration preserves inertia' ($coasting.velocity -gt 0) $coasting
    Start-Sleep -Milliseconds 400
    $later = Read-Marker
    Assert-Marker 'Unforced speed decays' ([Math]::Abs($later.velocity) -lt [Math]::Abs($coasting.velocity)) $later
    Set-Marker '$component:uix-slider--force-slider' '-3'
    $reverse = Read-Marker
    Assert-Marker 'Negative acceleration reverses direction' ($reverse.velocity -lt 0) $reverse
    for ($i = 0; $i -lt 10; $i++) {
        $s = Read-Marker
        $samples.Add($s)
        Assert-Marker "Bounded motion sample $i" ([Math]::Abs($s.position.x) -le 0.7001 -and [Math]::Abs($s.velocity) -le 1.5001 -and $s.position.y -eq 0 -and $s.position.z -eq 0) $s
    }
    Set-Marker '$component:running' 'false'
    $stopped = Read-Marker
    Assert-Marker 'Stop returns to origin and clears velocity' ($stopped.velocity -eq 0 -and $stopped.position.x -eq 0 -and $stopped.label -eq 'START') $stopped
    $marker = Invoke-MarkerCli @('inspect', '$slot:marker', '--state', $State, '--depth', '0')
    Assert-Marker 'Actual marker Slot returns to origin' ($marker.position.x -eq 0 -and $marker.position.y -eq 0 -and $marker.position.z -eq 0) $marker.position
} catch {
    $failure = $_.ToString()
} finally {
    Set-Marker '$component:running' 'false'
    Set-Marker '$component:uix-slider--force-slider' ([double]$before.acceleration).ToString('R', [Globalization.CultureInfo]::InvariantCulture)
    $after = Read-Marker
    $restored = -not $after.running -and $after.acceleration -eq $before.acceleration -and $after.velocity -eq 0 -and $after.position.x -eq 0
    $directory = Split-Path -Parent $Report
    if ($directory) { $null = New-Item -ItemType Directory -Force -Path $directory }
    [pscustomobject]@{passed=($null -eq $failure -and $restored); verification='field-and-time-probe'; realInputVerified=$false; multiplayerVerified=$false; restored=$restored; before=$before; after=$after; checks=$checks.ToArray(); samples=$samples.ToArray(); failure=$failure} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Report -Encoding utf8
}
if ($failure) { throw $failure }
if (-not $restored) { throw 'Original shared controls were not restored.' }
Write-Output "Passed $($checks.Count) checks; controls restored. Report: $Report"
