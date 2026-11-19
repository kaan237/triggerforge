[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string] $Thumbprint,
    [Parameter(Mandatory)]
    [string[]] $Path
)

$ErrorActionPreference = 'Stop'
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
if (-not $certificate -or -not $certificate.HasPrivateKey) {
    throw "Code signing certificate $Thumbprint with a private key was not found in Cert:\CurrentUser\My."
}

foreach ($file in $Path) {
    $signature = Set-AuthenticodeSignature -LiteralPath $file -Certificate $certificate `
        -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
    if (-not $signature.TimeStamperCertificate -or $signature.SignerCertificate.Thumbprint -ne $Thumbprint) {
        throw "Signing verification failed for ${file}: $($signature.StatusMessage)"
    }
    if ($signature.Status -ne 'Valid') {
        # A self-signed certificate is not in the trusted roots, so Windows reports UnknownError
        # for an intact signature. Accept that one case only: the chain must fail for an
        # untrusted root and nothing else (expiry, revocation, bad usage still stop here).
        $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
        try {
            $chain.ChainPolicy.RevocationMode = 'NoCheck'
            $null = $chain.Build($signature.SignerCertificate)
            $problems = @($chain.ChainStatus | Where-Object { $_.Status -ne 'UntrustedRoot' })
            if ($signature.Status -ne 'UnknownError' -or $problems.Count -gt 0) {
                throw "Signing verification failed for ${file}: $($signature.StatusMessage)"
            }
        }
        finally {
            $chain.Dispose()
        }
    }
}
