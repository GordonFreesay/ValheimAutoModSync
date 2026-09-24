param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('Prepare','Next','Cleanup')]
    [string]$Action,
    [Parameter(Mandatory=$true)][string]$ClientBepInEx
)

$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0

$statePath=Join-Path $env:TEMP 'AMS26-Phase3-StaleTrust-State.json'
$clientAms=Join-Path $ClientBepInEx 'AutoModSync'
$forceTrustMarker=Join-Path $clientAms 'phase1-test-force-trust-prompt.once'
$disconnectTrustMarker=Join-Path $clientAms 'phase3-test-disconnect-during-trust.once'
$clientLog=Join-Path $ClientBepInEx 'LogOutput.log'
$trustCaption='Valheim AutoModSync - Trust Server'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AMSPhase3TrustNative {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
}
'@

function Ensure-Root {
    if(-not(Test-Path -LiteralPath $ClientBepInEx -PathType Container)){throw "Client BepInEx root not found: $ClientBepInEx"}
    New-Item -ItemType Directory -Path $clientAms -Force|Out-Null
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

function Native-TrustPromptOpen {
    return ([AMSPhase3TrustNative]::FindWindow($null,$trustCaption) -ne [IntPtr]::Zero)
}

function Save-State($State){
    $State|ConvertTo-Json|Set-Content -LiteralPath $statePath -Encoding UTF8
}

function Prepare-Test {
    Ensure-Root
    if(Test-Path -LiteralPath $statePath -PathType Leaf){
        $existing=Get-Content -LiteralPath $statePath -Raw|ConvertFrom-Json
        throw ("Phase 3 stale-trust suite is already active at stage " + [string]$existing.stage + ". Run Next for the current stage, or Cleanup to reset it.")
    }

    foreach($p in @($forceTrustMarker,$disconnectTrustMarker)){
        try{if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Force}}catch{}
    }

    New-Item -ItemType File -Path $forceTrustMarker -Force|Out-Null
    New-Item -ItemType File -Path $disconnectTrustMarker -Force|Out-Null

    [ordered]@{
        stage=1
        clientLogLines=(Get-LineCount $clientLog)
        preparedUtc=[DateTime]::UtcNow.ToString('o')
    }|ConvertTo-Json|Set-Content -LiteralPath $statePath -Encoding UTF8

    Write-Host ''
    Write-Host 'ARMED [1/2]: protected connection loss while the native trust dialog is open.'
    Write-Host 'Start the dedicated server if needed, then start Valheim and join the server once.'
    Write-Host 'Do NOT click Yes or No. About 1.5 seconds after the trust dialog opens, the development hook will close the protected socket.'
    Write-Host 'The stale native dialog should disappear automatically. Then run -Action Next.'
}

