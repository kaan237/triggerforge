[CmdletBinding()]
param(
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string] $Thumbprint = '25C26195A82AC4EC3A5C57CF6B7D652939154655',
    [switch] $NoSign
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$app = Join-Path $artifacts 'app'
$ui = Join-Path $root 'ui'
$notices = Join-Path $root 'THIRD-PARTY-NOTICES.txt'
$signScript = Join-Path $PSScriptRoot 'sign.ps1'
$properties = [xml](Get-Content -Raw -LiteralPath (Join-Path $root 'Directory.Build.props'))
$version = $properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw 'Directory.Build.props must contain a numeric release version.'
}

Push-Location -LiteralPath $ui
try {
    if (-not (Test-Path -LiteralPath 'node_modules' -PathType Container)) {
        & npm.cmd ci
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed ($LASTEXITCODE)." }
    }
    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw "UI build failed ($LASTEXITCODE)." }
}
finally {
    Pop-Location
}

# Use a clean output directory so stale UI assets or portable.txt cannot enter a release.
foreach ($directory in @($artifacts, $app)) {
    if (Test-Path -LiteralPath $directory) {
        $item = Get-Item -LiteralPath $directory -Force
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Expected a real output directory at $directory."
        }
    }
}
if (Test-Path -LiteralPath $app) {
    if ((Resolve-Path -LiteralPath $app).ProviderPath -ne $app) {
        throw "Unexpected publish output path: $app."
    }
    [IO.Directory]::Delete($app, $true)
}

Push-Location -LiteralPath $root
try {
    & dotnet publish src/Tactile.Host -c Release -r win-x64 --self-contained true `
        --artifacts-path (Join-Path $artifacts 'build') -p:PublishSingleFile=false -o $app
    if ($LASTEXITCODE -ne 0) { throw "Host publish failed ($LASTEXITCODE)." }
}
finally {
    Pop-Location
}

foreach ($file in @('TriggerForge.exe', 'WebView2Loader.dll', 'opus.dll', 'ui\index.html')) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $file) -PathType Leaf)) {
        throw "Publish output is missing $file."
    }
}
if (-not $NoSign) {
    $binaries = Get-ChildItem -LiteralPath $app -File -Recurse |
        Where-Object { $_.Name -in @('TriggerForge.exe', 'TriggerForge.dll') -or $_.Name -like 'Tactile.*.dll' }
    & $signScript -Thumbprint $Thumbprint -Path $binaries.FullName
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $artifacts "TriggerForge-$version-portable.zip"
$pendingZip = "$zip.partial"
try {
    if (Test-Path -LiteralPath $pendingZip) { Remove-Item -LiteralPath $pendingZip -Force }
    # Entries are added one by one: Windows PowerShell's CreateFromDirectory writes '\' separators,
    # which other unzip tools do not treat as folders.
    $archive = [IO.Compression.ZipFile]::Open($pendingZip, 'Create')
    try {
        foreach ($file in Get-ChildItem -LiteralPath $app -File -Recurse) {
            $entryName = $file.FullName.Substring($app.Length + 1).Replace('\', '/')
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName)
        }
        $null = $archive.CreateEntry('portable.txt')
        $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $notices, 'THIRD-PARTY-NOTICES.txt')
    }
    finally {
        $archive.Dispose()
    }
    Move-Item -LiteralPath $pendingZip -Destination $zip -Force
}
finally {
    if (Test-Path -LiteralPath $pendingZip) { Remove-Item -LiteralPath $pendingZip -Force }
}
Write-Host "SHA-256 $((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash)  $zip"

$iscc = (Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1).Source
if (-not $iscc) {
    # winget installs Inno Setup per user under %LOCALAPPDATA%\Programs.
    $iscc = @(${env:ProgramFiles(x86)}, $env:ProgramW6432, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs')) |
        Where-Object { $_ } |
        ForEach-Object { Join-Path $_ 'Inno Setup 6\ISCC.exe' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
}
if (-not $iscc) {
    Write-Host 'Inno Setup 6 is needed to build the installer. The portable ZIP is ready.'
    return
}

$arguments = @("/DAppVersion=$version")
if (-not $NoSign) {
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments += ('/STriggerForgeSign=$q{0}$q -NoProfile -NonInteractive -File $q{1}$q -Thumbprint {2} -Path $f' -f
        $powershell.Replace('$', '$$'), $signScript.Replace('$', '$$'), $Thumbprint)
    $arguments += '/DSignRelease=1'
}
$arguments += Join-Path $root 'installer\TriggerForge.iss'
& $iscc @arguments
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed ($LASTEXITCODE)." }
$installer = Join-Path $artifacts "TriggerForge-$version-setup.exe"
Write-Host "SHA-256 $((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash)  $installer"
