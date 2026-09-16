param(
    [Parameter(Mandatory)]
    [string]$FilePath,
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedPublisher
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
    throw "Release executable not found: $FilePath"
}

$signature = Get-AuthenticodeSignature -LiteralPath $FilePath
if ($signature.Status -ne 'Valid') {
    throw "Release signature is not valid: $($signature.Status) - $($signature.StatusMessage)"
}
if ($null -eq $signature.SignerCertificate) {
    throw 'Release signature has no signer certificate.'
}
if ($null -eq $signature.TimeStamperCertificate) {
    throw 'Release signature has no timestamp certificate.'
}

$publisher = $signature.SignerCertificate.GetNameInfo(
    [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
if ($publisher -cne $ExpectedPublisher) {
    throw "Unexpected release publisher: '$publisher' (expected '$ExpectedPublisher')."
}

Write-Host "Verified timestamped release signature: $publisher ($($signature.SignerCertificate.Thumbprint))"
