param(
    [string]$Wix = (Join-Path $PSScriptRoot '../obj/wix314'),
    [switch]$SkipIceValidation,
    [string]$StagePath = 'obj/installer-payload',
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$stage = Join-Path $root $StagePath
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $stage,$dist | Out-Null
& $Dotnet publish (Join-Path $root 'KoVideo.csproj') -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -o $stage
if ($LASTEXITCODE) { throw 'Publish failed.' }
$sdkRoot = Split-Path (Get-Command $Dotnet -CommandType Application).Source
Copy-Item (Join-Path $sdkRoot 'LICENSE.txt') (Join-Path $stage 'DotNet-LICENSE.txt')
Copy-Item (Join-Path $sdkRoot 'ThirdPartyNotices.txt') (Join-Path $stage 'DotNet-ThirdPartyNotices.txt')
Copy-Item (Join-Path $root 'engine/notices/*.txt') $stage

$patterns = [regex]::Match((Get-Content (Join-Path $root 'PlayerWindow.cs') -Raw), 'VideoPatterns = "([^"]+)"').Groups[1].Value
if (!$patterns) { throw 'Video extensions were not found.' }
$associations = foreach ($pattern in $patterns.Split(';')) {
    $extension = $pattern.Substring(1)
    '<RegistryValue Root="HKCU" Key="Software\KoView\Capabilities\FileAssociations" Name="{0}" Value="KoVideo.Video" Type="string" />' -f $extension
    '<RegistryValue Root="HKCU" Key="Software\Classes\{0}\OpenWithProgids" Name="KoVideo.Video" Value="" Type="string" />' -f $extension
}
$source = Join-Path $stage 'KoVideo.wxs'
(Get-Content (Join-Path $PSScriptRoot 'KoVideo.wxs') -Raw).Replace('<!-- File associations -->', ($associations -join "`n")) | Set-Content $source -Encoding utf8
$object = Join-Path $stage 'KoVideo.wixobj'
& (Join-Path $Wix 'candle.exe') -nologo -arch x64 "-dPayload=$stage" "-dIcon=$(Join-Path $root 'Assets/KoVideo.ico')" -out $object $source
if ($LASTEXITCODE) { throw 'MSI compilation failed.' }
[string[]]$validation = if ($SkipIceValidation) { '-sval' }
& (Join-Path $Wix 'light.exe') -nologo @validation -out (Join-Path $dist 'KoView-x64.msi') $object
if ($LASTEXITCODE) { throw 'MSI linking failed.' }
