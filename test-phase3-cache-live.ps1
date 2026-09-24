param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('StartSuite','Next','Cleanup')]
    [string]$Action,
    [Parameter(Mandatory=$true)][string]$ServerBepInEx,
    [Parameter(Mandatory=$true)][string]$ClientBepInEx
)

$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0

$statePath=Join-Path $env:TEMP 'AMS26-Phase3-CacheLifecycle-State.json'
$serverAms=Join-Path $ServerBepInEx 'AutoModSync'
$clientAms=Join-Path $ClientBepInEx 'AutoModSync'
$serverFixtureDir=Join-Path (Join-Path (Join-Path $serverAms 'ClientPayload') 'plugins') '__AMS_PHASE3_CACHE__'
$serverFixture=Join-Path $serverFixtureDir 'payload.bin'
$clientFixtureDir=Join-Path (Join-Path $ClientBepInEx 'plugins') '__AMS_PHASE3_CACHE__'
$clientFixture=Join-Path $clientFixtureDir 'payload.bin'
$disablePrewarmMarker=Join-Path $serverAms 'phase3-test-disable-prewarm.once'
$singleFlightMarker=Join-Path $serverAms 'phase3-test-singleflight-follower.once'
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

function Write-Fixture([char]$Fill){
    New-Item -ItemType Directory -Path $serverFixtureDir -Force|Out-Null
    $text=New-Object string($Fill,262144)
    [IO.File]::WriteAllText($serverFixture,$text,(New-Object System.Text.UTF8Encoding($false)))
}

function Get-ReadyArtifact([string]$Text,[string]$ExpectedStatus){
    $pattern='AutoModSync bundle ready: cache='+[regex]::Escape($ExpectedStatus)+', key=([0-9a-fA-F]+), sha256=([0-9a-fA-F]{64}), compressedBytes=(\d+), files=1,'
    $m=[regex]::Match($Text,$pattern)
    if(-not$m.Success){return $null}
    return [pscustomobject]@{
        Status=$ExpectedStatus
        Key=$m.Groups[1].Value.ToLowerInvariant()
        Sha=$m.Groups[2].Value.ToLowerInvariant()
        Bytes=[int64]$m.Groups[3].Value
    }
}

function Assert-ClientStoppedAfterHeader([string]$ClientText,[ref]$Ok){
    if($ClientText.IndexOf('AutoModSync DEV TEST Phase 3 stopping after validated bundle header before payload transfer.',[StringComparison]::OrdinalIgnoreCase)-ge 0){
        Write-Host '  PASS client validated the bundle header and stopped before payload transfer.'
    }else{
        Write-Host '  FAIL missing client stop-after-header evidence.'
        $Ok.Value=$false
    }

    if($ClientText.IndexOf('AutoModSync released the original Valheim ServerHandshake',[StringComparison]::OrdinalIgnoreCase)-ge 0){
        Write-Host '  FAIL vanilla ServerHandshake was replayed during the deliberately stopped protected sync.'
        $Ok.Value=$false
    }else{
        Write-Host '  PASS protected test stop did not replay vanilla ServerHandshake.'
    }

    if(Test-Path -LiteralPath $clientFixture -PathType Leaf){
        Write-Host("  FAIL reserved cache fixture reached the live client tree: {0}"-f$clientFixture)
        $Ok.Value=$false
    }else{
        Write-Host '  PASS reserved cache fixture did not reach the live client tree.'
    }

    if(Test-Path -LiteralPath $clientPending -PathType Leaf){
        Write-Host("  FAIL durable pending apply exists: {0}"-f$clientPending)
        $Ok.Value=$false
    }else{
        Write-Host '  PASS no durable pending apply was accepted.'
    }
}

function Save-State($State){
    $State|ConvertTo-Json|Set-Content -LiteralPath $statePath -Encoding UTF8
}

