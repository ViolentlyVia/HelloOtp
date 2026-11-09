# Builds the release zip and its checksum in publish\release.
# The tree must be committed, so a release always matches a commit. Needs the .NET 10 SDK.

$ErrorActionPreference = 'Stop'

$repository = Split-Path $PSScriptRoot -Parent
if (git -C $repository status --porcelain) {
    throw 'Commit your changes first, so the release matches a commit.'
}

[xml]$properties = Get-Content (Join-Path $repository 'Directory.Build.props')
$version = $properties.Project.PropertyGroup.Version
$output = Join-Path $repository 'publish\release'
$stage = Join-Path $output 'stage'
$zip = Join-Path $output "HelloOtp-v$version-win-x64.zip"

if (Test-Path $output) { Remove-Item $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# No debug files, and ContinuousIntegrationBuild keeps this PC's folder paths out of the exe.
dotnet publish (Join-Path $repository 'src\HelloOtp\HelloOtp.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true -o $stage
if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }

Copy-Item (Join-Path $PSScriptRoot 'install.ps1'), (Join-Path $PSScriptRoot 'uninstall.ps1'), (Join-Path $repository 'LICENSE') $stage
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item $stage -Recurse -Force

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $(Split-Path $zip -Leaf)`n")
Write-Host "Built $zip"
Write-Host "SHA-256 $hash"
