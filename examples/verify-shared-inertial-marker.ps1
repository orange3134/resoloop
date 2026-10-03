param(
    [Parameter(Mandatory)][string]$Url,
    [switch]$Probe,
    [string]$State = '.resoloop/state/shared-inertial-marker.json',
    [string]$Report = 'artifacts/shared-inertial-marker-study/motion.json'
)
$ErrorActionPreference = 'Stop'
if (-not $Probe) { throw 'Use -Probe explicitly while nobody operates this example.' }
if (Test-Path -LiteralPath $Report) { throw 'Choose a new report path.' }
function Invoke-SharedCli([string[]]$Arguments) {
    $raw = & resoloop @Arguments --url $Url --json 2>&1
    $exitCode = $LASTEXITCODE
    $result = ($raw -join "`n") | ConvertFrom-Json
    if ($exitCode -ne 0 -or -not $result.ok) { throw ($raw -join "`n") }
    $result.data
}
function Read-Marker {
    $r = Invoke-SharedCli @('observe', '$member:operator.Reference', '$member:running.Value', '$member:uix-slider--force-slider.Value', '$member:position-output.Value', '$member:velocity.Value', '$member:footer-text.Content', '--state', $State)
    [pscustomobject]@{
        operator = $r.values.'$member:operator.Reference'.member.targetId
        running = $r.values.'$member:running.Value'.member.value
        acceleration = $r.values.'$member:uix-slider--force-slider.Value'.member.value
        position = $r.values.'$member:position-output.Value'.member.value
        velocity = $r.values.'$member:velocity.Value'.member.value
        label = $r.values.'$member:footer-text.Content'.member.value
    }
}
function Set-Field([string]$Key, [string]$Member, [string]$Value) {
    $null = Invoke-SharedCli @('component','set',('$component:' + $Key),$Member,$Value,'--state',$State)
}
$root = Invoke-SharedCli @('inspect','$slot:root','--state',$State,'--depth','0')
if ($root.name -ne 'ResoLoop_Example_SharedInertialMarker' -or $root.parentId -ne 'Root') { throw 'Expected owned example not found.' }
$before = Read-Marker
if ($before.operator -or $before.running) { throw 'Release the example before probing it.' }
$checks = [Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Passed, $Sample) {
    $checks.Add([pscustomobject]@{name=$Name;passed=$Passed;sample=$Sample})
    if (-not $Passed) { throw "Failed: $Name" }
}
$probeName = 'ResoLoop_Test_SharedMarkerUser_' + [guid]::NewGuid().ToString('N')
$probeDirectory = Join-Path ([IO.Path]::GetFullPath('artifacts/shared-inertial-marker-study')) $probeName
$null = New-Item -ItemType Directory -Path $probeDirectory
$probeState = Join-Path $probeDirectory 'state.json'
$probeSlot = $null
$failure = $null
$cleanup = $false
$restored = $false
try {
    # Assign the host through a temporary Flux impulse: ResoniteLink cannot reliably
    # write a User reference from the returned raw User ID on this runtime pair.
    $world = @{schemaVersion='1';ownership=@{key=$probeName};slot=@{key='root';name=$probeName;parent='Root'}}
    $worldPath = Join-Path $probeDirectory 'world.json'
    $world | ConvertTo-Json -Depth 10 | Set-Content $worldPath -Encoding utf8
    $created = Invoke-SharedCli @('apply',$worldPath,'--state',$probeState,'--brief')
    $probeSlot = $created.slotId
    'name = "shared-marker-user-probe"' | Set-Content (Join-Path $probeDirectory 'protograph.toml') -Encoding utf8
    @'
module ProbeHost
in Target: User mutable
where { OnStart(OnlyHost=true, Trigger=impulse { Target <- LocalUser; }); }
'@ | Set-Content (Join-Path $probeDirectory 'ProbeHost.pg') -Encoding utf8
    $flux = @{schemaVersion='1';projectDirectory='.';parent=$probeSlot;worldState=[IO.Path]::GetFullPath($State);deployState='flux-state.json';modules=@(@{name='probe-host';source='ProbeHost.pg';module='ProbeHost';bindings=@{Target=@{target='$member:operator.Reference';mode='source'}}})}
    $fluxPath = Join-Path $probeDirectory 'flux.json'
    $flux | ConvertTo-Json -Depth 10 | Set-Content $fluxPath -Encoding utf8
    Set-Field 'position-output' 'Value' '[0,0,0]'
    Set-Field 'velocity' 'Value' '0'
    Set-Field 'uix-slider--force-slider' 'Value' '0.04'
    Set-Field 'running' 'Value' 'true'
    $unclaimed = Read-Marker
    Check 'No operator means no fallback integration by host' ($unclaimed.velocity -eq 0 -and $unclaimed.position.x -eq 0) $unclaimed
    $null = Invoke-SharedCli @('flux','deploy-manifest',$fluxPath)
    $moving = Read-Marker
    Check 'Assigned operator updates shared position and velocity' ($moving.velocity -gt 0 -and $moving.position.x -gt 0 -and $null -ne $moving.operator) $moving
    Check 'Operator name is displayed' ($moving.label -like 'Operator: ?*') $moving
    Set-Field 'uix-slider--force-slider' 'Value' '0'
    $coast = Read-Marker
    Check 'Zero acceleration keeps nonzero shared velocity' ($coast.velocity -gt 0) $coast
    $later = Read-Marker
    Check 'Shared velocity decays' ($later.velocity -gt 0 -and $later.velocity -lt $coast.velocity) $later
    # Keep the reversal observation away from the boundary despite CLI latency.
    Set-Field 'uix-slider--force-slider' 'Value' '-0.1'
    $reverse = Read-Marker
    Check 'Operator reverses shared motion' ($reverse.velocity -lt 0) $reverse
    Set-Field 'uix-slider--force-slider' 'Value' '-3'
    for ($i=0; $i -lt 4; $i++) {
        $s = Read-Marker
        Check "Bounded shared result $i" ([Math]::Abs($s.position.x) -le 0.7001 -and [Math]::Abs($s.velocity) -le 1.5001) $s
    }
    Set-Field 'running' 'Value' 'false'
    Set-Field 'operator' 'Reference' 'null'
    Set-Field 'position-output' 'Value' '[0.25,0,0]'
    Set-Field 'velocity' 'Value' '0.12'
    $held = Read-Marker
    $heldAgain = Read-Marker
    Check 'Shared results persist without a local integration Drive' ($held.position.x -eq 0.25 -and $heldAgain.position.x -eq 0.25 -and [Math]::Abs($heldAgain.velocity - 0.12) -lt 0.0001) $heldAgain
    $marker = Invoke-SharedCli @('inspect','$slot:marker','--state',$State,'--depth','0')
    Check 'Native driver displays the shared position' ($marker.position.x -eq 0.25) $marker.position
} catch {
    $failure = $_.ToString()
} finally {
    try {
        Set-Field 'running' 'Value' 'false'
        Set-Field 'operator' 'Reference' 'null'
        Set-Field 'position-output' 'Value' ($before.position|ConvertTo-Json -Compress)
        Set-Field 'velocity' 'Value' ([double]$before.velocity).ToString('R',[Globalization.CultureInfo]::InvariantCulture)
        Set-Field 'uix-slider--force-slider' 'Value' ([double]$before.acceleration).ToString('R',[Globalization.CultureInfo]::InvariantCulture)
        $after = Read-Marker
        $restored = -not $after.operator -and -not $after.running -and $after.velocity -eq $before.velocity -and $after.position.x -eq $before.position.x -and $after.acceleration -eq $before.acceleration
    } finally {
        if ($probeSlot) {
            $owned = Invoke-SharedCli @('inspect',$probeSlot,'--depth','0')
            if ($owned.name -ne $probeName -or $owned.parentId -ne 'Root') { throw 'Probe ownership changed; refusing deletion.' }
            $null = Invoke-SharedCli @('slot','delete',$probeSlot,'--yes')
            $cleanup = $true
        }
        $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Report)
        [pscustomobject]@{passed=($null -eq $failure -and $restored -and $cleanup);verification='shared-fields-and-single-operator-probe';realClickVerified=$false;multiplayerVerified=$false;startupReloadVerified=$false;probeCleanup=$cleanup;restored=$restored;before=$before;after=$after;checks=$checks.ToArray();failure=$failure} | ConvertTo-Json -Depth 10 | Set-Content $Report -Encoding utf8
    }
}
if ($failure) { throw $failure }
if (-not $restored -or -not $cleanup) { throw 'Restoration or probe cleanup incomplete.' }
"Passed $($checks.Count) checks; restored shared state and removed the exact probe Slot."
