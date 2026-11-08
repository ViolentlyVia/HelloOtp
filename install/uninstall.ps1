# Removes HelloOtp. Your encrypted vault is kept unless you pass -DeleteVault.
param(
    [switch]$DeleteVault
)

$ErrorActionPreference = 'Stop'

$target = Join-Path $env:LOCALAPPDATA 'Programs\HelloOtp'
$exe = Join-Path $target 'HelloOtp.exe'
$data = Join-Path $env:LOCALAPPDATA 'HelloOtp'
$ruleName = 'HelloOtp block outbound'

Get-Process -Name HelloOtp -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe } |
    ForEach-Object { $_.Kill(); $_.WaitForExit() }

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'HelloOtp' -ErrorAction SilentlyContinue

if (Test-Path $target) {
    Remove-Item $target -Recurse -Force
}

$script = "Remove-NetFirewallRule -DisplayName '$ruleName' -ErrorAction SilentlyContinue"
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
try {
    Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '-NoProfile', '-EncodedCommand', $encoded
}
catch {
    Write-Warning "The firewall rule '$ruleName' was not removed. It is harmless and can be deleted in Windows Firewall."
}

if ($DeleteVault) {
    $answer = Read-Host "Permanently delete your two-factor vault in $data? This cannot be undone. Type DELETE to confirm"
    if ($answer -ceq 'DELETE') {
        Remove-Item $data -Recurse -Force
        Write-Host 'Vault deleted.'
    }
    else {
        Write-Host 'Vault kept.'
    }
}
else {
    Write-Host "Your encrypted vault was kept in $data"
}
