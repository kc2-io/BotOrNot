# Exercise the publication gate without contacting Azure or using a private key.
$ErrorActionPreference = 'Stop'
$verifier = Join-Path $PSScriptRoot 'verify-release-signature.ps1'
$fixture = [System.IO.Path]::GetTempFileName()
$publisher = 'Test Publisher'
$certificate = [pscustomobject]@{ Publisher = $publisher; Thumbprint = 'TEST' }
$certificate | Add-Member -MemberType ScriptMethod -Name GetNameInfo -Value { param($type, $issuer) $this.Publisher }

$signatureState = @{ Value = $null }
Set-Item -Path Function:Get-AuthenticodeSignature -Value {
    param([string]$LiteralPath)
    return $signatureState.Value
}.GetNewClosure()

function Assert-Rejected {
    param([string]$ExpectedMessage, [string]$Path = $fixture)
    $caught = $null
    try { & $verifier -FilePath $Path -ExpectedPublisher $publisher }
    catch { $caught = $_.Exception.Message }
    if ($null -eq $caught -or $caught -notlike "*$ExpectedMessage*") {
        throw "Expected rejection containing '$ExpectedMessage'; got '$caught'."
    }
}

try {
    $signatureState.Value = [pscustomobject]@{
        Status = 'Valid'
        StatusMessage = ''
        SignerCertificate = $certificate
        TimeStamperCertificate = [pscustomobject]@{ Subject = 'Timestamp authority' }
    }
    & $verifier -FilePath $fixture -ExpectedPublisher $publisher
    Assert-Rejected -ExpectedMessage 'not found' -Path "$fixture.missing"

    foreach ($status in @('NotSigned', 'HashMismatch', 'NotTrusted')) {
        $signatureState.Value.Status = $status
        Assert-Rejected -ExpectedMessage 'not valid'
    }
    $signatureState.Value.Status = 'Valid'
    $signatureState.Value.SignerCertificate = $null
    Assert-Rejected -ExpectedMessage 'no signer certificate'
    $signatureState.Value.SignerCertificate = $certificate
    $signatureState.Value.TimeStamperCertificate = $null
    Assert-Rejected -ExpectedMessage 'no timestamp certificate'
    $signatureState.Value.TimeStamperCertificate = [pscustomobject]@{ Subject = 'Timestamp authority' }
    $certificate.Publisher = 'Other Publisher'
    Assert-Rejected -ExpectedMessage 'Unexpected release publisher'
    Write-Host 'All 8 release-signature gate checks passed.'
}
finally {
    Remove-Item -LiteralPath $fixture -Force
}