function Next-Test {
    Ensure-Root
    if(-not(Test-Path -LiteralPath $statePath -PathType Leaf)){throw 'No Phase 3 stale-trust state exists. Run Prepare first.'}
    $state=Get-Content -LiteralPath $statePath -Raw|ConvertFrom-Json
    $stage=[int]$state.stage

    if($stage-eq 1){
        $deadline=[DateTime]::UtcNow.AddSeconds(6)
        do{
            $newClient=@(Get-NewLines $clientLog ([int]$state.clientLogLines))
            $clientText=$newClient-join[Environment]::NewLine
            $discarded=$clientText.IndexOf('The protected join was discarded; reconnect to try again.',[StringComparison]::OrdinalIgnoreCase)-ge 0
            $staleReturned=$clientText.IndexOf('AutoModSync DEV TEST ignored stale native trust-dialog result from generation ',[StringComparison]::OrdinalIgnoreCase)-ge 0
            if($discarded-and$staleReturned){break}
            Start-Sleep -Milliseconds 200
        }while([DateTime]::UtcNow-lt$deadline)

        $ok=$true
        Write-Host ''
        Write-Host 'Inspecting Phase 3 stale-trust stage 1/2: disconnect while dialog is open...'

        $checks=@(
            @('AutoModSync DEV FAIL-CLOSED forcing first-contact trust prompt for this verified session.','forced a real native trust prompt for the verified session'),
            @('AutoModSync DEV TEST armed protected-socket loss while the native trust dialog remains open.','armed socket loss only after the trust prompt was started'),
            @('AutoModSync is waiting for first-contact trust confirmation for server fingerprint','native trust session reached the pending state'),
            @('AutoModSync DEV TEST closing the protected socket while the native trust dialog is still open.','closed the recognized socket while trust was still pending'),
            @('The protected join was discarded; reconnect to try again.','recognized connection loss discarded the protected join'),
            @('AutoModSync invalidated the native trust prompt and requested dismissal','connection loss invalidated the prompt and requested native dismissal'),
            @('AutoModSync DEV TEST ignored stale native trust-dialog result from generation ','the old MessageBox actually returned and its stale result was generation-rejected')
        )
        foreach($check in $checks){
            if($clientText.IndexOf($check[0],[StringComparison]::OrdinalIgnoreCase)-ge 0){
                Write-Host("  PASS {0}."-f$check[1])
            }else{
                Write-Host("  FAIL missing evidence: {0}."-f$check[1])
                $ok=$false
            }
        }

        if($clientText.IndexOf('AutoModSync trusted server fingerprint ',[StringComparison]::OrdinalIgnoreCase)-ge 0){
            Write-Host '  FAIL the stale first dialog accepted/persisted trust after its connection died.'
            $ok=$false
        }else{
            Write-Host '  PASS the dead first session never executed trust acceptance.'
        }

        if($clientText.IndexOf('AutoModSync released the original Valheim ServerHandshake',[StringComparison]::OrdinalIgnoreCase)-ge 0){
            Write-Host '  FAIL the dead protected session replayed vanilla ServerHandshake.'
            $ok=$false
        }else{
            Write-Host '  PASS the dead protected session remained fail-closed.'
        }

        if(Native-TrustPromptOpen){
            Write-Host '  WARN an external caption lookup still sees a Trust Server window; visual confirmation and stale-MessageBox return evidence remain authoritative.'
        }else{
            Write-Host '  INFO external caption lookup sees no Trust Server window; stale-MessageBox return evidence is still required.'
        }

        if(Test-Path -LiteralPath $forceTrustMarker){
            Write-Host '  FAIL force-trust marker was not consumed.'
            $ok=$false
        }else{
            Write-Host '  PASS force-trust marker was consumed.'
        }

        if(Test-Path -LiteralPath $disconnectTrustMarker){
            Write-Host '  FAIL disconnect-during-trust marker was not consumed.'
            $ok=$false
        }else{
            Write-Host '  PASS disconnect-during-trust marker was consumed.'
        }

        if(-not$ok){
            Write-Host ''
            Write-Host 'Relevant new client AutoModSync lines:'
            $newClient|Where-Object{$_-match'AutoModSync'}|Select-Object -Last 100|ForEach-Object{Write-Host $_}
            exit 1
        }

        New-Item -ItemType File -Path $forceTrustMarker -Force|Out-Null
        $state.stage=2
        $state.clientLogLines=(Get-LineCount $clientLog)
        Save-State $state

        Write-Host ''
        Write-Host 'PASS stage 1/2.'
        Write-Host 'IMPORTANT: visually confirm the old trust dialog is gone before reconnecting. If any old Trust Server box is still visible, stop here and report it as a failure.'
        Write-Host 'ARMED [2/2]: later connection isolation.'
        Write-Host 'Join the same server again. A NEW native trust dialog should open.'
        Write-Host 'Do NOT click anything in it. Leave it open for at least 3 seconds, then run -Action Next again.'
        return
    }

    if($stage-eq 2){
        Start-Sleep -Milliseconds 500
        $newClient=@(Get-NewLines $clientLog ([int]$state.clientLogLines))
        $clientText=$newClient-join[Environment]::NewLine
        $ok=$true

        Write-Host ''
        Write-Host 'Inspecting Phase 3 stale-trust stage 2/2: stale result cannot act on a later connection...'

        if($clientText.IndexOf('AutoModSync DEV FAIL-CLOSED forcing first-contact trust prompt for this verified session.',[StringComparison]::OrdinalIgnoreCase)-ge 0-and
           $clientText.IndexOf('AutoModSync is waiting for first-contact trust confirmation for server fingerprint',[StringComparison]::OrdinalIgnoreCase)-ge 0){
            Write-Host '  PASS the later connection independently reached a new pending trust generation.'
        }else{
            Write-Host '  FAIL the later connection did not reach a new forced trust prompt.'
            $ok=$false
        }

        if($clientText.IndexOf('AutoModSync trusted server fingerprint ',[StringComparison]::OrdinalIgnoreCase)-ge 0){
            Write-Host '  FAIL trust was accepted on the later connection without a user decision.'
            $ok=$false
        }else{
            Write-Host '  PASS no stale result accepted trust on the later connection.'
        }

        if($clientText.IndexOf('AutoModSync released the original Valheim ServerHandshake',[StringComparison]::OrdinalIgnoreCase)-ge 0){
            Write-Host '  FAIL the later protected connection advanced without a trust decision.'
            $ok=$false
        }else{
            Write-Host '  PASS the later connection remained blocked awaiting its own trust decision.'
        }

        if(Native-TrustPromptOpen){
            Write-Host '  INFO external caption lookup sees the later Trust Server window.'
        }else{
            Write-Host '  INFO external caption lookup does not see the later Trust Server window; this lookup is not authoritative across the game/dialog process boundary.'
        }
        Write-Host '  MANUAL CHECK REQUIRED: visually confirm the NEW Trust Server dialog is still open before accepting this stage.'

        if(Test-Path -LiteralPath $forceTrustMarker){
            Write-Host '  FAIL second force-trust marker was not consumed.'
            $ok=$false
        }else{
            Write-Host '  PASS second force-trust marker was consumed.'
        }

        if(-not$ok){
            Write-Host ''
            Write-Host 'Relevant new client AutoModSync lines:'
            $newClient|Where-Object{$_-match'AutoModSync'}|Select-Object -Last 100|ForEach-Object{Write-Host $_}
            exit 1
        }

        $state.stage=3
        Save-State $state
        Write-Host ''
        Write-Host 'PASS (automated portion): the stale native trust result could not act on the later connection.'
        Write-Host 'FINAL MANUAL GATE: only count Stage 2 as passed if the NEW Trust Server dialog is visibly still open right now.'
        Write-Host 'After visually confirming that, click No on the currently open trust dialog, then close Valheim/server and run -Action Cleanup.'
        return
    }

    throw "Phase 3 stale-trust suite is already complete. Click No on any open test dialog, then run Cleanup."
}

function Cleanup-Test {
    Ensure-Root
    foreach($p in @($forceTrustMarker,$disconnectTrustMarker)){
        try{if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Force}}catch{}
    }
    try{if(Test-Path -LiteralPath $statePath){Remove-Item -LiteralPath $statePath -Force}}catch{}
    Write-Host 'Phase 3 stale-trust test state cleaned.'
}

switch($Action){
    'Prepare'{Prepare-Test}
    'Next'{Next-Test}
    'Cleanup'{Cleanup-Test}
}
