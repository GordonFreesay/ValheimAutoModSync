param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('Prepare','Inspect','Cleanup')]
    [string]$Action,
    [Parameter(Mandatory=$true)][string]$ServerBepInEx,
    [Parameter(Mandatory=$true)][string]$ClientBepInEx
)

$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0

$statePath=Join-Path $env:TEMP 'AMS26-Phase3-Prewarm-State.json'
$serverAms=Join-Path $ServerBepInEx 'AutoModSync'
$clientAms=Join-Path $ClientBepInEx 'AutoModSync'
$serverConfig=Join-Path (Join-Path $ServerBepInEx 'config') 'com.gordonfreesay.valheimautomodsync.server.cfg'
$configBackup=$serverConfig+'.phase3-prewarm-test.bak'
$nearlyBareMarker=Join-Path $clientAms 'phase3-test-emulate-nearly-bare.once'
$applyFailureMarker=Join-Path $clientAms 'phase1-test-fail-apply-prep.once'
$clientPending=Join-Path $clientAms 'pending.txt'
$clientReconnect=Join-Path $clientAms 'reconnect.txt'
$clientTransaction=Join-Path $clientAms 'apply-transaction'
$clientStaging=Join-Path $clientAms 'staging'
$clientLog=Join-Path $ClientBepInEx 'LogOutput.log'
$serverLog=Join-Path $ServerBepInEx 'LogOutput.log'

function Ensure-Roots {
    if(-not(Test-Path -LiteralPath $ServerBepInEx -PathType Container)){throw "Server BepInEx root not found: $ServerBepInEx"}
    if(-not(Test-Path -LiteralPath $ClientBepInEx -PathType Container)){throw "Client BepInEx root not found: $ClientBepInEx"}
    if(-not(Test-Path -LiteralPath $serverConfig -PathType Leaf)){throw "Server AutoModSync config not found: $serverConfig"}
    New-Item -ItemType Directory -Path $serverAms,$clientAms -Force|Out-Null
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

function Set-TestConfig {
    $raw=[IO.File]::ReadAllText($serverConfig)
    $ttlPattern='(?mi)^(\s*BundleCacheSeconds\s*=\s*)\d+\s*$'
    $prebuildPattern='(?mi)^(\s*PrebuildFreshClientBundle\s*=\s*)(true|false)\s*$'
    if([regex]::Matches($raw,$ttlPattern).Count-ne 1){throw 'Expected exactly one BundleCacheSeconds setting in the server config.'}
    if([regex]::Matches($raw,$prebuildPattern).Count-ne 1){throw 'Expected exactly one PrebuildFreshClientBundle setting in the server config.'}
    $raw=[regex]::Replace($raw,$ttlPattern,'13')
    $raw=[regex]::Replace($raw,$prebuildPattern,'1true')
    [IO.File]::WriteAllText($serverConfig,$raw,(New-Object System.Text.UTF8Encoding($false)))
}

function Prepare-Test {
    Ensure-Roots
    if(Test-Path -LiteralPath $configBackup -PathType Leaf){throw "Phase 3 config backup already exists: $configBackup . Run Cleanup before re-arming."}
    if(Test-Path -LiteralPath $clientPending -PathType Leaf){throw "Client has an existing pending apply: $clientPending"}
    if(Test-Path -LiteralPath $clientReconnect -PathType Leaf){throw "Client has an existing AutoModSync reconnect token: $clientReconnect"}
    if(Test-Path -LiteralPath $clientTransaction){throw "Client has an existing apply transaction: $clientTransaction"}
    foreach($p in @($nearlyBareMarker,$applyFailureMarker)){try{if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Force}}catch{}}
    if(Test-Path -LiteralPath $clientStaging){Remove-Item -LiteralPath $clientStaging -Recurse -Force}
    Copy-Item -LiteralPath $serverConfig -Destination $configBackup -Force
    Set-TestConfig
    New-Item -ItemType File -Path $nearlyBareMarker -Force|Out-Null
    New-Item -ItemType File -Path $applyFailureMarker -Force|Out-Null
    [ordered]@{
        clientLogLines=(Get-LineCount $clientLog)
        serverLogLines=(Get-LineCount $serverLog)
        preparedUtc=[DateTime]::UtcNow.ToString('o')
        testBundleCacheSeconds=3
    }|ConvertTo-Json|Set-Content -LiteralPath $statePath -Encoding UTF8
    Write-Host ''
    Write-Host 'ARMED: Phase 3 dedicated-server startup-prewarm TTL-retention test.'
    Write-Host 'Temporary server config: PrebuildFreshClientBundle=true, BundleCacheSeconds=3.'
    Write-Host '1. Start ONLY the dedicated server.'
    Write-Host '2. Wait until the server logs "AutoModSync fresh-client bundle prewarm ready".'
    Write-Host '3. After that line appears, wait at least 8 more seconds.'
    Write-Host '4. Start Valheim and join once.'
    Write-Host '5. Wait for the deliberate apply-preparation failure/disconnect, then run -Action Inspect.'
}

