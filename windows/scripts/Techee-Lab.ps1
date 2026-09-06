<#
.SYNOPSIS
    Sets up and runs a two-machine Techee acceptance session.

.DESCRIPTION
    The manual procedure in docs/W5_CONTROLLER_HARNESS.md works, but it asks an operator
    to copy two base64 keys between machines by hand, find a LAN address, open a firewall
    port, and remember which flags belong to which role. Every one of those is a chance to
    spend an hour debugging a typo rather than the thing under test.

    This wraps all of it. It does not add capability — every action shells out to
    techee-host or techee-ctl, and anything it can do can be done by hand.

    WHAT IT DELIBERATELY DOES NOT DO: it never fabricates trust. `trust` imports a peer
    card that a human copied from the other machine, and the underlying verbs still print
    their TEST-SEEDED warning. A card is a convenience for moving a public key, not a
    pairing exchange, and it is not evidence for matrix row D3.

.PARAMETER Role
    Host or Controller. Decides which binary is used and which verbs apply.

.PARAMETER Action
    build   - build the solution
    broker  - start the signaling broker, open the port, print the URL (Host only)
    card    - write this machine's peer card, to copy to the other machine
    trust   - import the other machine's peer card and seed trust
    run     - Host: serve.  Controller: dial and open the viewer.
    status  - show what is configured and what is missing

.EXAMPLE
    # On the host machine
    .\Techee-Lab.ps1 -Role Host -Action build
    .\Techee-Lab.ps1 -Role Host -Action broker
    .\Techee-Lab.ps1 -Role Host -Action card          # copy host.peercard.json to C
    .\Techee-Lab.ps1 -Role Host -Action trust -Card .\controller.peercard.json -Control
    .\Techee-Lab.ps1 -Role Host -Action run

.EXAMPLE
    # On the controller machine
    .\Techee-Lab.ps1 -Role Controller -Action build
    .\Techee-Lab.ps1 -Role Controller -Action card    # copy controller.peercard.json to H
    .\Techee-Lab.ps1 -Role Controller -Action trust -Card .\host.peercard.json
    .\Techee-Lab.ps1 -Role Controller -Action run -Broker ws://192.168.1.43:8080 -Pair
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Host', 'Controller')]
    [string]$Role,

    [Parameter(Mandatory = $true)]
    [ValidateSet('build', 'broker', 'card', 'trust', 'run', 'status')]
    [string]$Action,

    # trust: the other machine's peer card.
    [string]$Card,

    # run: the broker to reach. The host defaults to its own loopback; the controller has
    # no sensible default and must be told.
    [string]$Broker,

    # trust (Host): also grant input.control. Off by default, matching techee-host, so a
    # grant never silently confers control.
    [switch]$Control,

    # run (Controller): register the pairing edge first. Needed once per broker lifetime.
    [switch]$Pair,

    # run (Controller): watch without sending input.
    [switch]$ViewOnly,

    # run (Host): refuse input at the transport level, whatever the grant says.
    [switch]$NoInput,

    # run: restrict ICE to relay candidates. Must be set on BOTH ends to prove anything.
    [switch]$ForceRelay,

    # run (Controller): record the session and snapshot the last frame.
    [string]$Record,
    [string]$Snapshot,

    [int]$Port = 8080,
    [string]$Configuration = 'Release',

    # Anything after -- goes straight to the underlying tool.
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Passthru
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Solution = Join-Path $RepoRoot 'windows\Techee.Windows.slnx'

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Warn($text) { Write-Host "  ! $text" -ForegroundColor Yellow }
function Write-Ok($text)   { Write-Host "  o $text" -ForegroundColor Green }

# ---- toolchain ----

function Get-DotNet {
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    # A user-local install, which is how a machine without admin rights gets an SDK.
    $userLocal = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path $userLocal) { return $userLocal }

    throw "No .NET SDK found. Install one with:`n" +
          "  winget install Microsoft.DotNet.SDK.10`n" +
          "or, without admin rights:`n" +
          "  irm https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1`n" +
          "  .\dotnet-install.ps1 -Channel 10.0 -InstallDir `"`$env:USERPROFILE\.dotnet`""
}

