param(
    [Parameter(Mandatory)][string]$Url,
    [switch]$Probe,
    [string]$State = '.resoloop/state/delayed-door.json',
    [string]$Report = 'artifacts/delayed-door-study/timing.json'
)
$ErrorActionPreference = 'Stop'
if (-not $Probe) { throw 'Use -Probe explicitly, with nobody operating this example during the check.' }
if (Test-Path -LiteralPath $Report) { throw 'Choose a new report path; reports are not overwritten.' }
function Invoke-DoorCli([string[]]$CliArgs) {
    $raw = & resoloop @CliArgs --url $Url --json
    $exitCode = $LASTEXITCODE
    $result = $raw | ConvertFrom-Json
    if ($exitCode -ne 0 -or -not $result.ok) { throw ($raw -join "`n") }
    return $result.data
}
function Read-Door {
    $data = Invoke-DoorCli @('observe', '$member:open-action.SetValue', '$member:close-at.Value', '$member:open-state.Value', '$member:readout-text.Content', '--state', $State)
    return [pscustomobject]@{
        now = [double]$data.values.'$member:open-action.SetValue'.member.value - 3.0
        deadline = [double]$data.values.'$member:close-at.Value'.member.value
        open = [bool]$data.values.'$member:open-state.Value'.member.value
        label = $data.values.'$member:readout-text.Content'.member.value
    }
}
function Set-Deadline([double]$Value) {
    $number = $Value.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
    $null = Invoke-DoorCli @('component', 'set', '$component:close-at', 'Value', $number, '--state', $State)
}
function Wait-Past([double]$Deadline) {
    $budget = [Diagnostics.Stopwatch]::StartNew()
    do {
        $sample = Read-Door
        if ($sample.now -gt $Deadline + 0.2) { return $sample }
        if ($budget.Elapsed.TotalSeconds -gt 20) { throw 'World time did not reach the deadline within the bounded wait.' }
        Start-Sleep -Milliseconds 150
    } while ($true)
}
$root = Invoke-DoorCli @('inspect', '$slot:root', '--state', $State, '--depth', '0')
if ($root.name -ne 'ResoLoop_Example_DelayedDoor' -or $root.parentId -ne 'Root') { throw 'The expected owned example was not resolved.' }
$before = Read-Door
$checks = [Collections.Generic.List[object]]::new()
function Assert-Door([string]$Name, $Sample, [bool]$ExpectedOpen) {
    $expectedLabel = if ($ExpectedOpen) { 'OPEN' } else { 'CLOSED' }
    $passed = $Sample.open -eq $ExpectedOpen -and $Sample.label -eq $expectedLabel
    $checks.Add([pscustomobject]@{name=$Name; passed=$passed; sample=$Sample})
    if (-not $passed) { throw "Failed: $Name" }
}
function Assert-Position([double]$ExpectedX) {
    $door = Invoke-DoorCli @('inspect', '$slot:door', '--state', $State, '--depth', '0')
    $passed = [Math]::Abs($door.position.x - $ExpectedX) -lt 0.0001 -and [Math]::Abs($door.position.y - 1.12) -lt 0.0001 -and [Math]::Abs($door.position.z) -lt 0.0001
    $checks.Add([pscustomobject]@{name="Door position x=$ExpectedX"; passed=$passed; position=$door.position})
    if (-not $passed) { throw 'Door position did not match.' }
}
$failure = $null
$restored = $false
try {
    Set-Deadline 0
    Assert-Door 'Expired deadline closes' (Read-Door) $false
    Assert-Position 0
    $snapshot = Read-Door
    $threeSecondDeadline = $snapshot.now + 3.0
    Set-Deadline $threeSecondDeadline
    Assert-Door 'Current button value opens' (Read-Door) $true
    Assert-Door 'Current button value expires' (Wait-Past $threeSecondDeadline) $false

    # Longer injected deadlines leave room for separate CLI connections.
    # Production duration and Flux are unchanged; this tests replacement semantics.
    $first = (Read-Door).now + 10.0
    Set-Deadline $first
    Assert-Door 'First deadline opens' (Read-Door) $true
    Assert-Position 1.3
    Start-Sleep -Milliseconds 1500
    $second = (Read-Door).now + 10.0
    if ($second -le $first) { throw 'The replacement did not extend the deadline.' }
    Set-Deadline $second
    $afterReplacement = Read-Door
    if ($afterReplacement.now -ge $first - 0.3) { throw 'CLI latency missed the replacement-before-expiry interval; result is inconclusive.' }
    Assert-Door 'Replacement applied before the old deadline' $afterReplacement $true
    $afterOld = Wait-Past $first
    if ($afterOld.now -ge $second - 0.3) { throw 'CLI latency missed the interval between deadlines; result is inconclusive.' }
    Assert-Door 'Still open after the superseded deadline' $afterOld $true
    Assert-Door 'Closes after the replacement deadline' (Wait-Past $second) $false
    Assert-Position 0
} catch {
    $failure = $_.ToString()
} finally {
    Set-Deadline $before.deadline
    $after = Read-Door
    $restored = $after.deadline -eq $before.deadline
    $directory = Split-Path -Parent $Report
    if ($directory) { $null = New-Item -ItemType Directory -Force -Path $directory }
    [pscustomobject]@{passed=($null -eq $failure -and $restored); verification='field-and-timing-probe'; realClickVerified=$false; multiplayerVerified=$false; restored=$restored; before=$before; after=$after; checks=$checks.ToArray(); failure=$failure} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Report -Encoding utf8
}
if ($failure) { throw $failure }
if (-not $restored) { throw 'Original deadline was not restored.' }
Write-Output "Passed $($checks.Count) checks; original deadline restored. Report: $Report"
