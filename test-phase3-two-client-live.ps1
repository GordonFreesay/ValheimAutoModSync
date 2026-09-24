param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('Prepare','Inspect','Cleanup')]
    [string]$Action,
    [Parameter(Mandatory=$true)][string]$ServerBepInEx
)

$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0

$statePath=Join-Path $env:TEMP 'AMS26-Phase3-TwoClient-State.json'
$serverAms=Join-Path $ServerBepInEx 'AutoModSync'
$fixtureDir=Join-Path (Join-Path (Join-Path $serverAms 'ClientPayload') 'plugins') '__AMS_PHASE3_TWOCLIENT__'
$fixture=Join-Path $fixtureDir 'payload.bin'
$disablePrewarmMarker=Join-Path $serverAms 'phase3-test-disable-prewarm.once'
$delayMarker=Join-Path $serverAms 'phase3-test-two-client-delay.once'
$serverLog=Join-Path $ServerBepInEx 'LogOutput.log'

function Ensure-Root {
    if(-not(Test-Path -LiteralPath $ServerBepInEx -PathType Container)){throw "Server BepInEx root not found: $ServerBepInEx"}
    New-Item -ItemType Directory -Path $serverAms -Force|Out-Null
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

function Write-RandomFixture {
    New-Item -ItemType Directory -Path $fixtureDir -Force|Out-Null
    $bytes=New-Object byte[] (4*1024*1024)
    $rng=[System.Security.Cryptography.RandomNumberGenerator]::Create()
    try{$rng.GetBytes($bytes)}finally{$rng.Dispose()}
    [System.IO.File]::WriteAllBytes($fixture,$bytes)
}

function Save-State($State){
    $State|ConvertTo-Json|Set-Content -LiteralPath $statePath -Encoding UTF8
}

function Prepare-Test {
    Ensure-Root
    if(Test-Path -LiteralPath $statePath -PathType Leaf){
        throw 'The two-client suite is already armed. Run Inspect after the two joins, or Cleanup to reset it.'
    }

    foreach($p in @($disablePrewarmMarker,$delayMarker)){
        try{if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Force}}catch{}
    }
    try{if(Test-Path -LiteralPath $fixtureDir){Remove-Item -LiteralPath $fixtureDir -Recurse -Force}}catch{}

    Write-RandomFixture
    $fixtureSha=(Get-FileHash -Algorithm SHA256 -LiteralPath $fixture).Hash.ToLowerInvariant()
    New-Item -ItemType File -Path $disablePrewarmMarker -Force|Out-Null
    New-Item -ItemType File -Path $delayMarker -Force|Out-Null

    $state=[ordered]@{
        serverLogLines=(Get-LineCount $serverLog)
        fixtureSha=$fixtureSha
        preparedUtc=[DateTime]::UtcNow.ToString('o')
    }
    Save-State $state

    Write-Host ''
    Write-Host 'ARMED: real two-client single-flight validation.'
    Write-Host 'The server has one new 4 MiB random client payload, startup prewarm is disabled for this process, and the first real bundle build will pause 10 seconds on a worker thread.'
    Write-Host 'Start the dedicated server. Then connect BOTH already-baselined 2.6 clients as close together as practical (within the 10-second build window).'
    Write-Host 'Let both synchronization attempts finish/restart. Then run -Action Inspect.'
}

