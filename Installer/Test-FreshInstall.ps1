[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SampleAvi,
    [string]$MsiPath = (Join-Path $PSScriptRoot '../dist/KoView-x64.msi'),
    [switch]$VerifyInstalledOnly
)

$ErrorActionPreference = 'Stop'
$appKey = 'HKCU:\Software\KoView'
$legacyKey = 'HKCU:\Software\KoVideo'
$avi = (Resolve-Path -LiteralPath $SampleAvi).Path
if ([IO.Path]::GetExtension($avi) -ine '.avi') { throw 'Specify a playable AVI fixture.' }

if (!$VerifyInstalledOnly) {
    if ((Test-Path $appKey) -or (Test-Path $legacyKey) -or
        (Test-Path (Join-Path $env:LOCALAPPDATA 'KoView')) -or
        (Test-Path (Join-Path $env:LOCALAPPDATA 'KoVideo'))) {
        throw 'Run the fresh install test in a clean Windows account without KoView or KoVideo.'
    }
    $msi = (Resolve-Path -LiteralPath $MsiPath).Path
    $log = Join-Path $env:TEMP 'KoView-fresh-install.log'
    $result = Start-Process msiexec.exe -ArgumentList ('/i "{0}" /qn /L*v "{1}"' -f $msi, $log) -PassThru -Wait -WindowStyle Hidden
    if ($result.ExitCode) { throw "MSI returned $($result.ExitCode). See $log." }
    Write-Output "Fresh install succeeded. Log: $log"
}

$install = Get-ItemPropertyValue $appKey InstallPath
$exe = Join-Path $install 'KoView.exe'
foreach ($file in 'KoView.exe', 'libmpv-2.dll', 'Mpv-Distribution-Notice.txt') {
    if (!(Test-Path -LiteralPath (Join-Path $install $file) -PathType Leaf)) { throw "Missing installed file: $file" }
}

$handler = Get-ItemPropertyValue 'HKCU:\Software\KoView\Capabilities\FileAssociations' '.avi'
$open = (Get-Item "HKCU:\Software\Classes\$handler\shell\open\command").GetValue('')
if ($open -ne ('"{0}" "%1"' -f $exe)) { throw "Unexpected AVI open command: $open" }
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'KoView.lnk'
if (!(Test-Path -LiteralPath $shortcutPath -PathType Leaf)) { throw 'Start Menu shortcut is missing.' }
$shell = New-Object -ComObject WScript.Shell
if ($shell.CreateShortcut($shortcutPath).TargetPath -ne $exe) { throw 'Unexpected Start Menu shortcut target.' }
Write-Output 'AVI registration and Start Menu shortcut passed.'

$process = Start-Process -FilePath $exe -ArgumentList ('"{0}"' -f $avi) -PassThru -WindowStyle Hidden
try {
    $deadline = (Get-Date).AddSeconds(12)
    do {
        $process.Refresh()
        if ($process.HasExited) { throw "KoView exited with code $($process.ExitCode)." }
        $engine = $process.Modules | Where-Object { $_.FileName -eq (Join-Path $install 'libmpv-2.dll') }
        if ($engine) { break }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    if (!$engine) { throw 'The installed libmpv DLL was not loaded.' }
    Write-Output 'KoView launched with the AVI fixture and loaded the installed libmpv DLL.'
    Write-Output 'For visual playback via Explorer, select KoView as the AVI default app and double-click the fixture.'
}
finally {
    if (!$process.HasExited) {
        $closed = $process.CloseMainWindow() -and $process.WaitForExit(3000)
        if (!$closed) { Stop-Process -Id $process.Id }
    }
}
