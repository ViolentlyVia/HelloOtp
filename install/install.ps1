# Builds HelloOtp and installs it for the current user.
# -NoFirewall skips the outbound block rule (the only step that needs an admin prompt).
# -NoStartup skips starting HelloOtp at sign-in.
param(
    [switch]$NoFirewall,
    [switch]$NoStartup
)

$ErrorActionPreference = 'Stop'

$repository = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $repository 'publish'
$target = Join-Path $env:LOCALAPPDATA 'Programs\HelloOtp'
$exe = Join-Path $target 'HelloOtp.exe'
$ruleName = 'HelloOtp block outbound'

dotnet publish (Join-Path $repository 'src\HelloOtp\HelloOtp.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }

Get-Process -Name HelloOtp -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe } |
    ForEach-Object { $_.Kill(); $_.WaitForExit() }

New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item (Join-Path $publish 'HelloOtp.exe') $exe -Force

if (-not $NoFirewall) {
    $script = @"
Remove-NetFirewallRule -DisplayName '$ruleName' -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName '$ruleName' -Direction Outbound -Program '$exe' -Action Block -Profile Any | Out-Null
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    try {
        $process = Start-Process powershell -Verb RunAs -Wait -PassThru -WindowStyle Hidden `
            -ArgumentList '-NoProfile', '-EncodedCommand', $encoded
        if ($process.ExitCode -ne 0) { throw 'The firewall command failed.' }
        Write-Host 'Firewall rule added: HelloOtp cannot make outbound connections.'
    }
    catch {
        Write-Warning "The firewall rule was NOT added: $($_.Exception.Message) Run this script again to add it."
    }
}

if (-not $NoStartup) {
    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'HelloOtp' -Value "`"$exe`""
}

Start-Process $exe
Write-Host "Installed to $target"