function Inspect-Test {
    Ensure-Root
    if(-not(Test-Path -LiteralPath $statePath -PathType Leaf)){throw 'No two-client test state exists. Run Prepare first.'}
    $state=Get-Content -LiteralPath $statePath -Raw|ConvertFrom-Json

    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    do{
        $newServer=@(Get-NewLines $serverLog ([int]$state.serverLogLines))
        $serverText=$newServer-join[Environment]::NewLine
        $readyCount=[regex]::Matches($serverText,'AutoModSync bundle ready: cache=(?:MISS|WAIT-HIT|HIT), key=[0-9a-fA-F]+, sha256=[0-9a-fA-F]{64}, compressedBytes=\d+, files=1,').Count
        if($readyCount-ge 2){break}
        Start-Sleep -Milliseconds 250
    }while([DateTime]::UtcNow-lt$deadline)

    $ok=$true
    Write-Host ''
    Write-Host 'Inspecting real two-client Phase 3 overlap...'

    if($serverText.IndexOf('AutoModSync DEV TEST armed a 10000 ms background bundle-build delay for real two-client overlap validation.',[StringComparison]::OrdinalIgnoreCase)-ge 0){
        Write-Host '  PASS the real-client overlap delay was armed.'
    }else{
        Write-Host '  FAIL the real-client overlap delay marker was not consumed.'
        $ok=$false
    }

    if($serverText.IndexOf('AutoModSync DEV TEST delaying one bundle build by 10000 ms to force an overlapping identical acquisition.',[StringComparison]::OrdinalIgnoreCase)-ge 0){
        Write-Host '  PASS the first production bundle build actually entered the 10-second worker delay.'
    }else{
        Write-Host '  FAIL missing evidence that the delayed production build ran.'
        $ok=$false
    }

    if($serverText.IndexOf('AutoModSync DEV TEST single-flight follower result:',[StringComparison]::OrdinalIgnoreCase)-ge 0){
        Write-Host '  FAIL a synthetic in-process follower participated; this run cannot count as the real two-client qualification.'
        $ok=$false
    }else{
        Write-Host '  PASS no synthetic follower participated.'
    }

    $admissionCount=[regex]::Matches($serverText,'AutoModSync bundle request admitted to scheduler: .*requestedFiles=1,').Count
    if($admissionCount-ge 2){
        Write-Host("  PASS observed at least two independent scheduler admissions for the same one-file delta ({0})."-f$admissionCount)
    }else{
        Write-Host("  FAIL expected two scheduler admissions for the one-file delta; observed {0}."-f$admissionCount)
        $ok=$false
    }

    $waitMatch=[regex]::Match($serverText,'AutoModSync bundle cache WAIT key=([0-9a-fA-F]+); another client is building the identical artifact.')
    if($waitMatch.Success){
        $waitKey=$waitMatch.Groups[1].Value.ToLowerInvariant()
        Write-Host("  PASS the second real client entered single-flight WAIT for key {0}."-f$waitKey)
    }else{
        $waitKey=''
        Write-Host '  FAIL no real-client WAIT was observed. The clients may not have overlapped inside the build window.'
        $ok=$false
    }

    $pattern='AutoModSync bundle ready: cache=(MISS|WAIT-HIT|HIT), key=([0-9a-fA-F]+), sha256=([0-9a-fA-F]{64}), compressedBytes=(\d+), files=1,'
    $matches=[regex]::Matches($serverText,$pattern)
    $miss=$null
    $waitHit=$null
    foreach($m in $matches){
        $entry=[pscustomobject]@{
            Status=$m.Groups[1].Value
            Key=$m.Groups[2].Value.ToLowerInvariant()
            Sha=$m.Groups[3].Value.ToLowerInvariant()
            Bytes=[int64]$m.Groups[4].Value
        }
        if($entry.Status-eq'MISS'-and$null-eq$miss){$miss=$entry}
        if($entry.Status-eq'WAIT-HIT'-and$null-eq$waitHit){$waitHit=$entry}
    }

    if($null-ne$miss){
        Write-Host("  PASS builder returned cache=MISS for key {0}."-f$miss.Key)
    }else{
        Write-Host '  FAIL missing builder cache=MISS result.'
        $ok=$false
    }

    if($null-ne$waitHit){
        Write-Host("  PASS follower returned cache=WAIT-HIT after waiting on key {0}."-f$waitHit.Key)
    }else{
        Write-Host '  FAIL missing real-client cache=WAIT-HIT result.'
        $ok=$false
    }

    if($null-ne$miss-and$null-ne$waitHit-and$miss.Key-eq$waitHit.Key-and$miss.Sha-eq$waitHit.Sha-and$miss.Bytes-eq$waitHit.Bytes){
        Write-Host '  PASS both real clients received the exact same immutable cache key/SHA-256/compressed size.'
    }elseif($null-ne$miss-and$null-ne$waitHit){
        Write-Host '  FAIL MISS and WAIT-HIT artifact identities differ.'
        $ok=$false
    }

    if($null-ne$miss){
        $buildCount=[regex]::Matches($serverText,('AutoModSync bundle cache MISS key='+[regex]::Escape($miss.Key))).Count
        if($buildCount-eq 1){
            Write-Host '  PASS exactly one ZIP build was published for the shared key.'
        }else{
            Write-Host("  FAIL expected one ZIP build for the shared key; observed {0}."-f$buildCount)
            $ok=$false
        }
    }

    $completeCount=[regex]::Matches($serverText,'AutoModSync scheduler transfer complete:').Count
    if($completeCount-ge 2){
        Write-Host("  PASS at least two real scheduled transfers completed ({0})."-f$completeCount)
    }else{
        Write-Host("  WARN only {0} transfer-complete line(s) are visible so far; cache overlap can still be evaluated independently."-f$completeCount)
    }

    if(Test-Path -LiteralPath $delayMarker){
        Write-Host '  FAIL two-client delay marker still exists.'
        $ok=$false
    }else{
        Write-Host '  PASS two-client delay marker was consumed.'
    }

    if(-not$ok){
        Write-Host ''
        Write-Host 'Relevant new server AutoModSync lines:'
        $newServer|Where-Object{$_-match'AutoModSync'}|Select-Object -Last 160|ForEach-Object{Write-Host $_}
        exit 1
    }

    Write-Host ''
    Write-Host 'PASS: two independent network clients overlapped one real production bundle build; one built MISS, one waited WAIT-HIT, and both used the same immutable artifact.'
    Write-Host 'Stop both Valheim clients and the dedicated server, then run -Action Cleanup.'
}

function Cleanup-Test {
    Ensure-Root
    foreach($p in @($disablePrewarmMarker,$delayMarker)){
        try{if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Force}}catch{}
    }
    try{if(Test-Path -LiteralPath $fixtureDir){Remove-Item -LiteralPath $fixtureDir -Recurse -Force}}catch{}
    try{if(Test-Path -LiteralPath $statePath){Remove-Item -LiteralPath $statePath -Force}}catch{}
    Write-Host 'Phase 3 real two-client test state cleaned.'
    Write-Host 'The test payload was removed from the server source. Normal ownership reconciliation can remove the now-stale client copy on a later join.'
}

switch($Action){
    'Prepare'{Prepare-Test}
    'Inspect'{Inspect-Test}
    'Cleanup'{Cleanup-Test}
}
