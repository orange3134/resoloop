param(
    [Parameter(Mandatory)][string]$Url,
    [switch]$Probe,
    [string]$Report = 'artifacts/ordered-attempt-study/verification.json'
)
$ErrorActionPreference = 'Stop'
if (-not $Probe) { throw 'Use -Probe explicitly to create and remove an isolated test fixture.' }
if (Test-Path -LiteralPath $Report) { throw 'Choose a new report path; reports are not overwritten.' }
function Invoke-StudyCli([string[]]$CliArgs) {
    $raw = & resoloop @CliArgs --url $Url --json
    $exitCode = $LASTEXITCODE
    $result = $raw | ConvertFrom-Json
    if ($exitCode -ne 0 -or -not $result.ok) { throw [InvalidOperationException]::new("resoloop failed (exit $exitCode): $($raw -join [Environment]::NewLine)") }
    return $result.data
}
$probeName = 'ResoLoop_Test_OrderedAttempt_' + [guid]::NewGuid().ToString('N')
$directory = Join-Path ([IO.Path]::GetFullPath('artifacts/ordered-attempt-study')) $probeName
$null = New-Item -ItemType Directory -Path $directory
$state = Join-Path $directory 'state.json'
$worldPath = Join-Path $directory 'world.json'
$fluxPath = Join-Path $directory 'flux.json'
$sourcePath = Join-Path $PSScriptRoot 'flux/OrderedAttempt.pg'
$source = Get-Content -LiteralPath $sourcePath -Raw
$sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
# Retain the production logic exactly. Replace only the two button entrypoints
# with one host-only OnStart, because Link cannot press UIX buttons.
$entryLines = @(
    'in TryButton: FrooxEngine.UIX.Button element',
    'in ResetButton: FrooxEngine.UIX.Button element',
    '    _tryButton = ObjectCast<FrooxEngine.UIX.Button,IButton>(TryButton);',
    '    _resetButton = ObjectCast<FrooxEngine.UIX.Button,IButton>(ResetButton);',
    '    ButtonEvents(Button=asDrivenGlobal<IButton>(_tryButton), Pressed=_run);',
    '    ButtonEvents(Button=asDrivenGlobal<IButton>(_resetButton), Pressed=_reset);'
)
foreach ($line in $entryLines) {
    if (($source.Split(@($line), [StringSplitOptions]::None).Count - 1) -ne 1) { throw "Expected exactly one source line: $line" }
}
$fixtureSource = $source
foreach ($line in $entryLines) { $fixtureSource = $fixtureSource.Replace($line, '') }
$lastBrace = $fixtureSource.LastIndexOf('}')
if ($lastBrace -lt 0) { throw 'Module closing brace is missing.' }
$checks = [Collections.Generic.List[object]]::new()
$probeSlot = $null
$failure = $null
$cleanup = $false
try {
    $world = @{schemaVersion='1';ownership=@{key=$probeName};slot=@{key='root';name=$probeName;parent='Root'};children=@(
        @{slot=@{key='mode-slot';name='Mode'};components=@(@{key='mode';type='[FrooxEngine]FrooxEngine.ValueField<int>';initialFields=@{Value=1}})},
        @{slot=@{key='logic';name='State'};components=@(
            @{key='attempts';type='[FrooxEngine]FrooxEngine.ValueField<int>';initialFields=@{Value=0}},
            @{key='target';type='[FrooxEngine]FrooxEngine.ValueField<float>';initialFields=@{Value=0}},
            @{key='summary';type='[FrooxEngine]FrooxEngine.ValueField<string>';initialFields=@{Value='READY'}}
        )}
    )}
    $world | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $worldPath -Encoding utf8
    $plan = Invoke-StudyCli @('diff',$worldPath,'--state',$state,'--brief')
    if ($plan.updates -or $plan.deletes -or $plan.renames) { throw 'Probe plan was not creation-only.' }
    $created = Invoke-StudyCli @('apply',$worldPath,'--state',$state,'--brief')
    $probeSlot = $created.slotId
    'name = "ordered-attempt-probe"' | Set-Content (Join-Path $directory 'protograph.toml') -Encoding utf8
    $flux = @{schemaVersion='1';projectDirectory='.';parent='$slot:logic';worldState=$state;deployState='flux-state.json';modules=@(@{
        name='ordered-attempt';source='OrderedAttempt.pg';module='OrderedAttempt';bindings=@{
            Mode=@{target='$member:mode.Value';mode='source'}
            Target=@{target='$member:target.Value';mode='source'}
            Attempts=@{target='$member:attempts.Value';mode='source'}
            Summary=@{target='$member:summary.Value';mode='source'}
        }
    })}
    $flux | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $fluxPath -Encoding utf8
    $cases = @(
        @{name='Success writes and counts once';mode=1;entry='_run';target=1;attempts=1;summary='WRITTEN'},
        @{name='False condition skips write but counts once';mode=0;entry='_run';target=1;attempts=2;summary='SKIPPED'},
        @{name='OnFail leaves target unchanged but counts once';mode=2;entry='_run';target=1;attempts=3;summary='WRITE FAILED'},
        @{name='Success after failure uses the current result';mode=1;entry='_run';target=2;attempts=4;summary='WRITTEN'},
        @{name='Repeated failure counts exactly once';mode=2;entry='_run';target=2;attempts=5;summary='WRITE FAILED'},
        @{name='Reset clears state without adding an attempt';mode=1;entry='_reset';target=0;attempts=0;summary='READY'}
    )
    foreach ($case in $cases) {
        $null = Invoke-StudyCli @('component','set','$component:mode','Value',([string]$case.mode),'--state',$state)
        # Change the fixture source for every case so deploy-manifest starts a
        # fresh node instance rather than legitimately skipping unchanged code.
        $entry = '    // Probe case: ' + $case.name + "`n" + '    OnStart(OnlyHost=true, Trigger=' + $case.entry + ');' + "`n"
        $fixtureSource.Insert($lastBrace, $entry) | Set-Content (Join-Path $directory 'OrderedAttempt.pg') -Encoding utf8
        $deployment = Invoke-StudyCli @('flux','deploy-manifest',$fluxPath)
        $deployment | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $directory ('deploy-' + $checks.Count + '.json')) -Encoding utf8
        $observed = Invoke-StudyCli @('observe','$member:attempts.Value','$member:target.Value','$member:summary.Value','--state',$state)
        $actual = @{attempts=$observed.values.'$member:attempts.Value'.member.value;target=$observed.values.'$member:target.Value'.member.value;summary=$observed.values.'$member:summary.Value'.member.value}
        $passed = $actual.attempts -eq $case.attempts -and $actual.target -eq $case.target -and $actual.summary -eq $case.summary
        $checks.Add(@{name=$case.name;passed=$passed;expected=$case;actual=$actual})
        if (-not $passed) { throw ('Failed: ' + $case.name) }
    }
} catch {
    $failure = 'Probe failed: ' + ($_ | Out-String)
} finally {
    try {
        # Recover the managed root after a partial apply before cleanup.
        if (-not $probeSlot -and (Test-Path -LiteralPath $state)) {
            $owned = Invoke-StudyCli @('inspect','$slot:root','--state',$state,'--depth','0')
            $probeSlot = $owned.id
        }
        if ($probeSlot) {
            $owned = Invoke-StudyCli @('inspect',$probeSlot,'--depth','0')
            if ($owned.name -ne $probeName -or $owned.parentId -ne 'Root') { throw 'Probe ownership changed; refusing deletion.' }
            $null = Invoke-StudyCli @('slot','delete',$probeSlot,'--yes')
            $cleanup = $true
        }
    } finally {
        $reportDirectory = Split-Path -Parent $Report
        if ($reportDirectory) { $null = New-Item -ItemType Directory -Force -Path $reportDirectory }
        @{passed=($null -eq $failure -and $cleanup -and $checks.Count -eq 6);verification='isolated-production-logic-with-OnStart-entry';sourceHash=$sourceHash;realClickVerified=$false;multiplayerVerified=$false;probeCleanup=$cleanup;checks=$checks.ToArray();failure=$failure} | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $Report -Encoding utf8
    }
}
if ($null -ne $failure) { throw $failure }
if ($checks.Count -ne 6) { throw 'Not all required cases completed.' }
if (-not $cleanup) { throw 'Probe cleanup incomplete.' }
"Passed $($checks.Count) cases; removed the exact isolated test Slot. Report: $Report"