# A user-local SDK is invisible to the built executables: the apphost looks at PATH, at
# DOTNET_ROOT, at the registry, and at C:\Program Files\dotnet, and a ~/.dotnet install
# is none of those. Without this the binaries fail with "You must install .NET to run
# this application" on the very machine that just compiled them.
function Set-DotNetRoot {
    if ($env:DOTNET_ROOT) { return }

    $userLocal = Join-Path $env:USERPROFILE '.dotnet'
    if ((Test-Path (Join-Path $userLocal 'dotnet.exe')) -and -not (Test-Path 'C:\Program Files\dotnet\dotnet.exe')) {
        $env:DOTNET_ROOT = $userLocal
        Write-Ok "DOTNET_ROOT -> $userLocal (user-local SDK)"
    }
}

function Get-ToolPath {
    param([string]$ForRole)

    Set-DotNetRoot

    if ($ForRole -eq 'Host') {
        $path = Join-Path $RepoRoot "windows\src\Techee.Windows.HostApp\bin\$Configuration\net10.0-windows\techee-host.exe"
    } else {
        $path = Join-Path $RepoRoot "windows\src\Techee.Windows.ControllerApp\bin\$Configuration\net10.0-windows\techee-ctl.exe"
    }

    if (-not (Test-Path $path)) {
        throw "$([System.IO.Path]::GetFileName($path)) not found.`nRun: .\Techee-Lab.ps1 -Role $ForRole -Action build"
    }

    return $path
}

# ---- peer cards ----
#
# A peer card carries only the public half of an identity. It is not a secret and it is
# not a credential: possessing one lets you *address* and *verify* a peer, never
# impersonate it. The private key never leaves CNG on the machine that made it.

function Get-CardPath { param([string]$ForRole) Join-Path (Get-Location) "$($ForRole.ToLower()).peercard.json" }

function New-PeerCard {
    $tool = Get-ToolPath -ForRole $Role

    Write-Step "Reading this machine's identity"
    $output = & $tool identity
    if ($LASTEXITCODE -ne 0) { throw "identity failed with exit code $LASTEXITCODE" }

    $deviceId = ($output | Select-String -Pattern '^device id\s*:\s*(\S+)').Matches.Groups[1].Value
    $publicKey = ($output | Select-String -Pattern '^public key\s*:\s*(\S+)').Matches.Groups[1].Value
    $shortFp = ($output | Select-String -Pattern '^short fp\s*:\s*(\S+)').Matches.Groups[1].Value

    if (-not $deviceId -or -not $publicKey) {
        Write-Host ($output -join "`n")
        throw "Could not parse the identity output above."
    }

    $card = [ordered]@{
        role        = $Role
        deviceId    = $deviceId
        publicKey   = $publicKey
        shortFp     = $shortFp
        machine     = $env:COMPUTERNAME
        createdAt   = (Get-Date).ToString('o')
    }

    $path = Get-CardPath -ForRole $Role
    $card | ConvertTo-Json | Out-File -FilePath $path -Encoding utf8

    Write-Ok "wrote $path"
    Write-Host ""
    Write-Host "  device id : $deviceId"
    Write-Host "  short fp  : $shortFp"
    Write-Host ""
    Write-Step "Copy this file to the other machine, then run there:"
    if ($Role -eq 'Host') {
        Write-Host "  .\Techee-Lab.ps1 -Role Controller -Action trust -Card .\host.peercard.json"
    } else {
        Write-Host "  .\Techee-Lab.ps1 -Role Host -Action trust -Card .\controller.peercard.json -Control"
    }
}

function Import-PeerCard {
    if (-not $Card) { throw "-Card is required. Point it at the peer card copied from the other machine." }
    if (-not (Test-Path $Card)) { throw "Peer card not found: $Card" }

    $peer = Get-Content $Card -Raw | ConvertFrom-Json

    $expected = 'Controller'
    if ($Role -eq 'Controller') { $expected = 'Host' }

    if ($peer.role -ne $expected) {
        throw "That card is for a '$($peer.role)'. A $Role trusts a $expected. You have probably imported your own card."
    }

    $tool = Get-ToolPath -ForRole $Role

    Write-Step "Trusting $($peer.role) '$($peer.machine)'"
    Write-Host "  device id : $($peer.deviceId)"
    Write-Host "  short fp  : $($peer.shortFp)"
    Write-Host ""

    $trustArgs = @('trust', '--pub', $peer.publicKey, '--name', "$($peer.role) on $($peer.machine)")
    if ($Role -eq 'Host' -and $Control) { $trustArgs += '--control' }

    & $tool @trustArgs
    if ($LASTEXITCODE -ne 0) { throw "trust failed with exit code $LASTEXITCODE" }

    if ($Role -eq 'Host' -and -not $Control) {
        Write-Warn "Granted screen.view only. Add -Control to grant input.control (needed for D7/D8)."
    }
}

