param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('Prepare','Inspect','Cleanup')]
    [string]$Action,
    [Parameter(Mandatory=$true)][string]$ServerBepInEx
)

$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0

$statePath=Join-Path $env:TEMP 'AMS26-Phase3-CacheSafety-State.json'
$serverAms=Join-Path $ServerBepInEx 'AutoModSync'
$cacheRoot=Join-Path $serverAms 'cache'
$sourceRoot=Join-Path $serverAms 'phase3-cache-safety-source'
$disablePrewarmMarker=Join-Path $serverAms 'phase3-test-disable-prewarm.once'
$safetyMarker=Join-Path $serverAms 'phase3-test-cache-safety.once'
$orphanZip=Join-Path $cacheRoot 'bundle-cache-phase3-orphan.zip'
$orphanTemp=Join-Path $cacheRoot 'bundle-build-phase3-orphan.tmp'
$serverLog=Join-Path $ServerBepInEx 'LogOutput.log'

function Ensure-Root {
    if(-not(Test-Path -LiteralPath $ServerBepInEx -PathType Container)){throw "Server BepInEx root not found: $ServerBepInEx"}
    New-Item -ItemType Directory -Path $serverAms,$cacheRoot -Force|Out-Null
}

function Read-SharedLogLines([string]$Path){
    if(-not(Test-Path -LiteralPath $Path -PathType Leaf)){return @()}
    $stream=$null;$reader=$null
    try{
        $stream=[System.IO.FileStream]::new($Path,[System.IO.FileMode]::Open,[System.IO.FileAccess]::Read,([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
        $reader=[System.IO.StreamReader]::new($stream,[System.Text.Encoding]::UTF8,$true,4096,$false)
        $lines=New-Object System.Collections.Generic.List[string]
        while(-not $reader.EndOfStream){[void]$lines.Add($reader.ReadLine())}
        return @($lines.ToArray())
    }finally{
        if($reader-ne$null){$reader.Dispose()}elseif($stream-ne$null){$stream.Dispose()}
    }
}

function Get-LineCount([string]$Path){return @(Read-SharedLogLines $Path).Count}

function Get-NewLines([string]$Path,[int]$StartCount){
    $all=@(Read-SharedLogLines $Path)
    if($StartCount-lt 0){$StartCount=0}
    if($all.Length-lt$StartCount){return $all}
    if($StartCount-ge$all.Length){return @()}
    return @($all[$StartCount..($all.Length-1)])
}

function Prepare-Test {
    Ensure-Root
    if(Test-Path -LiteralPath $statePath -PathType Leaf){
        $existing=Get-Content -LiteralPath $statePath -Raw|ConvertFrom-Json
        $newServer=@(Get-NewLines $serverLog ([int]$existing.serverLogLines))
        $newServerText=$newServer-join[Environment]::NewLine
        $armedNow=(Test-Path -LiteralPath $safetyMarker -PathType Leaf)
        $suiteBegan=$newServerText.IndexOf('AutoModSync DEV TEST Phase 3 cache-safety suite BEGIN.',[StringComparison]::OrdinalIgnoreCase)-ge 0
        if($armedNow-or$suiteBegan){
            throw 'Phase 3 cache-safety suite is already armed or has started. Do not Prepare again; run Inspect after the server-side suite finishes, or Cleanup to reset it.'
        }
        Write-Warning 'Found stale Phase 3 cache-safety state with no armed marker and no suite BEGIN evidence. Resetting that stale state and re-arming cleanly.'
        try{Remove-Item -LiteralPath $statePath -Force}catch{}
    }

    foreach($p in @($disablePrewarmMarker,$safetyMarker,$orphanZip,$orphanTemp)){
        try{if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Force}}catch{}
    }
    try{if(Test-Path -LiteralPath $sourceRoot){Remove-Item -LiteralPath $sourceRoot -Recurse -Force}}catch{}

    [IO.File]::WriteAllBytes($orphanZip,[byte[]](1,2,3,4,5,6,7,8))
    [IO.File]::WriteAllBytes($orphanTemp,[byte[]](9,10,11,12,13,14,15,16))
    New-Item -ItemType File -Path $disablePrewarmMarker -Force|Out-Null
    New-Item -ItemType File -Path $safetyMarker -Force|Out-Null

    [ordered]@{
        serverLogLines=(Get-LineCount $serverLog)
        preparedUtc=[DateTime]::UtcNow.ToString('o')
    }|ConvertTo-Json|Set-Content -LiteralPath $statePath -Encoding UTF8

    Write-Host ''
    Write-Host 'ARMED: Phase 3 cache safety / eviction / restart-orphan suite.'
    Write-Host 'Start ONLY the dedicated server. Valheim client is not needed.'
    Write-Host 'Wait until the server logs "AutoModSync DEV TEST Phase 3 cache-safety suite PASS."'
    Write-Host 'Then run -Action Inspect.'
}

function Inspect-Test {
    Ensure-Root
    if(-not(Test-Path -LiteralPath $statePath -PathType Leaf)){throw 'No Phase 3 cache-safety state exists. Run Prepare first.'}
    $state=Get-Content -LiteralPath $statePath -Raw|ConvertFrom-Json
    $newServer=@(Get-NewLines $serverLog ([int]$state.serverLogLines))
    $serverText=$newServer-join[Environment]::NewLine
    $ok=$true

    Write-Host ''
    Write-Host 'Inspecting Phase 3 cache safety / eviction / restart-orphan suite...'

    $cleanup=[regex]::Match($serverText,'AutoModSync startup cache cleanup: discoveredZip=(\d+), deletedZip=(\d+), discoveredTemp=(\d+), deletedTemp=(\d+)\.')
    if($cleanup.Success){
        $dz=[int]$cleanup.Groups[1].Value
        $rz=[int]$cleanup.Groups[2].Value
        $dt=[int]$cleanup.Groups[3].Value
        $rt=[int]$cleanup.Groups[4].Value
        if($dz-ge 1-and$dt-ge 1-and$rz-eq$dz-and$rt-eq$dt){
            Write-Host("  PASS startup removed all discovered prior-process cache artifacts: ZIP {0}/{1}, temp {2}/{3}."-f$rz,$dz,$rt,$dt)
        }else{
            Write-Host("  FAIL startup cache cleanup was incomplete: ZIP {0}/{1}, temp {2}/{3}."-f$rz,$dz,$rt,$dt)
            $ok=$false
        }
    }else{
        Write-Host '  FAIL missing startup orphan-cache cleanup evidence.'
        $ok=$false
    }

    $checks=@(
        @('AutoModSync DEV TEST Phase 3 PASS restart rebuild used cache=MISS after startup orphan cleanup.','post-restart request rebuilt from MISS instead of trusting an orphan'),
        @('AutoModSync DEV TEST Phase 3 PASS same-size post-manifest mutation aborted during source re-hash before publication.','same-size post-manifest mutation was rejected by source re-hash'),
        @('AutoModSync DEV TEST Phase 3 PASS failed ZIP construction left no cache entry, published ZIP, or bundle-build temp file.','failed ZIP construction left no published/cache/temp artifact'),
        @('AutoModSync DEV TEST Phase 3 PASS BundleCacheSeconds=0 shared the active artifact and removed it only after the last release.','zero-TTL active sharing/removal policy passed'),
        @('AutoModSync DEV TEST Phase 3 PASS TTL expiry removed only the idle artifact and preserved the active artifact.','TTL expiry removed idle only'),
        @('AutoModSync DEV TEST Phase 3 PASS cache-budget LRU evicted the oldest idle artifact while preserving newer idle and active artifacts.','LRU budget preserved active and newer idle artifacts'),
        @('AutoModSync DEV TEST Phase 3 cache-safety suite PASS.','complete cache-safety self-test passed')
    )

    foreach($check in $checks){
        if($serverText.IndexOf($check[0],[StringComparison]::OrdinalIgnoreCase)-ge 0){
            Write-Host("  PASS {0}."-f$check[1])
        }else{
            Write-Host("  FAIL missing evidence: {0}."-f$check[1])
            $ok=$false
        }
    }

    if($serverText.IndexOf('AutoModSync DEV TEST Phase 3 cache-safety suite FAIL:',[StringComparison]::OrdinalIgnoreCase)-ge 0){
        Write-Host '  FAIL server logged a cache-safety self-test failure.'
        $ok=$false
    }

    if(Test-Path -LiteralPath $orphanZip){
        Write-Host("  FAIL orphan ZIP still exists: {0}"-f$orphanZip)
        $ok=$false
    }else{
        Write-Host '  PASS armed orphan ZIP was removed during server startup.'
    }

    if(Test-Path -LiteralPath $orphanTemp){
        Write-Host("  FAIL orphan temp still exists: {0}"-f$orphanTemp)
        $ok=$false
    }else{
        Write-Host '  PASS armed orphan temp was removed during server startup.'
    }

    $temps=@(Get-ChildItem -LiteralPath $cacheRoot -Filter 'bundle-build-*.tmp' -File -ErrorAction SilentlyContinue)
    if($temps.Count-eq 0){
        Write-Host '  PASS no bundle-build temp files remain after the suite.'
    }else{
        Write-Host("  FAIL {0} bundle-build temp file(s) remain after the suite."-f$temps.Count)
        $ok=$false
    }

    foreach($pair in @(
        @($disablePrewarmMarker,'startup-prewarm-disable marker'),
        @($safetyMarker,'cache-safety marker')
    )){
        if(Test-Path -LiteralPath $pair[0]){
            Write-Host("  FAIL {0} was not consumed."-f$pair[1])
            $ok=$false
        }else{
            Write-Host("  PASS {0} was consumed."-f$pair[1])
        }
    }

    if(Test-Path -LiteralPath $sourceRoot){
        Write-Host("  FAIL development cache-safety source directory was not cleaned: {0}"-f$sourceRoot)
        $ok=$false
    }else{
        Write-Host '  PASS development cache-safety source fixtures were removed.'
    }

    if($ok){
        Write-Host ''
        Write-Host 'PASS: Phase 3 cache safety, zero-TTL/TTL/LRU eviction, failed-build cleanup, and restart-orphan handling all passed.'
        Write-Host 'Stop the dedicated server, then run -Action Cleanup.'
        return
    }

    Write-Host ''
    Write-Host 'Relevant new server AutoModSync lines:'
    $newServer|Where-Object{$_-match'AutoModSync'}|Select-Object -Last 120|ForEach-Object{Write-Host $_}
    exit 1
}

function Cleanup-Test {
    Ensure-Root
    foreach($p in @($disablePrewarmMarker,$safetyMarker,$orphanZip,$orphanTemp)){
        try{if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Force}}catch{}
    }
    try{if(Test-Path -LiteralPath $sourceRoot){Remove-Item -LiteralPath $sourceRoot -Recurse -Force}}catch{}
    try{if(Test-Path -LiteralPath $statePath){Remove-Item -LiteralPath $statePath -Force}}catch{}
    Write-Host 'Phase 3 cache-safety test state cleaned.'
}

switch($Action){
    'Prepare'{Prepare-Test}
    'Inspect'{Inspect-Test}
    'Cleanup'{Cleanup-Test}
}
