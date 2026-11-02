$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# This script is embedded in the executable; the elevated helper accepts only owner hash, IPv4 and port.
Import-Module (Join-Path $PSHOME 'Modules\NetSecurity\NetSecurity.psd1') -ErrorAction Stop
$group = 'JeekMusic.LAN.Managed.v1.' + $request.owner
$marker = 'JeekMusic managed LAN rule v1; owner=' + $request.owner
$needed = $request.port -ne 0 -and -not [Net.IPAddress]::IsLoopback([Net.IPAddress]::Parse($request.bind))
$local = if ($request.bind -eq '0.0.0.0') { 'Any' } else { $request.bind }
function RuleName($program, $address, $port) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($program.ToUpperInvariant() + '|' + $address + '|' + $port))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    return $group + '.' + $hash
}
$name = RuleName $request.program $local $request.port
function Rules($store) {
    # A narrow exact group query: never enumerate or disclose unrelated firewall rules.
    @(Get-NetFirewallRule -PolicyStore $store -Group $group -ErrorAction SilentlyContinue)
}
function Owned($rule) {
    if ($rule.Group -ne $group -or $rule.Description -ne $marker -or $rule.Direction -ne 'Inbound' -or $rule.Action -ne 'Allow') { return $false }
    $app = $rule | Get-NetFirewallApplicationFilter
    $port = $rule | Get-NetFirewallPortFilter
    $address = $rule | Get-NetFirewallAddressFilter
    if ($port.Protocol -ne 'TCP' -or @($port.LocalPort).Count -ne 1 -or @($address.LocalAddress).Count -ne 1 -or $app.Program -notmatch '(?i)\\JeekMusic\.exe$' -or $port.LocalPort -notmatch '^\d+$') { return $false }
    return $rule.Name -eq (RuleName $app.Program ([string]$address.LocalAddress) ([string]$port.LocalPort))
}
function Exact($rule) {
    if (-not (Owned $rule) -or $rule.Name -ne $name -or $rule.Enabled -ne 'True' -or [string]$rule.Profile -ne 'Any' -or $rule.EdgeTraversalPolicy -ne 'Block') { return $false }
    $app = $rule | Get-NetFirewallApplicationFilter
    $port = $rule | Get-NetFirewallPortFilter
    $address = $rule | Get-NetFirewallAddressFilter
    return $app.Program -eq $request.program -and [string]$port.LocalPort -eq [string]$request.port -and [string]$port.RemotePort -eq 'Any' -and [string]$address.LocalAddress -eq $local -and [string]$address.RemoteAddress -eq 'Any'
}
try {
    $owned = @(Rules 'PersistentStore' | Where-Object { Owned $_ })
    if ($apply) {
        foreach ($rule in $owned) {
            if (-not $needed -or -not (Exact $rule)) { $rule | Remove-NetFirewallRule -ErrorAction Stop }
        }
        if ($needed -and -not @($owned | Where-Object { Exact $_ }).Count) {
            New-NetFirewallRule -PolicyStore PersistentStore -Name $name -DisplayName 'JeekMusic music sync (managed)' -Group $group -Description $marker -Program $request.program -Direction Inbound -Action Allow -Enabled True -Protocol TCP -LocalPort $request.port -RemotePort Any -LocalAddress $local -RemoteAddress Any -Profile Any -EdgeTraversalPolicy Block | Out-Null
        }
    }
    $owned = @(Rules 'PersistentStore' | Where-Object { Owned $_ })
    $active = @(Rules 'ActiveStore' | Where-Object { Exact $_ })
    $satisfied = if ($needed) { $owned.Count -eq 1 -and @($owned | Where-Object { Exact $_ }).Count -eq 1 -and $active.Count -eq 1 } else { $owned.Count -eq 0 }
    $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore | Select-Object Name,Enabled,AllowInboundRules,AllowLocalFirewallRules)
    $policyBlocked = $needed -and @($profiles | Where-Object { $_.Enabled -and ($_.AllowInboundRules -eq 'False' -or $_.AllowLocalFirewallRules -eq 'False') }).Count -gt 0
    if ($policyBlocked) { $satisfied = $false }
    $state = if ($policyBlocked) { 'policy_blocked' } elseif ($satisfied -and $needed) { 'configured' } elseif ($satisfied) { 'not_required' } else { 'repair_required' }
    $message = switch ($state) {
        'configured' { 'Exact Windows TCP application rule is active. External reachability is not verified.' }
        'not_required' { 'No managed inbound rule is needed for a stopped or loopback listener; old owned rules are removed.' }
        'policy_blocked' { 'Managed Windows policy disables local inbound rules on one or more profiles.' }
        default { 'The exact application rule is missing or stale; administrator repair is needed.' }
    }
    [ordered]@{ state=$state; satisfied=[bool]$satisfied; message=$message; ruleName=$(if($needed){$name}else{$null}); ownedRuleCount=$owned.Count; activeExactRuleCount=$active.Count; localAddress=$local; remoteAddress='Any'; protocol='TCP'; profiles=@('Domain','Private','Public'); profilePolicy=$profiles } | ConvertTo-Json -Depth 5 -Compress
} catch {
    # Never print rule contents, command inputs or unrelated policy. Parent reports failure separately from listener state.
    exit 1
}