# ---- broker ----

function Get-LanAddress {
    # PrefixOrigin 'WellKnown' is what an APIPA 169.254 address gets when DHCP failed,
    # and an interface with no route is worse than no answer: the operator would type it
    # into the other machine and get a connection timeout with nothing to explain it.
    $addresses = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object {
            $_.IPAddress -ne '127.0.0.1' -and
            $_.PrefixOrigin -ne 'WellKnown' -and
            $_.InterfaceAlias -notmatch 'Loopback|vEthernet|VirtualBox|VMware|Hyper-V|WSL'
        }

    if (-not $addresses) { return $null }

    # InterfaceMetric lives on Get-NetIPInterface, not on the address, so the two are
    # joined by index. Lowest metric is the interface Windows would actually route over,
    # which on a laptop with both Wi-Fi and a docked Ethernet is the one that matters.
    $interfaces = @{}
    foreach ($i in (Get-NetIPInterface -AddressFamily IPv4 -ErrorAction SilentlyContinue)) {
        $interfaces[$i.InterfaceIndex] = $i.InterfaceMetric
    }

    $ranked = $addresses | Sort-Object -Property `
        @{ Expression = { $_.SkipAsSource } },
        @{ Expression = {
            if ($interfaces.ContainsKey($_.InterfaceIndex)) { $interfaces[$_.InterfaceIndex] } else { [int]::MaxValue }
        } }

    return @($ranked)[0].IPAddress
}

function Start-Broker {
    if ($Role -ne 'Host') { throw "The broker action belongs to the Host role." }

    $server = Join-Path $RepoRoot 'server'
    if (-not (Test-Path (Join-Path $server 'node_modules'))) {
        Write-Step "Installing broker dependencies"
        Push-Location $server
        try { npm ci } finally { Pop-Location }
    }

    # The rule is only added when it is missing, so repeated runs do not accumulate
    # duplicates. It needs elevation; without it the broker still runs and is still
    # reachable from this machine, just not from the other one.
    $ruleName = "techee-broker-$Port"
    $existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    if (-not $existing) {
        Write-Step "Opening TCP $Port to the local network"
        try {
            New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -LocalPort $Port `
                -Protocol TCP -Action Allow -Profile Private -ErrorAction Stop | Out-Null
            Write-Ok "firewall rule '$ruleName' added"
        } catch {
            Write-Warn "Could not add the firewall rule (needs an elevated shell)."
            Write-Warn "The controller will not reach this broker until port $Port is open."
        }
    } else {
        Write-Ok "firewall rule '$ruleName' already present"
    }

    $lan = Get-LanAddress
    Write-Host ""
    if ($lan) {
        Write-Step "Broker URL for the controller machine:"
        Write-Host "  ws://${lan}:$Port" -ForegroundColor Green
    } else {
        Write-Warn "Could not determine a LAN address; run ipconfig and use that."
    }
    Write-Host ""

    Push-Location $server
    try {
        $env:PORT = "$Port"
        node src/server.js
    } finally {
        Pop-Location
    }
}

# ---- run ----

