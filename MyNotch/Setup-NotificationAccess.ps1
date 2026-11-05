param([Parameter(Mandatory=$true)][string]$AppDirectory)
$ErrorActionPreference = 'Stop'
$appRoot = (Resolve-Path -LiteralPath $AppDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $appRoot 'MyNotch.exe'))) { throw 'MyNotch.exe not found.' }
$identityRoot = Join-Path $appRoot 'NotificationIdentity'
$assets = Join-Path $identityRoot 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
# A local development identity provides the Windows capability; no certificate or OS setting is changed.
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 150,150
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.Clear([System.Drawing.Color]::Black)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(240,137,46)),9
    try { $graphics.DrawEllipse($pen,35,30,80,80); $graphics.DrawLine($pen,50,115,100,115) } finally { $pen.Dispose() }
    $bitmap.Save((Join-Path $assets 'Logo.png'),[System.Drawing.Imaging.ImageFormat]::Png)
} finally { $graphics.Dispose(); $bitmap.Dispose() }
$manifest = @'
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
 xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
 xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
 IgnorableNamespaces="uap uap3 uap10 rescap">
 <Identity Name="MyNotch.Local" Publisher="CN=MyNotch" Version="1.0.0.0" ProcessorArchitecture="x64" />
 <Properties><DisplayName>MyNotch</DisplayName><PublisherDisplayName>MyNotch</PublisherDisplayName><Logo>Assets\Logo.png</Logo><uap10:AllowExternalContent>true</uap10:AllowExternalContent></Properties>
 <Resources><Resource Language="en-US" /></Resources>
 <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" /></Dependencies>
 <Applications><Application Id="App" Executable="MyNotch.exe" uap10:TrustLevel="mediumIL" uap10:RuntimeBehavior="win32App">
 <uap:VisualElements DisplayName="MyNotch" Description="Dynamic island for Windows" BackgroundColor="transparent" Square150x150Logo="Assets\Logo.png" Square44x44Logo="Assets\Logo.png" />
 </Application></Applications>
 <Capabilities><rescap:Capability Name="runFullTrust" /><rescap:Capability Name="unvirtualizedResources" /><uap3:Capability Name="userNotificationListener" /></Capabilities>
</Package>
'@
$manifestPath = Join-Path $identityRoot 'AppxManifest.xml'
[System.IO.File]::WriteAllText($manifestPath,$manifest)
Add-AppxPackage -Register $manifestPath -ExternalLocation $appRoot -ErrorAction Stop
Write-Output 'Windows access is set up. Close MyNotch, open MyNotch from Start, then click Enable Windows app notifications.'