function Inspect-Test {
    Ensure-Roots
    if(-not(Test-Path -LiteralPath $statePath -PathType Leaf)){throw 'No prepared Phase 3 prewarm state exists. Run -Action Prepare first.'}
    $state=Get-Content -LiteralPath $statePath -Raw|ConvertFrom-Json
    $newClient=@(Get-NewLines $clientLog ([int]$state.clientLogLines))
    $newServer=@(Get-NewLines $serverLog ([int]$state.serverLogLines))
    $clientText=$newClient-join[Environment]::NewLine
    $serverText=$newServer-join[Environment]::NewLine
    $ok=$true
    Write-Host ''
    Write-Host 'Inspecting Phase 3 startup-prewarm retention...'

    $prewarmMatch=[regex]::Match($serverText,'AutoModSync fresh-client bundle prewarm ready: cache=MISS, key=([0-9a-fA-F]+)')
    if($prewarmMatch.Success){$key=$prewarmMatch.Groups[1].Value.ToLowerInvariant();Write-Host("  PASS startup prewarm published one MISS baseline: key={0}."-f$key)}
    else{$key='';Write-Host '  FAIL missing startup prewarm MISS/ready evidence.';$ok=$false}

    $hasMiss=$serverText.IndexOf('AutoModSync bundle cache MISS key=',[StringComparison]::OrdinalIgnoreCase)-ge 0
    $hasBuild=$serverText.IndexOf('ZIP build=',[StringComparison]::OrdinalIgnoreCase)-ge 0
    $hasHash=$serverText.IndexOf('ZIP SHA-256=',[StringComparison]::OrdinalIgnoreCase)-ge 0
    if($hasMiss-and$hasBuild-and$hasHash){Write-Host '  PASS startup build logged ZIP-build and SHA-256 preparation timings.'}
    else{Write-Host '  FAIL missing ZIP build/SHA-256 timing evidence.';$ok=$false}

    $ttlMatch=[regex]::Match($serverText,'AutoModSync startup-pinned bundle survived ordinary cache TTL until first real client use: idle=([0-9.]+) s, BundleCacheSeconds=(\d+), key=([0-9a-fA-F]+)')
    if($ttlMatch.Success){
        $idle=[double]::Parse($ttlMatch.Groups[1].Value,[Globalization.CultureInfo]::InvariantCulture)
        $ttl=[int]$ttlMatch.Groups[2].Value
        $ttlKey=$ttlMatch.Groups[3].Value.ToLowerInvariant()
        if(($idle-gt$ttl)-and(($key.Length-eq 0)-or($ttlKey-eq$key))){Write-Host("  PASS startup pin retained baseline beyond ordinary TTL: idle={0:0.000}s > TTL={1}s."-f$idle,$ttl)}
        else{Write-Host '  FAIL TTL-retention evidence did not exceed TTL or key did not match.';$ok=$false}
    }else{Write-Host '  FAIL missing startup-pinned TTL-retention evidence. Wait 8+ seconds after prewarm ready before joining.';$ok=$false}

    $matchingHit=$false
    foreach($m in [regex]::Matches($serverText,'AutoModSync bundle ready: cache=HIT, key=([0-9a-fA-F]+)')){
        if(($key.Length-eq 0)-or($m.Groups[1].Value.ToLowerInvariant()-eq$key)){$matchingHit=$true;break}
    }
    if($matchingHit){Write-Host '  PASS first real nearly-bare request reused the same startup artifact with cache=HIT.'}
    else{Write-Host '  FAIL no matching cache=HIT for the startup baseline was observed.';$ok=$false}

    if($key.Length-gt 0){
        $missCount=[regex]::Matches($serverText,('AutoModSync bundle cache MISS key='+[regex]::Escape($key))).Count
        if($missCount-eq 1){Write-Host '  PASS baseline key was built exactly once; no join-time rebuild occurred.'}
        else{Write-Host("  FAIL expected exactly one MISS for baseline key {0}, observed {1}."-f$key,$missCount);$ok=$false}
    }

    $clientChecks=@(
        @('AutoModSync DEV TEST emulating a nearly-bare client for Phase 3 startup-prewarm validation','client consumed the nearly-bare emulator marker'),
        @('AutoModSync compressed package verified and unpacked:','client fully verified/extracted the prewarmed bundle'),
        @('Mods downloaded but automatic apply/restart failed: DEV TEST forced apply/restart preparation failure','test stopped before durable/live apply')
    )
    foreach($check in $clientChecks){
        if($clientText.IndexOf($check[0],[StringComparison]::OrdinalIgnoreCase)-ge 0){Write-Host("  PASS {0}."-f$check[1])}
        else{Write-Host("  FAIL missing client evidence: {0}"-f$check[1]);$ok=$false}
    }

    if($clientText.IndexOf('AutoModSync released the original Valheim ServerHandshake',[StringComparison]::OrdinalIgnoreCase)-ge 0){Write-Host '  FAIL vanilla ServerHandshake was replayed during deliberately aborted protected sync.';$ok=$false}
    else{Write-Host '  PASS protected test failure did not replay vanilla ServerHandshake.'}

    if(Test-Path -LiteralPath $clientPending -PathType Leaf){Write-Host("  FAIL durable pending apply exists: {0}"-f$clientPending);$ok=$false}
    else{Write-Host '  PASS no durable pending apply was accepted.'}

    if(Test-Path -LiteralPath $nearlyBareMarker){Write-Host '  FAIL nearly-bare marker was not consumed.';$ok=$false}else{Write-Host '  PASS nearly-bare marker was consumed.'}
    if(Test-Path -LiteralPath $applyFailureMarker){Write-Host '  FAIL apply-preparation marker was not consumed.';$ok=$false}else{Write-Host '  PASS apply-preparation marker was consumed.'}

    if($ok){
        Write-Host ''
        Write-Host 'PASS: startup prewarm was built once, survived beyond ordinary TTL, and the first real nearly-bare client reused it without a join-time ZIP rebuild.'
        Write-Host 'Stop Valheim and the dedicated server, then run -Action Cleanup.'
        return
    }

    Write-Host ''
    Write-Host 'Relevant new server AutoModSync lines:'
    $newServer|Where-Object{$_-match'AutoModSync'}|Select-Object -Last 80|ForEach-Object{Write-Host $_}
    Write-Host ''
    Write-Host 'Relevant new client AutoModSync lines:'
    $newClient|Where-Object{$_-match'AutoModSync'}|Select-Object -Last 80|ForEach-Object{Write-Host $_}
    exit 1
}

