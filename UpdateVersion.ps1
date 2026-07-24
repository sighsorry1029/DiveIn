[CmdletBinding()]
param (
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $VersionString,
    [string] $PluginFile,
    [string] $ManifestFile,
    [switch] $ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    if ([string]::IsNullOrWhiteSpace($PluginFile)) {
        $PluginFile = Join-Path $PSScriptRoot 'Plugin.cs'
    }

    if ([string]::IsNullOrWhiteSpace($ManifestFile)) {
        $ManifestFile = Join-Path (Join-Path $PSScriptRoot 'Thunderstore') 'manifest.json'
    }

    if ($VersionString -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "Version '$VersionString' must use major.minor.patch with no leading zeroes."
    }

    foreach ($part in $VersionString.Split('.')) {
        $numericPart = [Convert]::ToUInt64($part, [Globalization.CultureInfo]::InvariantCulture)
        if ($numericPart -gt 65534) {
            throw "Version component '$part' exceeds the AssemblyVersion limit of 65534."
        }
    }

    $resolvedPluginPath = (Resolve-Path -LiteralPath $PluginFile).Path
    $resolvedManifestPath = (Resolve-Path -LiteralPath $ManifestFile).Path
    if ((Get-Item -LiteralPath $resolvedPluginPath).PSIsContainer) {
        throw "Plugin path '$resolvedPluginPath' is not a file."
    }

    if ((Get-Item -LiteralPath $resolvedManifestPath).PSIsContainer) {
        throw "Manifest path '$resolvedManifestPath' is not a file."
    }

    $pluginText = [IO.File]::ReadAllText($resolvedPluginPath)
    $pluginVersionPattern = [Text.RegularExpressions.Regex]::new(
        '(?m)^(?<prefix>[ \t]*internal[ \t]+const[ \t]+string[ \t]+ModVersion[ \t]*=[ \t]*")(?<version>[^"]+)(?<suffix>"[ \t]*;[^\r\n]*)$',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    $pluginVersionMatches = $pluginVersionPattern.Matches($pluginText)
    if ($pluginVersionMatches.Count -ne 1) {
        throw "Expected exactly one ModVersion declaration in '$resolvedPluginPath', but found $($pluginVersionMatches.Count)."
    }

    $pluginVersion = $pluginVersionMatches[0].Groups['version'].Value

    $manifestText = [IO.File]::ReadAllText($resolvedManifestPath)
    $manifest = $manifestText | ConvertFrom-Json
    if ($null -eq $manifest -or $null -eq $manifest.PSObject.Properties['version_number']) {
        throw "Manifest '$resolvedManifestPath' does not contain a version_number property."
    }

    $manifestVersionPattern = [Text.RegularExpressions.Regex]::new(
        '(?<prefix>"version_number"[ \t]*:[ \t]*")(?<version>[^"]*)(?<suffix>")',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    $manifestVersionMatches = $manifestVersionPattern.Matches($manifestText)
    if ($manifestVersionMatches.Count -ne 1) {
        throw "Expected exactly one quoted version_number in '$resolvedManifestPath', but found $($manifestVersionMatches.Count)."
    }

    $manifestVersion = [string] $manifest.version_number
    if (-not [string]::Equals(
        $manifestVersion,
        $manifestVersionMatches[0].Groups['version'].Value,
        [StringComparison]::Ordinal)) {
        throw "Parsed manifest version does not match its source text in '$resolvedManifestPath'."
    }

    if ($ValidateOnly) {
        $mismatches = @()
        if (-not [string]::Equals($pluginVersion, $VersionString, [StringComparison]::Ordinal)) {
            $mismatches += "Plugin.cs ModVersion is '$pluginVersion'"
        }

        if (-not [string]::Equals($manifestVersion, $VersionString, [StringComparison]::Ordinal)) {
            $mismatches += "manifest.json version_number is '$manifestVersion'"
        }

        if ($mismatches.Count -gt 0) {
            throw "Version mismatch: expected '$VersionString'; $($mismatches -join '; '). Run the SetVersion target with the intended version, then rebuild."
        }

        Write-Host "Version validation passed: $VersionString"
        return
    }

    $updatedPluginText = $pluginVersionPattern.Replace(
        $pluginText,
        [Text.RegularExpressions.MatchEvaluator] {
            param ($match)
            return $match.Groups['prefix'].Value + $VersionString + $match.Groups['suffix'].Value
        },
        1)
    $updatedManifestText = $manifestVersionPattern.Replace(
        $manifestText,
        [Text.RegularExpressions.MatchEvaluator] {
            param ($match)
            return $match.Groups['prefix'].Value + $VersionString + $match.Groups['suffix'].Value
        },
        1)

    $pluginNeedsUpdate = -not [string]::Equals($pluginText, $updatedPluginText, [StringComparison]::Ordinal)
    $manifestNeedsUpdate = -not [string]::Equals($manifestText, $updatedManifestText, [StringComparison]::Ordinal)
    if (-not $pluginNeedsUpdate -and -not $manifestNeedsUpdate) {
        Write-Host "Version is already set to $VersionString"
        return
    }

    $utf8NoBom = New-Object Text.UTF8Encoding($false)
    $pluginWriteAttempted = $false
    $manifestWriteAttempted = $false
    try {
        if ($pluginNeedsUpdate) {
            $pluginWriteAttempted = $true
            [IO.File]::WriteAllText($resolvedPluginPath, $updatedPluginText, $utf8NoBom)
        }

        if ($manifestNeedsUpdate) {
            $manifestWriteAttempted = $true
            [IO.File]::WriteAllText($resolvedManifestPath, $updatedManifestText, $utf8NoBom)
        }

        $verifiedPluginText = [IO.File]::ReadAllText($resolvedPluginPath)
        $verifiedPluginMatches = $pluginVersionPattern.Matches($verifiedPluginText)
        $verifiedManifest = [IO.File]::ReadAllText($resolvedManifestPath) | ConvertFrom-Json
        $pluginWriteVerified = $verifiedPluginMatches.Count -eq 1 -and
                               [string]::Equals($verifiedPluginMatches[0].Groups['version'].Value, $VersionString, [StringComparison]::Ordinal)
        $manifestWriteVerified = [string]::Equals([string] $verifiedManifest.version_number, $VersionString, [StringComparison]::Ordinal)
        if (-not $pluginWriteVerified -or -not $manifestWriteVerified) {
            throw "Post-write verification failed."
        }
    }
    catch {
        $updateFailure = $_.Exception.Message
        $rollbackFailures = @()
        if ($pluginWriteAttempted) {
            try {
                [IO.File]::WriteAllText($resolvedPluginPath, $pluginText, $utf8NoBom)
            }
            catch {
                $rollbackFailures += "Plugin.cs rollback failed: $($_.Exception.Message)"
            }
        }

        if ($manifestWriteAttempted) {
            try {
                [IO.File]::WriteAllText($resolvedManifestPath, $manifestText, $utf8NoBom)
            }
            catch {
                $rollbackFailures += "manifest.json rollback failed: $($_.Exception.Message)"
            }
        }

        $rollbackSuffix = if ($rollbackFailures.Count -eq 0) {
            " Original contents were restored."
        }
        else {
            " $($rollbackFailures -join '; ')"
        }

        throw "Version update failed: $updateFailure$rollbackSuffix"
    }

    Write-Host "Updated version: Plugin.cs '$pluginVersion' -> '$VersionString'; manifest.json '$manifestVersion' -> '$VersionString'"
}
catch {
    throw "Failed to coordinate version '$VersionString': $($_.Exception.Message)"
}
