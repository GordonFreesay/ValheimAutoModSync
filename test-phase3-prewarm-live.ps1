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
$serverZeroTtlMarker=Join-Path $serverAms 'phase3-test-cache-seconds-zero.once'
$nearlyBareMarker=Join-Path $clientAms 'phase3-test-emulate-nearly-bare.once'
$stopAfterHeaderMarker=Join-Path $clientAms 'phase3-test-stop-after-bundle-header.once'
$clientPending=Join-Path $clientAms 'pending.txt'
$clientReconnect=Join-Path $clientAms 'reconnect.txt'
$clientTransaction=Join-Path $clientAms 'apply-transaction'
$clientStaging=Join-Path $clientAms 'staging'
$clientLog=Join-Path $ClientBepInEx 'LogOutput.log'
$serverLog=Join-Path $ServerBepInEx 'LogOutput.log'

function Ensure-Roots {
    if(-not(Test-Path -LiteralPath $ServerBepInEx -PathType Container)){throw "Server BepInEx root not found: $ServerBepInEx"}
    if(-not(Test-Path -LiteralPath $ClientBepInEx -PathType Container)){throw "Client BepInEx root not found: $ClientBepInEx"}
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

function Prepare-Test {
    Ensure-Roots
    if(Test-Path -LiteralPath $clientPending -PathType Leaf){throw "Client has an existing pending apply: $clientPending"}
    if(Test-Path -LiteralPath $clientReconnect -PathType Leaf){throw "Client has an existing AutoModSync reconnect token: $clientReconnect"}
    if(Test-Path -LiteralPath $clientTransaction){throw "Client has an existing apply transaction: $clientTransaction"}

    foreach($p in @($serverZeroTtlMarker,$nearlyBareMarker,$stopAfterHeaderMarker)){
        try{if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Force}}catch{}
    }
    if(Test-Path -LiteralPath $clientStaging){Remove-Item -LiteralPath $clientStaging -Recurse -Force}

    New-Item -ItemType File -Path $serverZeroTtlMarker -Force|Out-Null
    New-Item -ItemType File -Path $nearlyBareMarker -Force|Out-Null
    New-Item -ItemType File -Path $stopAfterHeaderMarker -Force|Out-Null

    [ordered]@{
        clientLogLines=(Get-LineCount $clientLog)
        serverLogLines=(Get-LineCount $serverLog)
        preparedUtc=[DateTime]::UtcNow.ToString('o')
    }|ConvertTo-Json|Set-Content -LiteralPath $statePath -Encoding UTF8

    Write-Host ''
    Write-Host 'ARMED: Phase 3 startup-prewarm retention test.'
    Write-Host 'The server will use an in-memory development-only BundleCacheSeconds=0 override for this process; persistent config is not changed.'
    Write-Host '1. Start ONLY the dedicated server.'
    Write-Host '2. Wait until the server logs "AutoModSync fresh-client bundle prewarm ready".'
    Write-Host '3. Start Valheim and join once.'
    Write-Host '4. The client will stop immediately after validating the bundle header, so the 313 MB payload is not downloaded.'
    Write-Host '5. Run -Action Inspect.'
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

    if($serverText.IndexOf('AutoModSync DEV TEST forcing effective BundleCacheSeconds=0 for this server process.',[StringComparison]::OrdinalIgnoreCase)-ge 0){
        Write-Host '  PASS server consumed the zero-TTL process override before cache activity.'
    }else{
        Write-Host '  FAIL missing zero-TTL process-override evidence.'
        $ok=$false
    }

    $prewarmMatch=[regex]::Match($serverText,'AutoModSync fresh-client bundle prewarm ready: cache=MISS, key=([0-9a-fA-F]+)')
    if($prewarmMatch.Success){
        $key=$prewarmMatch.Groups[1].Value.ToLowerInvariant()
        Write-Host("  PASS startup prewarm published one MISS baseline: key={0}."-f$key)
    }else{
        $key=''
        Write-Host '  FAIL missing startup prewarm MISS/ready evidence.'
        $ok=$false
    }

    $hasMiss=$serverText.IndexOf('AutoModSync bundle cache MISS key=',[StringComparison]::OrdinalIgnoreCase)-ge 0
    $hasBuild=$serverText.IndexOf('ZIP build=',[StringComparison]::OrdinalIgnoreCase)-ge 0
    $hasHash=$serverText.IndexOf('ZIP SHA-256=',[StringComparison]::OrdinalIgnoreCase)-ge 0
    if($hasMiss-and$hasBuild-and$hasHash){
        Write-Host '  PASS startup build logged ZIP-build and SHA-256 preparation timings.'
    }else{
        Write-Host '  FAIL missing ZIP build/SHA-256 timing evidence.'
        $ok=$false
    }

    $matchingHit=$false
    foreach($m in [regex]::Matches($serverText,'AutoModSync bundle ready: cache=HIT, key=([0-9a-fA-F]+)')){
        if(($key.Length-eq 0)-or($m.Groups[1].Value.ToLowerInvariant()-eq$key)){$matchingHit=$true;break}
    }
    if($matchingHit){
        Write-Host '  PASS first real nearly-bare request reused the same startup artifact with cache=HIT.'
    }else{
        Write-Host '  FAIL no matching cache=HIT for the startup baseline was observed.'
        $ok=$false
    }

    $pinMatch=[regex]::Match($serverText,'AutoModSync startup-pinned bundle survived ordinary cache TTL until first real client use: idle=([0-9.]+) s, BundleCacheSeconds=0, key=([0-9a-fA-F]+)')
    if($pinMatch.Success-and(($key.Length-eq 0)-or($pinMatch.Groups[2].Value.ToLowerInvariant()-eq$key))){
        Write-Host("  PASS startup pin retained the baseline despite effective TTL=0: idle={0}s."-f$pinMatch.Groups[1].Value)
    }else{
        Write-Host '  FAIL missing explicit startup-pin retention evidence for effective TTL=0.'
        $ok=$false
    }

    if($key.Length-gt 0){
        $missCount=[regex]::Matches($serverText,('AutoModSync bundle cache MISS key='+[regex]::Escape($key))).Count
        if($missCount-eq 1){
            Write-Host '  PASS baseline key was built exactly once; no join-time rebuild occurred.'
        }else{
            Write-Host("  FAIL expected exactly one MISS for baseline key {0}, observed {1}."-f$key,$missCount)
            $ok=$false
        }
    }

    if($clientText.IndexOf('AutoModSync DEV TEST emulating a nearly-bare client for Phase 3 startup-prewarm validation',[StringComparison]::OrdinalIgnoreCase)-ge 0){
        Write-Host '  PASS client consumed the nearly-bare emulator marker.'
    }else{
        Write-Host '  FAIL client did not consume the nearly-bare emulator marker.'
        $ok=$false
    }

    if($clientText.IndexOf('AutoModSync DEV TEST Phase 3 stopping after validated bundle header before payload transfer.',[StringComparison]::OrdinalIgnoreCase)-ge 0){
        Write-Host '  PASS client validated the bundle header and stopped before payload transfer.'
    }else{
        Write-Host '  FAIL missing stop-after-header evidence.'
        $ok=$false
    }

    if($clientText.IndexOf('AutoModSync released the original Valheim ServerHandshake',[StringComparison]::OrdinalIgnoreCase)-ge 0){
        Write-Host '  FAIL vanilla ServerHandshake was replayed during the deliberately stopped protected sync.'
        $ok=$false
    }else{
        Write-Host '  PASS protected test stop did not replay vanilla ServerHandshake.'
    }

    if(Test-Path -LiteralPath $clientPending -PathType Leaf){
        Write-Host("  FAIL durable pending apply exists: {0}"-f$clientPending)
        $ok=$false
    }else{
        Write-Host '  PASS no durable pending apply was accepted.'
    }

    foreach($pair in @(
        @($serverZeroTtlMarker,'server zero-TTL marker'),
        @($nearlyBareMarker,'client nearly-bare marker'),
        @($stopAfterHeaderMarker,'client stop-after-header marker')
    )){
        if(Test-Path -LiteralPath $pair[0]){
            Write-Host("  FAIL {0} was not consumed."-f$pair[1])
            $ok=$false
        }else{
            Write-Host("  PASS {0} was consumed."-f$pair[1])
        }
    }

    if($ok){
        Write-Host ''
        Write-Host 'PASS: startup prewarm was built once, remained pinned with effective BundleCacheSeconds=0, and the first real nearly-bare request reused it without a join-time rebuild.'
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
    foreach($p in @($serverZeroTtlMarker,$nearlyBareMarker,$stopAfterHeaderMarker)){
        try{if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Force}}catch{}
    }
    if((-not(Test-Path -LiteralPath $clientPending))-and(-not(Test-Path -LiteralPath $clientTransaction))){
        try{if(Test-Path -LiteralPath $clientStaging){Remove-Item -LiteralPath $clientStaging -Recurse -Force;Write-Host 'Removed inert Phase 3 staging files.'}}catch{Write-Warning("Could not remove inert staging: "+$_.Exception.Message)}
    }else{
        Write-Warning 'Client has durable pending/apply-transaction state; staging was intentionally left untouched.'
    }
    try{if(Test-Path -LiteralPath $statePath){Remove-Item -LiteralPath $statePath -Force}}catch{}
    Write-Host 'Phase 3 startup-prewarm test state cleaned.'
}

switch($Action){
    'Prepare'{Prepare-Test}
    'Inspect'{Inspect-Test}
    'Cleanup'{Cleanup-Test}
}
