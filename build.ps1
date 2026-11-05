# Build MyNotch (Phase 1). Uses system dotnet if present, else the local .\.dotnet SDK.
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$PublishAot
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = "$PSScriptRoot\.dotnet-home"
$env:NUGET_PACKAGES = "$PSScriptRoot\.nuget\packages"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { 'dotnet' } else { "$PSScriptRoot\.dotnet\dotnet.exe" }
if (-not (Test-Path $dotnet) -and $dotnet -ne 'dotnet') {
    throw "No dotnet SDK found. Install .NET 8 SDK or run: Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1"
}

# WinUI's MrtCore targets load Appx tasks from VS Build Tools when using the standalone SDK.
$appxProps = @()
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vsPath = & $vswhere -latest -products * -property installationPath 2>$null | Select-Object -First 1
    $appx = if ($vsPath) { Join-Path $vsPath 'MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage' } else { $null }
    if ($appx -and (Test-Path (Join-Path $appx 'Microsoft.Build.AppxPackage.dll'))) {
        $appxProps = @("-p:AppxMSBuildToolsPath=$($appx.Replace('\','/'))/")
    }
}

if ($PublishAot) {
    & $dotnet publish MyNotch\MyNotch.csproj -c Release -p:Platform=x64 -r win-x64 @appxProps
} else {
    & $dotnet build MyNotch\MyNotch.csproj -c $Configuration -p:Platform=x64 @appxProps
}
exit $LASTEXITCODE