function Cleanup-Test {
    Ensure-Roots
    foreach($p in @($nearlyBareMarker,$applyFailureMarker)){try{if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Force}}catch{}}
    if(Test-Path -LiteralPath $configBackup -PathType Leaf){
        Copy-Item -LiteralPath $configBackup -Destination $serverConfig -Force
        Remove-Item -LiteralPath $configBackup -Force
        Write-Host 'Restored original AutoModSync server config.'
    }
    if((-not(Test-Path -LiteralPath $clientPending))-and(-not(Test-Path -LiteralPath $clientTransaction))){
        try{if(Test-Path -LiteralPath $clientStaging){Remove-Item -LiteralPath $clientStaging -Recurse -Force;Write-Host 'Removed inert Phase 3 staging files.'}}catch{Write-Warning("Could not remove inert staging: "+$_.Exception.Message)}
    }else{Write-Warning 'Client has durable pending/apply-transaction state; staging was intentionally left untouched.'}
    try{if(Test-Path -LiteralPath $statePath){Remove-Item -LiteralPath $statePath -Force}}catch{}
    Write-Host 'Phase 3 startup-prewarm test state cleaned.'
}

switch($Action){
    'Prepare'{Prepare-Test}
    'Inspect'{Inspect-Test}
    'Cleanup'{Cleanup-Test}
}
