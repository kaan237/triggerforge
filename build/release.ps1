[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $NotesEn,
    [Parameter(Mandatory)] [string] $NotesTr,
    [string] $Repository = 'kaan237/triggerforge'
)

# Builds, signs and publishes one release on the public update channel
# (docs/contract-phase7b.md, "Job C"). Refuses to replace an existing release.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$properties = [xml](Get-Content -Raw -LiteralPath (Join-Path $root 'Directory.Build.props'))
$version = $properties.Project.PropertyGroup.Version
$tag = "v$version"

& gh release view $tag --repo $Repository *> $null
if ($LASTEXITCODE -eq 0) { throw "Release $tag already exists on $Repository." }

& (Join-Path $PSScriptRoot 'publish.ps1')
$installer = Join-Path $artifacts "TriggerForge-$version-setup.exe"
$zip = Join-Path $artifacts "TriggerForge-$version-portable.zip"
foreach ($file in $installer, $zip) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing $file; Inno Setup 6 is needed for the installer." }
}

$tool = Join-Path $artifacts 'release-tool'
& dotnet build (Join-Path $root 'tools\Tactile.Tools.Release') -c Release -o $tool
if ($LASTEXITCODE -ne 0) { throw "Release tool build failed ($LASTEXITCODE)." }
$exe = Join-Path $tool 'triggerforge-release.exe'

& $exe manifest $version $artifacts $NotesEn $NotesTr
if ($LASTEXITCODE -ne 0) { throw "Manifest signing failed ($LASTEXITCODE)." }

# The key the app trusts is the one in UpdateKey.cs, not whatever the tool holds.
$keyFile = Get-Content -Raw -LiteralPath (Join-Path $root 'src\Tactile.Core\Updates\UpdateKey.cs')
if ($keyFile -notmatch 'ManifestPublicKey\s*=\s*"\s*([A-Za-z0-9+/=]+)"') { throw 'ManifestPublicKey not found in UpdateKey.cs.' }
& $exe verify $artifacts $Matches[1]
if ($LASTEXITCODE -ne 0) { throw 'The signed manifest does not verify with the key the app trusts.' }

$notes = "$NotesEn`n`n---`n`n$NotesTr"
& gh release create $tag --repo $Repository --title "TriggerForge $version" --notes $notes `
    $installer $zip (Join-Path $artifacts 'manifest.json') (Join-Path $artifacts 'manifest.json.sig')
if ($LASTEXITCODE -ne 0) { throw "gh release create failed ($LASTEXITCODE)." }
Write-Host "Published $tag on $Repository."