function Start-Suite {
    Ensure-Roots
    if(Test-Path -LiteralPath $statePath -PathType Leaf){throw 'Phase 3 cache lifecycle state already exists. Run Cleanup first.'}
    if(Test-Path -LiteralPath $clientPending -PathType Leaf){throw "Client has an existing pending apply: $clientPending"}
    if(Test-Path -LiteralPath $clientReconnect -PathType Leaf){throw "Client has an existing AutoModSync reconnect token: $clientReconnect"}
    if(Test-Path -LiteralPath $clientTransaction){throw "Client has an existing apply transaction: $clientTransaction"}
    if(Test-Path -LiteralPath $clientFixture -PathType Leaf){throw "Reserved client fixture already exists: $clientFixture"}

    foreach($p in @($disablePrewarmMarker,$singleFlightMarker,$stopAfterHeaderMarker)){
        try{if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Force}}catch{}
    }
    try{if(Test-Path -LiteralPath $serverFixtureDir){Remove-Item -LiteralPath $serverFixtureDir -Recurse -Force}}catch{}
    try{if(Test-Path -LiteralPath $clientStaging){Remove-Item -LiteralPath $clientStaging -Recurse -Force}}catch{}

    Write-Fixture 'A'
    New-Item -ItemType File -Path $disablePrewarmMarker -Force|Out-Null
    New-Item -ItemType File -Path $stopAfterHeaderMarker -Force|Out-Null

    $state=[ordered]@{
        stage=1
        clientLogLines=(Get-LineCount $clientLog)
        serverLogLines=(Get-LineCount $serverLog)
        firstKey=''
        firstSha=''
        firstBytes=0
        preparedUtc=[DateTime]::UtcNow.ToString('o')
    }
    Save-State $state

    Write-Host ''
    Write-Host 'ARMED [1/3]: ordinary cache MISS.'
    Write-Host 'Start the dedicated server and Valheim, join once, and wait for the protected join to stop after the bundle header.'
    Write-Host 'Then run the same command with -Action Next.'
}

