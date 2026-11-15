[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CertificateThumbprint,
    [Parameter(Mandatory)]
    [uri]$TimestampUrl,
    [string]$MsiPath = (Join-Path $PSScriptRoot '../dist/KoView-x64.msi'),
    [string]$SignTool = 'signtool.exe',
    [switch]$LocalMachine
)

$ErrorActionPreference = 'Stop'
$thumbprint = $CertificateThumbprint -replace '\s', ''
if ($thumbprint -notmatch '^[A-Fa-f0-9]{40}$') { throw 'Use the 40-digit code-signing certificate thumbprint.' }
if (!$TimestampUrl.IsAbsoluteUri -or $TimestampUrl.Scheme -ne 'https') {
    throw 'Use an HTTPS RFC 3161 timestamp server URL.'
}

$store = if ($LocalMachine) { 'Cert:\LocalMachine\My' } else { 'Cert:\CurrentUser\My' }
$now = Get-Date
$certificate = Get-ChildItem -LiteralPath $store -CodeSigningCert | Where-Object {
    $_.Thumbprint -eq $thumbprint -and $_.HasPrivateKey -and $_.NotBefore -le $now -and $_.NotAfter -gt $now
}
if (!$certificate) { throw "No valid code-signing certificate with a private key was found in $store." }

$msi = (Resolve-Path -LiteralPath $MsiPath).Path
$signed = Join-Path (Split-Path $msi) ("KoView-signed-$([Guid]::NewGuid()).msi")
$storeOption = if ($LocalMachine) { @('/sm') } else { @() }
try {
    Copy-Item -LiteralPath $msi -Destination $signed
    & $SignTool sign @storeOption /sha1 $thumbprint /fd SHA256 /tr $TimestampUrl.AbsoluteUri /td SHA256 $signed
    if ($LASTEXITCODE) { throw 'MSI signing failed.' }
    & $SignTool verify /pa /tw /v $signed
    if ($LASTEXITCODE) { throw 'MSI signature verification failed.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $signed
    if ($signature.Status -ne 'Valid' -or !$signature.TimeStamperCertificate) {
        throw 'A valid Authenticode signature and timestamp are required.'
    }
    Move-Item -LiteralPath $signed -Destination $msi -Force
    Get-FileHash -LiteralPath $msi -Algorithm SHA256
}
finally {
    if (Test-Path -LiteralPath $signed) { Remove-Item -LiteralPath $signed }
}
