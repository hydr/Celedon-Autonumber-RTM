<#
.SYNOPSIS
    Produces versioned managed and unmanaged Celedon AutoNumber solution zips.

.DESCRIPTION
    The repo ships template solution zips under Solutions/. This script takes
    those templates, swaps in a freshly built plugin assembly, stamps the
    solution version, and writes ready-to-import managed + unmanaged zips to
    the output directory.

    Only the plugin assembly entry and solution.xml <Version> are modified;
    every other entry is copied byte-for-byte via System.IO.Compression in
    Update mode, so the produced zips stay valid for Dataverse import.

    The assembly's strong name (PublicKeyToken ad1329e8e0985d48) and assembly
    version (1.0.0.0) must stay constant — customizations.xml references the
    assembly by full name, so only the solution version is bumped per release.

.PARAMETER Version
    Four-part solution version, e.g. "1.3.0.42" (Major.Minor.Patch.Build).

.PARAMETER AssemblyPath
    Path to the freshly built Celedon.AutoNumber.dll.

.PARAMETER TemplateDir
    Directory holding the template zips. Defaults to "Solutions".

.PARAMETER OutputDir
    Directory to write the produced zips to. Defaults to "artifacts".

.EXAMPLE
    .\scripts\Build-SolutionArtifacts.ps1 `
        -Version "1.3.0.42" `
        -AssemblyPath "AutoNumber\bin\Release\Celedon.AutoNumber.dll"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$AssemblyPath,

    [Parameter(Mandatory = $false)]
    [string]$TemplateDir = "Solutions",

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "artifacts",

    [Parameter(Mandatory = $false)]
    [string]$UnmanagedTemplate = "CeledonAutoNumber_1_2_0_0.zip",

    [Parameter(Mandatory = $false)]
    [string]$ManagedTemplate = "CeledonAutoNumber_1_2_0_0_managed.zip"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Update-SolutionZip {
    param(
        [string]$TemplatePath,
        [string]$OutputPath,
        [string]$Version,
        [byte[]]$AssemblyBytes
    )

    if (-not (Test-Path $TemplatePath)) {
        throw "Template solution zip not found: $TemplatePath"
    }

    if (Test-Path $OutputPath) { Remove-Item $OutputPath -Force }
    Copy-Item -Path $TemplatePath -Destination $OutputPath -Force

    $zip = [System.IO.Compression.ZipFile]::Open($OutputPath, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        # --- 1) Stamp solution version -------------------------------------
        $solEntry = $zip.GetEntry("solution.xml")
        if ($null -eq $solEntry) { throw "solution.xml not found in $TemplatePath" }

        $reader = New-Object System.IO.StreamReader($solEntry.Open(), [System.Text.Encoding]::UTF8)
        $solXml = $reader.ReadToEnd()
        $reader.Dispose()

        $newSolXml = [System.Text.RegularExpressions.Regex]::Replace(
            $solXml, '<Version>[^<]*</Version>', "<Version>$Version</Version>", 1)
        if ($newSolXml -eq $solXml) {
            throw "Could not find/replace <Version> in solution.xml of $TemplatePath"
        }

        $stream = $solEntry.Open()
        $stream.SetLength(0)
        $writer = New-Object System.IO.StreamWriter($stream, (New-Object System.Text.UTF8Encoding($true)))
        $writer.Write($newSolXml)
        $writer.Flush()
        $writer.Dispose()

        # --- 2) Swap the plugin assembly -----------------------------------
        $dllEntry = $zip.Entries | Where-Object { $_.FullName -like "PluginAssemblies/*/*.dll" } | Select-Object -First 1
        if ($null -eq $dllEntry) { throw "Plugin assembly entry not found in $TemplatePath" }

        $dllStream = $dllEntry.Open()
        $dllStream.SetLength(0)
        $dllStream.Write($AssemblyBytes, 0, $AssemblyBytes.Length)
        $dllStream.Flush()
        $dllStream.Dispose()

        Write-Host "  Updated $([System.IO.Path]::GetFileName($OutputPath)): version=$Version, assembly entry='$($dllEntry.FullName)' ($($AssemblyBytes.Length) bytes)"
    }
    finally {
        $zip.Dispose()
    }
}

# ── Main ───────────────────────────────────────────────────────────────────

$resolvedAsm = (Resolve-Path $AssemblyPath).Path
$asmBytes = [System.IO.File]::ReadAllBytes($resolvedAsm)
Write-Host "Assembly: $resolvedAsm ($($asmBytes.Length) bytes)"

if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir | Out-Null }
$OutputDir = (Resolve-Path $OutputDir).Path

$baseName = "CeledonAutoNumber_" + ($Version -replace '\.', '_')
$unmanagedOut = Join-Path $OutputDir "$baseName.zip"
$managedOut   = Join-Path $OutputDir "${baseName}_managed.zip"

Write-Host ""
Write-Host "Building unmanaged solution..."
Update-SolutionZip -TemplatePath (Join-Path $TemplateDir $UnmanagedTemplate) `
    -OutputPath $unmanagedOut -Version $Version -AssemblyBytes $asmBytes

Write-Host "Building managed solution..."
Update-SolutionZip -TemplatePath (Join-Path $TemplateDir $ManagedTemplate) `
    -OutputPath $managedOut -Version $Version -AssemblyBytes $asmBytes

Write-Host ""
Write-Host "Done. Artifacts:"
Write-Host "  $unmanagedOut"
Write-Host "  $managedOut"