function Invoke-Run {
    $tool = Get-ToolPath -ForRole $Role

    if ($Role -eq 'Host') {
        if (-not $Broker) { $Broker = "ws://127.0.0.1:$Port" }

        $runArgs = @('run', '--broker', $Broker)
        if ($NoInput)    { $runArgs += '--no-input' }
        if ($ForceRelay) { $runArgs += '--force-relay' }
    }
    else {
        if (-not $Broker) {
            throw "-Broker is required for the controller, e.g. -Broker ws://192.168.1.43:8080`n" +
                  "The host prints the right URL when it starts the broker."
        }

        $hostCard = Join-Path (Get-Location) 'host.peercard.json'
        if (-not (Test-Path $hostCard)) {
            throw "host.peercard.json not found in $(Get-Location).`n" +
                  "Copy it from the host machine and run -Action trust first."
        }

        $peer = Get-Content $hostCard -Raw | ConvertFrom-Json

        $runArgs = @('connect', '--broker', $Broker, '--host', $peer.deviceId)
        if ($Pair)       { $runArgs += '--pair' }
        if ($ViewOnly)   { $runArgs += '--view-only' }
        if ($ForceRelay) { $runArgs += '--force-relay' }
        if ($Record)     { $runArgs += @('--record', $Record) }
        if ($Snapshot)   { $runArgs += @('--snapshot', $Snapshot) }
    }

    if ($Passthru) { $runArgs += $Passthru }

    Write-Step "$tool $($runArgs -join ' ')"
    Write-Host ""

    & $tool @runArgs
    $code = $LASTEXITCODE

    Write-Host ""
    Explain-ExitCode -Code $code
    exit $code
}

function Explain-ExitCode {
    param([int]$Code)

    # The point of this table is that several of these failures look identical from the
    # outside — a session that connects and carries nothing looks exactly like a healthy
    # one until you read the counters.
    $meanings = @{
        0 = 'clean'
        1 = 'no verb given'
        2 = 'bad arguments, or no trusted key for the peer'
        3 = 'registration failed - is the broker running and reachable?'
        4 = '--force-relay was set but the broker offered no TURN server'
        5 = 'the dial was refused - not paired (try -Pair), or consent declined'
        6 = "the peer's SDP did not authenticate - trust the right key on both sides"
        7 = 'connected, but NO FRAME EVER DECODED'
    }

    if ($Code -eq 0) {
        Write-Ok "exit 0 - $($meanings[0])"
        return
    }

    $text = $meanings[$Code]
    if (-not $text) { $text = 'unrecognised failure' }
    Write-Host "  x exit $Code - $text" -ForegroundColor Red

    if ($Code -eq 7) {
        Write-Warn "This is the failure that looks healthy. ICE and DTLS completed and"
        Write-Warn "packets may well have arrived, but nothing assembled into a decodable"
        Write-Warn "frame. Compare 'video packets' against 'video frames' in the summary."
    }
}

# ---- status ----

function Show-Status {
    Write-Step "Techee lab status - role $Role"
    Write-Host ""

    try {
        $dotnet = Get-DotNet
        $version = & $dotnet --version
        Write-Ok ".NET SDK $version ($dotnet)"
    } catch {
        Write-Host "  x no .NET SDK" -ForegroundColor Red
    }

    try {
        $tool = Get-ToolPath -ForRole $Role
        Write-Ok "binary $tool"
    } catch {
        Write-Host "  x binary not built (-Action build)" -ForegroundColor Red
    }

    $mine = Get-CardPath -ForRole $Role
    if (Test-Path $mine) { Write-Ok "own card $mine" } else { Write-Warn "no own card yet (-Action card)" }

    $otherRole = 'controller'
    if ($Role -eq 'Controller') { $otherRole = 'host' }
    $theirs = Join-Path (Get-Location) "$otherRole.peercard.json"
    if (Test-Path $theirs) { Write-Ok "peer card $theirs" } else { Write-Warn "no $otherRole card here yet - copy it from the other machine" }

    try {
        $tool = Get-ToolPath -ForRole $Role
        Write-Host ""
        Write-Step "Trust store"
        & $tool list
    } catch {
        # Already reported above.
    }

    if ($Role -eq 'Host') {
        $lan = Get-LanAddress
        Write-Host ""
        if ($lan) { Write-Step "This machine is reachable at ws://${lan}:$Port" }
    }
}

# ---- dispatch ----

switch ($Action) {
    'build' {
        $dotnet = Get-DotNet
        Write-Step "Building $Solution ($Configuration)"
        & $dotnet build $Solution -c $Configuration
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        Write-Ok "build succeeded"
    }
    'broker' { Start-Broker }
    'card'   { New-PeerCard }
    'trust'  { Import-PeerCard }
    'run'    { Invoke-Run }
    'status' { Show-Status }
}