function Next-Stage {
    Ensure-Roots
    if(-not(Test-Path -LiteralPath $statePath -PathType Leaf)){throw 'No Phase 3 cache lifecycle state exists. Run StartSuite first.'}
    $state=Get-Content -LiteralPath $statePath -Raw|ConvertFrom-Json
    $stage=[int]$state.stage

    $newClient=@(Get-NewLines $clientLog ([int]$state.clientLogLines))
    $newServer=@(Get-NewLines $serverLog ([int]$state.serverLogLines))
    $clientText=$newClient-join[Environment]::NewLine
    $serverText=$newServer-join[Environment]::NewLine
    $ok=$true

    Write-Host ''
    if($stage-eq 1){
        Write-Host 'Inspecting Phase 3 cache lifecycle stage 1/3: first changed-set MISS...'

        if($serverText.IndexOf('AutoModSync DEV TEST disabled startup bundle prewarm for this server process.',[StringComparison]::OrdinalIgnoreCase)-ge 0){
            Write-Host '  PASS startup prewarm was disabled for this ordinary-cache process.'
        }else{
            Write-Host '  FAIL missing startup-prewarm-disable evidence.'
            $ok=$false
        }

        $artifact=Get-ReadyArtifact $serverText 'MISS'
        if($null-ne$artifact){
            Write-Host("  PASS first one-file changed set produced cache=MISS: key={0}, sha256={1}, bytes={2}."-f$artifact.Key,$artifact.Sha,$artifact.Bytes)
        }else{
            Write-Host '  FAIL missing one-file cache=MISS artifact evidence.'
            $ok=$false
        }

        if($serverText.IndexOf('ZIP build=',[StringComparison]::OrdinalIgnoreCase)-ge 0-and$serverText.IndexOf('ZIP SHA-256=',[StringComparison]::OrdinalIgnoreCase)-ge 0){
            Write-Host '  PASS MISS logged ZIP-build and SHA-256 preparation timings.'
        }else{
            Write-Host '  FAIL MISS timing evidence is incomplete.'
            $ok=$false
        }

        Assert-ClientStoppedAfterHeader $clientText ([ref]$ok)

        if(-not$ok){
            Write-Host ''
            Write-Host 'Relevant new server AutoModSync lines:'
            $newServer|Where-Object{$_-match'AutoModSync'}|Select-Object -Last 80|ForEach-Object{Write-Host $_}
            Write-Host ''
            Write-Host 'Relevant new client AutoModSync lines:'
            $newClient|Where-Object{$_-match'AutoModSync'}|Select-Object -Last 80|ForEach-Object{Write-Host $_}
            exit 1
        }

        $state.stage=2
        $state.firstKey=$artifact.Key
        $state.firstSha=$artifact.Sha
        $state.firstBytes=$artifact.Bytes
        $state.clientLogLines=(Get-LineCount $clientLog)
        $state.serverLogLines=(Get-LineCount $serverLog)
        Save-State $state
        New-Item -ItemType File -Path $stopAfterHeaderMarker -Force|Out-Null

        Write-Host ''
        Write-Host 'PASS stage 1/3.'
        Write-Host 'ARMED [2/3]: identical changed-set HIT.'
        Write-Host 'Do NOT restart the dedicated server. Join the same server once more, then run -Action Next.'
        return
    }

    if($stage-eq 2){
        Write-Host 'Inspecting Phase 3 cache lifecycle stage 2/3: identical-set HIT...'

        $artifact=Get-ReadyArtifact $serverText 'HIT'
        if($null-ne$artifact){
            Write-Host("  PASS second identical request produced cache=HIT: key={0}."-f$artifact.Key)
        }else{
            Write-Host '  FAIL missing cache=HIT artifact evidence.'
            $ok=$false
        }

        if($null-ne$artifact-and$artifact.Key-eq[string]$state.firstKey-and$artifact.Sha-eq[string]$state.firstSha-and$artifact.Bytes-eq[int64]$state.firstBytes){
            Write-Host '  PASS HIT reused the exact same cache key, bundle SHA-256, and compressed byte size.'
        }else{
            Write-Host '  FAIL HIT artifact identity differs from the first MISS.'
            $ok=$false
        }

        if($serverText.IndexOf(('AutoModSync bundle cache MISS key='+[string]$state.firstKey),[StringComparison]::OrdinalIgnoreCase)-ge 0){
            Write-Host '  FAIL identical request rebuilt the artifact instead of reusing it.'
            $ok=$false
        }else{
            Write-Host '  PASS identical request performed no second ZIP build.'
        }

        Assert-ClientStoppedAfterHeader $clientText ([ref]$ok)

        if(-not$ok){
            Write-Host ''
            Write-Host 'Relevant new server AutoModSync lines:'
            $newServer|Where-Object{$_-match'AutoModSync'}|Select-Object -Last 80|ForEach-Object{Write-Host $_}
            Write-Host ''
            Write-Host 'Relevant new client AutoModSync lines:'
            $newClient|Where-Object{$_-match'AutoModSync'}|Select-Object -Last 80|ForEach-Object{Write-Host $_}
            exit 1
        }

        Write-Host ''
        Write-Host 'PASS stage 2/3.'
        Write-Host 'Changing the reserved source bytes without changing its size, then waiting for the normal manifest cache to expire...'
        Write-Fixture 'B'
        Start-Sleep -Seconds 6

        $state.stage=3
        $state.clientLogLines=(Get-LineCount $clientLog)
        $state.serverLogLines=(Get-LineCount $serverLog)
        Save-State $state
        New-Item -ItemType File -Path $singleFlightMarker -Force|Out-Null
        New-Item -ItemType File -Path $stopAfterHeaderMarker -Force|Out-Null

        Write-Host 'ARMED [3/3]: changed-content MISS + overlapping identical single-flight follower.'
        Write-Host 'Keep the dedicated server running. Join once more, then run -Action Next.'
        return
    }

    if($stage-eq 3){
        Write-Host 'Inspecting Phase 3 cache lifecycle stage 3/3: changed key + WAIT/WAIT-HIT...'

        $deadline=[DateTime]::UtcNow.AddSeconds(5)
        do{
            $newServer=@(Get-NewLines $serverLog ([int]$state.serverLogLines))
            $serverText=$newServer-join[Environment]::NewLine
            if($serverText.IndexOf('AutoModSync DEV TEST single-flight follower result:',[StringComparison]::OrdinalIgnoreCase)-ge 0){break}
            Start-Sleep -Milliseconds 200
        }while([DateTime]::UtcNow-lt$deadline)

        $artifact=Get-ReadyArtifact $serverText 'MISS'
        if($null-ne$artifact){
            Write-Host("  PASS changed same-size source produced a new cache=MISS: key={0}."-f$artifact.Key)
        }else{
            Write-Host '  FAIL missing changed-content cache=MISS evidence.'
            $ok=$false
        }

        if($null-ne$artifact-and$artifact.Key-ne[string]$state.firstKey){
            Write-Host '  PASS changed requested content changed the content-derived cache key.'
        }else{
            Write-Host '  FAIL changed requested content reused the old cache key.'
            $ok=$false
        }

        if($null-ne$artifact){
            $missCount=[regex]::Matches($serverText,('AutoModSync bundle cache MISS key='+[regex]::Escape($artifact.Key))).Count
            if($missCount-eq 1){
                Write-Host '  PASS overlapping acquisitions produced exactly one build for the new key.'
            }else{
                Write-Host("  FAIL expected exactly one build for the overlapping key; observed {0} MISS lines."-f$missCount)
                $ok=$false
            }

            if($serverText.IndexOf(('AutoModSync bundle cache WAIT key='+$artifact.Key),[StringComparison]::OrdinalIgnoreCase)-ge 0){
                Write-Host '  PASS follower logged WAIT while the identical artifact was being built.'
            }else{
                Write-Host '  FAIL missing single-flight WAIT evidence.'
                $ok=$false
            }

            $followerPattern='AutoModSync DEV TEST single-flight follower result: cache=WAIT-HIT, key='+[regex]::Escape($artifact.Key)+', sha256=([0-9a-fA-F]{64}), compressedBytes=(\d+), wait=([0-9.]+) s\.'
            $fm=[regex]::Match($serverText,$followerPattern)
            if($fm.Success-and$fm.Groups[1].Value.ToLowerInvariant()-eq$artifact.Sha-and[int64]$fm.Groups[2].Value-eq$artifact.Bytes){
                Write-Host("  PASS follower returned WAIT-HIT on the exact same immutable artifact after {0}s."-f$fm.Groups[3].Value)
            }else{
                Write-Host '  FAIL follower did not return WAIT-HIT with the exact same SHA-256/size.'
                $ok=$false
            }
        }

        $newClient=@(Get-NewLines $clientLog ([int]$state.clientLogLines))
        $clientText=$newClient-join[Environment]::NewLine
        Assert-ClientStoppedAfterHeader $clientText ([ref]$ok)

        if(Test-Path -LiteralPath $singleFlightMarker){
            Write-Host '  FAIL single-flight follower marker was not consumed.'
            $ok=$false
        }else{
            Write-Host '  PASS single-flight follower marker was consumed.'
        }

        if(-not$ok){
            Write-Host ''
            Write-Host 'Relevant new server AutoModSync lines:'
            $newServer|Where-Object{$_-match'AutoModSync'}|Select-Object -Last 100|ForEach-Object{Write-Host $_}
            Write-Host ''
            Write-Host 'Relevant new client AutoModSync lines:'
            $newClient|Where-Object{$_-match'AutoModSync'}|Select-Object -Last 80|ForEach-Object{Write-Host $_}
            exit 1
        }

        Write-Host ''
        Write-Host 'PASS: Phase 3 ordinary cache lifecycle passed MISS -> identical HIT -> changed-key MISS with overlapping WAIT/WAIT-HIT single-flight sharing.'
        Write-Host 'Stop Valheim and the dedicated server, then run -Action Cleanup.'
        return
    }

    throw "Unknown Phase 3 cache lifecycle stage: $stage"
}

function Cleanup-Suite {
    Ensure-Roots
    foreach($p in @($disablePrewarmMarker,$singleFlightMarker,$stopAfterHeaderMarker)){
        try{if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Force}}catch{}
    }
    try{if(Test-Path -LiteralPath $serverFixtureDir){Remove-Item -LiteralPath $serverFixtureDir -Recurse -Force}}catch{}
    if((-not(Test-Path -LiteralPath $clientPending))-and(-not(Test-Path -LiteralPath $clientTransaction))){
        try{if(Test-Path -LiteralPath $clientStaging){Remove-Item -LiteralPath $clientStaging -Recurse -Force}}catch{}
    }
    if(Test-Path -LiteralPath $clientFixture -PathType Leaf){
        Write-Warning "Reserved LIVE client fixture exists and was not deleted automatically: $clientFixture"
    }
    try{if(Test-Path -LiteralPath $statePath){Remove-Item -LiteralPath $statePath -Force}}catch{}
    Write-Host 'Phase 3 ordinary cache lifecycle test state cleaned.'
}

switch($Action){
    'StartSuite'{Start-Suite}
    'Next'{Next-Stage}
    'Cleanup'{Cleanup-Suite}
}
