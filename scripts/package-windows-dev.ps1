[CmdletBinding()]
param(
    [string]$AuditExistingPublishDirectory,
    [switch]$TestPrivacyScanner,
    [switch]$TestPublishDiagnostics
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $workspaceRoot 'apps\desktop\Omrina.Desktop.csproj'
$projectNames = @('Omrina.Desktop', 'Omrina.Core', 'Omrina.Protocol', 'Omrina.Scanning', 'Omrina.Server', 'Omrina.Platform')
$packageConfiguration = 'Release'
$targetFramework = 'net10.0-windows10.0.26100.0'
$runtimeIdentifier = 'win-x64'
$packageCachePath = Join-Path $workspaceRoot '.tools\nuget-packages'
$minimumFreeBytes = 1.5GB
$artifactsRoot = Join-Path $workspaceRoot 'artifacts'
$packageRoot = Join-Path $artifactsRoot 'windows-dev'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'POWERSHELL_7_REQUIRED'
}

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'WINDOWS_HOST_REQUIRED'
}

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw 'DESKTOP_PROJECT_NOT_FOUND'
}

function Assert-NoReparsePoint([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'OUTPUT_PATH_REPARSE_POINT'
        }
    }
}

function Assert-PathChainNoReparsePoint([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $currentPath = [IO.Path]::GetPathRoot($fullPath)
    $relativePath = $fullPath.Substring($currentPath.Length)
    $segments = $relativePath.Split([char[]]@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)

    foreach ($segment in $segments) {
        $currentPath = Join-Path $currentPath $segment
        Assert-NoReparsePoint $currentPath
    }
}

function Assert-NoReparseTree([string]$DirectoryPath) {
    Assert-PathChainNoReparsePoint $DirectoryPath
    if (-not (Test-Path -LiteralPath $DirectoryPath -PathType Container)) {
        throw 'PRIVACY_SCAN_DIRECTORY_NOT_FOUND'
    }

    $pendingDirectories = [Collections.Generic.Stack[string]]::new()
    $pendingDirectories.Push([IO.Path]::GetFullPath($DirectoryPath))
    while ($pendingDirectories.Count -gt 0) {
        $currentDirectory = $pendingDirectories.Pop()
        foreach ($entryPath in [IO.Directory]::EnumerateFileSystemEntries($currentDirectory)) {
            $attributes = [IO.File]::GetAttributes($entryPath)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'OUTPUT_TREE_REPARSE_POINT'
            }

            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $pendingDirectories.Push($entryPath)
            }
        }
    }
}

function Assert-OutputRoots {
    $workspacePrefix = $workspaceRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $artifactsFullPath = [IO.Path]::GetFullPath($artifactsRoot)
    $packageFullPath = [IO.Path]::GetFullPath($packageRoot)
    $artifactsPrefix = $artifactsFullPath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar

    if ((-not $artifactsPrefix.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) -or (-not $packageFullPath.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase))) {
        throw 'OUTPUT_PATH_OUTSIDE_ARTIFACTS'
    }

    Assert-PathChainNoReparsePoint $workspaceRoot
    Assert-PathChainNoReparsePoint $artifactsRoot
    Assert-PathChainNoReparsePoint $packageRoot

    if ((Test-Path -LiteralPath $artifactsRoot) -and -not (Test-Path -LiteralPath $artifactsRoot -PathType Container)) {
        throw 'OUTPUT_ROOT_NOT_DIRECTORY'
    }
    if ((Test-Path -LiteralPath $packageRoot) -and -not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
        throw 'OUTPUT_ROOT_NOT_DIRECTORY'
    }

    if (-not (Test-Path -LiteralPath $artifactsRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $artifactsRoot -ErrorAction Stop | Out-Null
    }

    if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $packageRoot -ErrorAction Stop | Out-Null
    }

    Assert-PathChainNoReparsePoint $artifactsRoot
    Assert-PathChainNoReparsePoint $packageRoot
}

function Initialize-LocalPrivacyPatterns {
    $localRoots = [Collections.Generic.List[string]]::new()
    $localRoots.Add($workspaceRoot)
    $localRoots.Add([Environment]::UserName)
    foreach ($candidate in @(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile),
        $env:TEMP,
        $env:TMP,
        $env:APPDATA,
        $env:LOCALAPPDATA,
        $env:NUGET_PACKAGES,
        $env:DOTNET_ROOT
    )) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and [IO.Path]::IsPathRooted($candidate)) {
            $localRoots.Add([IO.Path]::GetFullPath($candidate))
        }
    }

    $pathVariants = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($localRoot in $localRoots) {
        $normalizedRoot = $localRoot.TrimEnd('\', '/')
        if ([string]::IsNullOrWhiteSpace($normalizedRoot)) {
            continue
        }

        $pathVariants.Add($normalizedRoot) | Out-Null
        $pathVariants.Add($normalizedRoot.ToLowerInvariant()) | Out-Null
        $pathVariants.Add($normalizedRoot.ToUpperInvariant()) | Out-Null
        $pathVariants.Add($normalizedRoot.Replace('\', '/')) | Out-Null
        $pathVariants.Add($normalizedRoot.Replace('\', '/').ToLowerInvariant()) | Out-Null
        $pathVariants.Add($normalizedRoot.Replace('\', '/').ToUpperInvariant()) | Out-Null
        try {
            if ([IO.Path]::IsPathRooted($normalizedRoot)) {
                $pathVariants.Add(([Uri]::new($normalizedRoot)).AbsoluteUri.TrimEnd('/')) | Out-Null
                $pathVariants.Add([Uri]::EscapeDataString($normalizedRoot.Replace('\', '/'))) | Out-Null
            }
        }
        catch {
            # A path variant that cannot be represented as a URI is still covered by its raw forms.
        }
    }

    $patterns = [Collections.Generic.List[byte[]]]::new()
    $encodings = @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode, [Text.Encoding]::BigEndianUnicode)
    foreach ($pathVariant in $pathVariants) {
        foreach ($encoding in $encodings) {
            $patterns.Add($encoding.GetBytes($pathVariant))
        }
    }

    $script:localPrivacyPatterns = $patterns.ToArray()
}

if (-not ('WorkspacePathLeakScanner' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;

public static class WorkspacePathLeakScanner
{
    public static bool FileContainsAny(string filePath, byte[][] patterns)
    {
        var maxPatternLength = 0;
        foreach (var pattern in patterns)
        {
            if (pattern.Length > maxPatternLength)
            {
                maxPatternLength = pattern.Length;
            }
        }

        if (maxPatternLength == 0)
        {
            return false;
        }

        var carryLimit = maxPatternLength - 1;
        var buffer = new byte[1024 * 1024 + carryLimit];
        var carry = 0;
        using (var stream = File.OpenRead(filePath))
        {
            int read;
            while ((read = stream.Read(buffer, carry, buffer.Length - carry)) > 0)
            {
                var length = carry + read;
                var content = buffer.AsSpan(0, length);
                foreach (var pattern in patterns)
                {
                    if (content.IndexOf(pattern) >= 0)
                    {
                        return true;
                    }
                }

                carry = Math.Min(carryLimit, length);
                if (carry > 0)
                {
                    Buffer.BlockCopy(buffer, length - carry, buffer, 0, carry);
                }
            }
        }

        return false;
    }
}
'@
}

function Get-LocalPrivacyLeaks([string]$DirectoryPath) {
    if (-not (Test-Path -LiteralPath $DirectoryPath -PathType Container)) {
        throw 'PRIVACY_SCAN_DIRECTORY_NOT_FOUND'
    }

    Assert-NoReparseTree $DirectoryPath
    $matches = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $DirectoryPath -Force -File -Recurse) {
        if ([WorkspacePathLeakScanner]::FileContainsAny($file.FullName, [byte[][]]$script:localPrivacyPatterns)) {
            $relativePath = [IO.Path]::GetRelativePath($DirectoryPath, $file.FullName).Replace('\', '/')
            $matches.Add($relativePath)
        }
    }

    return $matches.ToArray()
}

function ConvertTo-SafeDiagnosticDetail([string]$Text) {
    $safeText = $Text
    $localRoots = @(
        @{ Path = $workspaceRoot; Placeholder = '<WORKSPACE>' },
        @{ Path = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile); Placeholder = '<USER_PROFILE>' },
        @{ Path = $env:TEMP; Placeholder = '<TEMP>' },
        @{ Path = $env:TMP; Placeholder = '<TEMP>' },
        @{ Path = $env:APPDATA; Placeholder = '<APP_DATA>' },
        @{ Path = $env:LOCALAPPDATA; Placeholder = '<LOCAL_APP_DATA>' },
        @{ Path = $env:NUGET_PACKAGES; Placeholder = '<NUGET_PACKAGES>' },
        @{ Path = $env:DOTNET_ROOT; Placeholder = '<DOTNET_ROOT>' },
        @{ Path = $packageCachePath; Placeholder = '<NUGET_PACKAGES>' }
    )

    foreach ($localRoot in $localRoots) {
        if ([string]::IsNullOrWhiteSpace($localRoot.Path) -or -not [IO.Path]::IsPathRooted($localRoot.Path)) {
            continue
        }

        $rootVariants = @(
            [IO.Path]::GetFullPath($localRoot.Path).TrimEnd('\', '/'),
            [IO.Path]::GetFullPath($localRoot.Path).TrimEnd('\', '/').Replace('\', '/')
        )
        foreach ($rootVariant in $rootVariants) {
            if (-not [string]::IsNullOrWhiteSpace($rootVariant)) {
                $safeText = [regex]::Replace(
                    $safeText,
                    [regex]::Escape($rootVariant),
                    $localRoot.Placeholder,
                    [Text.RegularExpressions.RegexOptions]::IgnoreCase)
            }
        }
    }

    $safeText = [regex]::Replace($safeText, '(?i)file:///?[a-z]:[/\\][^\s"''<>|]+', '<LOCAL_PATH>')
    $safeText = [regex]::Replace($safeText, '(?i)(?:[a-z]:[/\\]|\\\\)[^\s"''<>|]+', '<LOCAL_PATH>')
    $safeText = [regex]::Replace(
        $safeText,
        '(?i)\b(access[_-]?token|refresh[_-]?token|token|password|secret|api[_-]?key|authorization|bearer|cookie|client[_-]?secret|credential)\b(\s*[:=]\s*)("[^"]*"|''[^'']*''|[^\s,;]+)',
        '$1$2<redacted>')
    $safeText = [regex]::Replace($safeText, '\s+', ' ').Trim()
    if ($safeText.Length -gt 300) {
        $safeText = $safeText.Substring(0, 300)
    }

    return $safeText
}

function Get-SafePublishDiagnostics([string]$LogPath, [string[]]$ProjectNames, [string]$Operation = 'PUBLISH') {
    if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        return @("${Operation}_DIAGNOSTIC=LOG_UNAVAILABLE")
    }

    $diagnosticPattern = [regex]::new('(?i)\b(?<severity>error|warning)(?:\s+(?<code>[A-Z][A-Z0-9]*\d{2,}))?\s*:')
    $errors = [Collections.Generic.List[string]]::new()
    $warnings = [Collections.Generic.List[string]]::new()
    $seenErrors = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $seenWarnings = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    foreach ($line in [IO.File]::ReadLines($LogPath)) {
        $match = $diagnosticPattern.Match($line)
        if (-not $match.Success) {
            continue
        }

        $projectFileName = 'unknown'
        foreach ($projectName in $ProjectNames) {
            $candidateFileName = "$projectName.csproj"
            if ($line.IndexOf($candidateFileName, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $projectFileName = $candidateFileName
                break
            }
        }

        $severity = $match.Groups['severity'].Value.ToUpperInvariant()
        $code = $match.Groups['code'].Value.ToUpperInvariant()
        if ([string]::IsNullOrWhiteSpace($code)) {
            $code = 'NO_CODE'
        }

        $diagnostic = "${Operation}_DIAGNOSTIC=$severity $code project=$projectFileName"
        if ($code -eq 'WMC9999') {
            $messageStart = $match.Index + $match.Length
            $detail = $line.Substring($messageStart).Trim()
            if (-not [string]::IsNullOrWhiteSpace($detail)) {
                $safeDetail = ConvertTo-SafeDiagnosticDetail $detail
                if (-not [string]::IsNullOrWhiteSpace($safeDetail)) {
                    $diagnostic += " message=$safeDetail"
                }
            }
        }

        if ($severity -eq 'ERROR') {
            if ($seenErrors.Add($diagnostic)) {
                $errors.Add($diagnostic)
            }
        }
        elseif ($seenWarnings.Add($diagnostic)) {
            $warnings.Add($diagnostic)
        }

        if ($errors.Count -ge 12) {
            break
        }
    }

    $selectedDiagnostics = [Collections.Generic.List[string]]::new()
    foreach ($diagnostic in $errors) {
        if ($selectedDiagnostics.Count -ge 12) {
            break
        }
        $selectedDiagnostics.Add($diagnostic)
    }
    foreach ($diagnostic in $warnings) {
        if ($selectedDiagnostics.Count -ge 12) {
            break
        }
        $selectedDiagnostics.Add($diagnostic)
    }

    if ($selectedDiagnostics.Count -eq 0) {
        return @("${Operation}_DIAGNOSTIC=NO_ERROR_OR_WARNING_CODE_EXTRACTED")
    }

    return $selectedDiagnostics.ToArray()
}

function Assert-RestoredProjectAssets([string[]]$ProjectNames, [string]$WorkDirectory, [string]$LocalFeedDirectory) {
    $targetSetMismatches = [Collections.Generic.List[string]]::new()
    $targetSummaries = [Collections.Generic.List[string]]::new()

    foreach ($projectName in $ProjectNames) {
        $assetsPath = Join-Path (Join-Path (Join-Path $WorkDirectory 'extensions') $projectName) 'project.assets.json'
        if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
            throw "ISOLATED_RELEASE_ASSETS_NOT_FOUND:$projectName"
        }

        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
        $restoreProperty = $assets.project.PSObject.Properties['restore']
        if ($null -eq $restoreProperty) {
            throw "ISOLATED_RELEASE_ASSETS_INVALID:$projectName"
        }

        $restore = $restoreProperty.Value
        $projectPathProperty = $restore.PSObject.Properties['projectPath']
        $sourcesProperty = $restore.PSObject.Properties['sources']
        if ($null -eq $projectPathProperty -or $null -eq $sourcesProperty) {
            throw "ISOLATED_RELEASE_RESTORE_METADATA_MISSING:$projectName"
        }

        if ($projectName -eq 'Omrina.Desktop') {
            $expectedProjectPath = $projectPath
            $expectedTargetFramework = $targetFramework
        }
        else {
            $expectedProjectPath = Join-Path (Join-Path (Join-Path $workspaceRoot 'src') $projectName) "$projectName.csproj"
            $expectedTargetFramework = 'net10.0'
        }

        if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
                [IO.Path]::GetFullPath([string]$projectPathProperty.Value),
                [IO.Path]::GetFullPath($expectedProjectPath))) {
            throw "ISOLATED_RELEASE_PROJECT_PATH_MISMATCH:$projectName"
        }

        $sourcesValue = $sourcesProperty.Value
        if ($sourcesValue -is [Collections.IDictionary]) {
            $sourceNames = @($sourcesValue.Keys)
        }
        elseif ($sourcesValue -is [Array]) {
            $sourceNames = @($sourcesValue)
        }
        else {
            $sourceNames = @($sourcesValue.PSObject.Properties | ForEach-Object { $_.Name })
        }

        $expectedSource = [IO.Path]::GetFullPath($LocalFeedDirectory).TrimEnd('\', '/')
        if ($sourceNames.Count -ne 1 -or
            -not [StringComparer]::OrdinalIgnoreCase.Equals(
                [IO.Path]::GetFullPath(([string]$sourceNames[0]).TrimEnd('\', '/')),
                $expectedSource)) {
            throw "ISOLATED_RELEASE_SOURCE_SET_MISMATCH:$projectName"
        }

        $frameworkTargetKey = $expectedTargetFramework
        $ridTargetKey = "$expectedTargetFramework/$runtimeIdentifier"
        if ($projectName -eq 'Omrina.Desktop') {
            $allowedTargetKeys = @($targetFramework, 'net10.0-desktop')
            foreach ($declaredRid in @('win-x86', 'win-x64', 'win-arm64')) {
                $allowedTargetKeys += "$targetFramework/$declaredRid"
                $allowedTargetKeys += "net10.0-desktop/$declaredRid"
            }
        }
        else {
            $allowedTargetKeys = @($frameworkTargetKey, $ridTargetKey)
        }

        $targetKeys = @($assets.targets.PSObject.Properties.Name)
        $safeTargetKeys = [Collections.Generic.List[string]]::new()
        foreach ($targetKey in $targetKeys) {
            if ($targetKey.Length -le 100 -and $targetKey -match '^[a-zA-Z0-9][a-zA-Z0-9.+/-]*$' -and $safeTargetKeys.Count -lt 12) {
                $safeTargetKeys.Add($targetKey)
            }
        }
        $targetSummaries.Add("$projectName=$($safeTargetKeys -join ',')")

        $unexpectedTargetKeys = @($targetKeys | Where-Object { $_ -notin $allowedTargetKeys })
        if ($targetKeys.Count -eq 0 -or $unexpectedTargetKeys.Count -gt 0) {
            $targetSetMismatches.Add($projectName)
        }

        if ($projectName -eq 'Omrina.Desktop' -and "$targetFramework/$runtimeIdentifier" -notin $targetKeys) {
            if (-not $targetSetMismatches.Contains($projectName)) {
                $targetSetMismatches.Add($projectName)
            }
        }

        if ($projectName -ne 'Omrina.Desktop' -and
            $frameworkTargetKey -notin $targetKeys -and
            $ridTargetKey -notin $targetKeys) {
            if (-not $targetSetMismatches.Contains($projectName)) {
                $targetSetMismatches.Add($projectName)
            }
        }
    }

    if ($targetSetMismatches.Count -gt 0) {
        throw "ISOLATED_RELEASE_TFM_TARGET_SET_MISMATCH:projects=$($targetSetMismatches -join ',');targets=$($targetSummaries -join ';')"
    }
}

function Assert-ReleaseDevelopmentImportsExcluded([string]$WorkDirectory) {
    $desktopExtensionsDirectory = Join-Path (Join-Path $WorkDirectory 'extensions') 'Omrina.Desktop'
    $developmentImportMarkers = @(
        'Uno.WinUI.DevServer.targets',
        'Uno.UI.HotDesign.props',
        'Uno.UI.HotDesign.targets',
        'Uno.UI.App.Mcp.targets'
    )

    foreach ($importFileName in @('Omrina.Desktop.csproj.nuget.g.props', 'Omrina.Desktop.csproj.nuget.g.targets')) {
        $importPath = Join-Path $desktopExtensionsDirectory $importFileName
        if (-not (Test-Path -LiteralPath $importPath -PathType Leaf)) {
            throw "ISOLATED_RELEASE_NUGET_IMPORT_FILE_MISSING:$importFileName"
        }

        $importContents = Get-Content -LiteralPath $importPath -Raw
        foreach ($marker in $developmentImportMarkers) {
            if ($importContents.Contains($marker, [StringComparison]::OrdinalIgnoreCase)) {
                throw "RELEASE_DEVELOPMENT_IMPORT_PRESENT:$marker"
            }
        }
    }
}

function Remove-OwnedStagingDirectory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return
    }

    $resolvedPath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
    $packagePrefix = [IO.Path]::GetFullPath($packageRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $stageName = [IO.Path]::GetFileName($resolvedPath)
    if ((-not $resolvedPath.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) -or ($stageName -notmatch '^\.staging-[a-f0-9]{32}$')) {
        throw 'REFUSED_UNOWNED_STAGING_CLEANUP'
    }

    Assert-NoReparseTree $resolvedPath
    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}

function Remove-OwnedStagingWorkDirectory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return
    }

    $resolvedPath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
    $stagingPathFull = [IO.Directory]::GetParent($resolvedPath).FullName
    $stageName = [IO.Path]::GetFileName($stagingPathFull)
    $workName = [IO.Path]::GetFileName($resolvedPath)
    $packagePrefix = [IO.Path]::GetFullPath($packageRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($workName -ne 'work' -or (-not $stagingPathFull.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) -or ($stageName -notmatch '^\.staging-[a-f0-9]{32}$')) {
        throw 'REFUSED_UNOWNED_STAGING_WORK_CLEANUP'
    }

    Assert-NoReparseTree $stagingPathFull
    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}

function Get-PackageRuntimeRequirements([string]$RuntimeConfigPath) {
    $runtimeConfig = Get-Content -LiteralPath $RuntimeConfigPath -Raw | ConvertFrom-Json
    $runtimeOptions = $runtimeConfig.runtimeOptions
    $frameworksProperty = $runtimeOptions.PSObject.Properties['frameworks']
    $frameworkProperty = $runtimeOptions.PSObject.Properties['framework']
    $includedFrameworksProperty = $runtimeOptions.PSObject.Properties['includedFrameworks']
    if ($null -eq $frameworksProperty -or $null -ne $frameworkProperty) {
        throw 'EXPECTED_FRAMEWORK_DEPENDENT_RUNTIME_CONFIG'
    }

    $frameworks = @($frameworksProperty.Value)
    $requiredNames = @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')
    foreach ($requiredName in $requiredNames) {
        if (-not ($frameworks | Where-Object { $_.name -eq $requiredName })) {
            throw 'REQUIRED_SHARED_FRAMEWORK_MISSING'
        }
    }

    $requestedFrameworks = foreach ($framework in $frameworks) {
        if ($framework.version -notmatch '^10\.') {
            throw 'UNEXPECTED_DOTNET_FRAMEWORK_VERSION'
        }

        '{0} {1}' -f $framework.name, $framework.version
    }

    if ($null -ne $includedFrameworksProperty) {
        throw 'EXPECTED_FRAMEWORK_DEPENDENT_RUNTIME_CONFIG'
    }

    return @($requestedFrameworks)
}

function Assert-RequiredPackageFiles([string]$AppDirectory) {
    $requiredFiles = @(
        'Omrina.Desktop.exe',
        'Omrina.Desktop.dll',
        'Omrina.Desktop.deps.json',
        'Omrina.Desktop.runtimeconfig.json',
        'Omrina.Desktop.pri',
        'Microsoft.WindowsAppRuntime.Bootstrap.dll',
        'Microsoft.WindowsAppRuntime.Bootstrap.Net.dll',
        'Microsoft.WindowsAppRuntime.dll',
        'Microsoft.WindowsAppRuntime.pri',
        'Microsoft.UI.pri',
        'Microsoft.ui.xaml.dll',
        'NAPS2.Sdk.dll',
        'NAPS2.Sdk.Worker.Win32.dll',
        'NAPS2.Worker.exe',
        'SkiaSharp.dll',
        'libSkiaSharp.dll'
    )

    foreach ($fileName in $requiredFiles) {
        $filePath = Join-Path $AppDirectory $fileName
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "REQUIRED_PACKAGE_FILE_MISSING:$fileName"
        }

        if ((Get-Item -LiteralPath $filePath).Length -le 0) {
            throw "REQUIRED_PACKAGE_FILE_EMPTY:$fileName"
        }
    }
}

try {
    Assert-OutputRoots
    Initialize-LocalPrivacyPatterns

    if ($TestPrivacyScanner -and -not [string]::IsNullOrWhiteSpace($AuditExistingPublishDirectory)) {
        throw 'PRIVACY_SCANNER_TEST_AND_AUDIT_ARE_MUTUALLY_EXCLUSIVE'
    }
    if ($TestPublishDiagnostics -and -not [string]::IsNullOrWhiteSpace($AuditExistingPublishDirectory)) {
        throw 'PUBLISH_DIAGNOSTICS_TEST_AND_AUDIT_ARE_MUTUALLY_EXCLUSIVE'
    }
    if ($TestPrivacyScanner -and $TestPublishDiagnostics) {
        throw 'PUBLISH_DIAGNOSTICS_TEST_AND_PRIVACY_SCANNER_TEST_ARE_MUTUALLY_EXCLUSIVE'
    }

    $probeDirectory = $null
    if (-not [string]::IsNullOrWhiteSpace($AuditExistingPublishDirectory)) {
        $auditPath = [IO.Path]::GetFullPath($AuditExistingPublishDirectory)
        $auditItem = Get-Item -LiteralPath $auditPath -Force
        $expectedProbeParent = [IO.Directory]::GetParent($auditPath).FullName
        $expectedProbe = [IO.Path]::GetFileName($expectedProbeParent)
        $expectedTempPrefix = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        $expectedProbeParentFull = [IO.Path]::GetFullPath($expectedProbeParent)
        if (($auditItem.PSIsContainer -ne $true) -or ($auditItem.Name -ne 'payload') -or ($expectedProbe -notmatch '^omrina-m4-resolve-probe-[a-f0-9]{32}$') -or (-not $expectedProbeParentFull.StartsWith($expectedTempPrefix, [StringComparison]::OrdinalIgnoreCase))) {
            throw 'AUDIT_PATH_MUST_BE_AN_OMRINA_TEMP_PUBLISH_PROBE'
        }

        $tempPrefix = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $auditPath.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'AUDIT_PATH_MUST_BE_AN_OMRINA_TEMP_PUBLISH_PROBE'
        }

        Assert-PathChainNoReparsePoint $auditPath
        Assert-NoReparseTree $auditPath
        $probeDirectory = $auditPath
    }
    elseif (-not $TestPrivacyScanner -and -not $TestPublishDiagnostics) {
        $driveRoot = [IO.Path]::GetPathRoot($packageRoot)
        $availableBytes = [IO.DriveInfo]::new($driveRoot).AvailableFreeSpace
        if ($availableBytes -lt $minimumFreeBytes) {
            throw 'INSUFFICIENT_DISK_SPACE; at least 1.5 GiB must be available before publishing.'
        }
    }

    $operationId = [Guid]::NewGuid().ToString('N')
    $stagingPath = Join-Path $packageRoot ".staging-$operationId"
    $finalPath = Join-Path $packageRoot "omrina-windows-x64-dev-release-$operationId"
    if ((Test-Path -LiteralPath $stagingPath) -or (Test-Path -LiteralPath $finalPath)) {
        throw 'OUTPUT_ALREADY_EXISTS'
    }

    New-Item -ItemType Directory -Path $stagingPath -ErrorAction Stop | Out-Null
    Assert-PathChainNoReparsePoint $stagingPath
    try {
        if ($TestPublishDiagnostics) {
            $syntheticLogPath = Join-Path $stagingPath 'synthetic-publish.log'
            $syntheticLines = @(
                "C:\Users\fixture-user\source\repo\apps\desktop\Omrina.Desktop.csproj : warning NU1900: local path $workspaceRoot and marker SYNTHETIC_SECRET_MARKER",
                "$workspaceRoot\src\Omrina.Scanning\Omrina.Scanning.csproj(27,3): error NETSDK1004: local path C:\Users\fixture-user\source\repo [C:\Users\fixture-user\source\repo\src\Omrina.Scanning\Omrina.Scanning.csproj]",
                "$workspaceRoot\src\Omrina.Scanning\Omrina.Scanning.csproj(29,3): error WMC9999: Synthetic XAML compile failure; workspace=$workspaceRoot\a.xaml; drive=C:\Users\fixture-user\private.xaml; unc=\\fixture-server\fixture-share\private.xaml; token=SYN_TOKEN; password=SYN_PASS; secret=SYN_SECRET; api-key=SYN_API_KEY; authorization=SYN_AUTH; bearer=SYN_BEARER; cookie=SYN_COOKIE; client_secret=SYN_CLIENT_SECRET; credential=SYN_CREDENTIAL"
            )
            [IO.File]::WriteAllLines($syntheticLogPath, $syntheticLines, [Text.UTF8Encoding]::new($false))

            $diagnostics = @(Get-SafePublishDiagnostics $syntheticLogPath $projectNames)
            $diagnosticSummary = $diagnostics -join "`n"
            $syntheticSensitiveMarkers = @(
                'SYN_TOKEN', 'SYN_PASS', 'SYN_SECRET', 'SYN_API_KEY', 'SYN_AUTH',
                'SYN_BEARER', 'SYN_COOKIE', 'SYN_CLIENT_SECRET', 'SYN_CREDENTIAL'
            )
            $unredactedMarkers = @($syntheticSensitiveMarkers | Where-Object { $diagnosticSummary.Contains($_) })
            if ($diagnostics.Count -ne 3 -or
                $diagnostics[0] -ne 'PUBLISH_DIAGNOSTIC=ERROR NETSDK1004 project=Omrina.Scanning.csproj' -or
                -not $diagnostics[1].StartsWith('PUBLISH_DIAGNOSTIC=ERROR WMC9999 project=Omrina.Scanning.csproj message=Synthetic XAML compile failure') -or
                $diagnostics[2] -ne 'PUBLISH_DIAGNOSTIC=WARNING NU1900 project=Omrina.Desktop.csproj' -or
                $diagnosticSummary.IndexOf($workspaceRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $diagnosticSummary.Contains('fixture-user') -or
                $diagnosticSummary.Contains('fixture-server') -or
                $diagnosticSummary.Contains('SYNTHETIC_SECRET_MARKER') -or
                $unredactedMarkers.Count -gt 0 -or
                $diagnostics[1] -match '(?i)(?:[a-z]:[\\/]|\\\\)' -or
                [regex]::Matches($diagnostics[1], '<redacted>').Count -lt $syntheticSensitiveMarkers.Count) {
                throw 'PUBLISH_DIAGNOSTICS_SELFTEST_FAILED'
            }

            $syntheticDetail = "Xaml compile failed in $workspaceRoot\apps\desktop\MainWindow.xaml; cache C:\Users\fixture-user\.nuget\packages\secret\Xaml.dll; token=synthetic-token"
            $safeDetail = ConvertTo-SafeDiagnosticDetail $syntheticDetail
            if ($safeDetail.IndexOf($workspaceRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $safeDetail.Contains('fixture-user') -or
                $safeDetail.Contains('synthetic-token') -or
                $safeDetail -match '(?i)(?:[a-z]:[\\/]|\\\\)' -or
                -not $safeDetail.Contains('<WORKSPACE>') -or
                -not $safeDetail.Contains('<LOCAL_PATH>') -or
                -not $safeDetail.Contains('<redacted>')) {
                throw 'PUBLISH_DIAGNOSTIC_REDACTION_SELFTEST_FAILED'
            }

            Remove-OwnedStagingDirectory $stagingPath
            $stagingPath = $null
            Write-Output 'PUBLISH_DIAGNOSTICS_SELFTEST_PASSED'
            return
        }

        if ($TestPrivacyScanner) {
            $hiddenDirectory = [IO.Directory]::CreateDirectory((Join-Path $stagingPath 'hidden'))
            $hiddenDirectory.Attributes = $hiddenDirectory.Attributes -bor [IO.FileAttributes]::Hidden
            $syntheticPath = Join-Path $hiddenDirectory.FullName 'embedded-path.bin'
            [IO.File]::WriteAllText($syntheticPath, 'synthetic safe payload', [Text.Encoding]::UTF8)

            $emptyResult = @(Get-LocalPrivacyLeaks $stagingPath)
            if ($emptyResult.Count -ne 0) {
                throw 'PRIVACY_SCANNER_EMPTY_RESULT_FAILED'
            }

            [IO.File]::WriteAllText($syntheticPath, $workspaceRoot, [Text.Encoding]::Unicode)
            $hitResult = @(Get-LocalPrivacyLeaks $stagingPath)
            if ($hitResult.Count -ne 1 -or $hitResult[0] -ne 'hidden/embedded-path.bin') {
                throw 'PRIVACY_SCANNER_MATCH_RESULT_FAILED'
            }

            [IO.File]::WriteAllText($syntheticPath, 'synthetic safe payload', [Text.Encoding]::UTF8)
            if (@(Get-LocalPrivacyLeaks $stagingPath).Count -ne 0) {
                throw 'PRIVACY_SCANNER_CLEAR_RESULT_FAILED'
            }

            Remove-OwnedStagingDirectory $stagingPath
            $stagingPath = $null
            Write-Output 'PRIVACY_SCANNER_SELFTEST_PASSED'
            return
        }

        if ($probeDirectory) {
            $leakedFiles = @(Get-LocalPrivacyLeaks $probeDirectory)
            if ($leakedFiles.Count -gt 0) {
                throw "PRIVACY_PATH_EMBEDDED:$($leakedFiles -join ',')"
            }

            Remove-OwnedStagingDirectory $stagingPath
            $stagingPath = $null
            Write-Output 'Privacy gate passed for the existing local publish probe. No package was created.'
            return
        }

        $dotnetVersion = (& dotnet --version 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $dotnetVersion -notmatch '^10\.') {
            throw 'DOTNET_SDK_10_REQUIRED'
        }
        if (-not (Test-Path -LiteralPath $packageCachePath -PathType Container)) {
            throw 'LOCAL_NUGET_PACKAGE_CACHE_NOT_FOUND'
        }
        Assert-PathChainNoReparsePoint $packageCachePath

        $appDirectory = Join-Path $stagingPath 'app'
        $workDirectory = Join-Path $stagingPath 'work'
        $isolatedPathProps = Join-Path $workDirectory 'isolated-restore.props'
        $localFeedDirectory = Join-Path $workDirectory 'local-feed'
        $nugetConfigPath = Join-Path $workDirectory 'offline-nuget.config'
        $restoreLogPath = Join-Path $workDirectory 'restore.log'
        $publishLogPath = Join-Path $workDirectory 'publish.log'
        $null = [IO.Directory]::CreateDirectory($appDirectory)
        $null = [IO.Directory]::CreateDirectory($workDirectory)
        $null = [IO.Directory]::CreateDirectory($localFeedDirectory)

        $propsText = @'
<Project>
  <PropertyGroup>
    <BaseIntermediateOutputPath>$(MSBuildThisFileDirectory)intermediate/$(MSBuildProjectName)/</BaseIntermediateOutputPath>
    <MSBuildProjectExtensionsPath>$(MSBuildThisFileDirectory)extensions/$(MSBuildProjectName)/</MSBuildProjectExtensionsPath>
    <BaseOutputPath>$(MSBuildThisFileDirectory)build/$(MSBuildProjectName)/</BaseOutputPath>
    <DefaultItemExcludes>$(DefaultItemExcludes);obj/**;bin/**;**/obj/**;**/bin/**</DefaultItemExcludes>
    <DefaultItemExcludes Condition="'$(MSBuildProjectName)' == 'Omrina.Desktop'">$(DefaultItemExcludes);tmp/**</DefaultItemExcludes>
  </PropertyGroup>
</Project>
'@
        [IO.File]::WriteAllText($isolatedPathProps, $propsText, [Text.UTF8Encoding]::new($false))

        $escapedLocalFeedDirectory = [System.Security.SecurityElement]::Escape($localFeedDirectory)
        $nugetConfigLines = @(
            '<?xml version="1.0" encoding="utf-8"?>',
            '<configuration>',
            '  <packageSources>',
            '    <clear />',
            "    <add key=`"offline-empty`" value=`"$escapedLocalFeedDirectory`" />",
            '  </packageSources>',
            '</configuration>'
        )
        [IO.File]::WriteAllLines($nugetConfigPath, $nugetConfigLines, [Text.UTF8Encoding]::new($false))

        $pathMap = "$workspaceRoot=."
        $restoreArguments = @(
            'restore',
            $projectPath,
            '--source', $localFeedDirectory,
            '--configfile', $nugetConfigPath,
            '--packages', $packageCachePath,
            '--verbosity', 'minimal',
            "-p:Configuration=$packageConfiguration",
            "-p:RuntimeIdentifier=$runtimeIdentifier",
            '-p:Platform=x64',
            '-p:Optimize=true',
            '-p:SelfContained=false',
            '-p:WindowsAppSDKSelfContained=true',
            '-p:UnoDisableMCPSupport=true',
            '-p:UnoDisableHotDesign=true',
            '-p:UnoDisableHotDesignAgent=true',
            '-p:HotDesignPreviewsFolder=',
            '-p:ApplicationPreviewsFolder=.',
            '-p:HotDesignSolutionDir=.',
            '-p:NuGetAudit=false',
            '-p:BuildProjectReferencesInParallel=false',
            "-p:CustomAfterDirectoryBuildProps=$isolatedPathProps"
        )
        & dotnet @restoreArguments *> $restoreLogPath
        $restoreExitCode = $LASTEXITCODE
        if ($restoreExitCode -ne 0) {
            foreach ($diagnostic in @(Get-SafePublishDiagnostics $restoreLogPath $projectNames 'RESTORE')) {
                [Console]::Error.WriteLine($diagnostic)
            }
            throw "RESTORE_FAILED:$restoreExitCode"
        }

        Assert-RestoredProjectAssets $projectNames $workDirectory $localFeedDirectory
        Assert-ReleaseDevelopmentImportsExcluded $workDirectory

        $publishArguments = @(
            'publish',
            $projectPath,
            '--no-restore',
            '--configuration', $packageConfiguration,
            '--framework', $targetFramework,
            '--runtime', $runtimeIdentifier,
            '--self-contained', 'false',
            '-p:Platform=x64',
            '-p:Optimize=true',
            '-p:SelfContained=false',
            '-p:WindowsAppSDKSelfContained=true',
            '-p:PublishProfile=',
            '-p:DebugSymbols=false',
            '-p:DebugType=None',
            '-p:UnoDisableMCPSupport=true',
            '-p:UnoDisableHotDesign=true',
            '-p:UnoDisableHotDesignAgent=true',
            '-p:HotDesignPreviewsFolder=',
            '-p:ApplicationPreviewsFolder=.',
            '-p:HotDesignSolutionDir=.',
            '-p:NuGetAudit=false',
            '-p:BuildProjectReferences=true',
            '-p:BuildProjectReferencesInParallel=false',
            "-p:CustomAfterDirectoryBuildProps=$isolatedPathProps",
            "-p:PathMap=$pathMap",
            '--output', $appDirectory
        )

        & dotnet @publishArguments *> $publishLogPath
        $publishExitCode = $LASTEXITCODE
        if ($publishExitCode -ne 0) {
            foreach ($diagnostic in @(Get-SafePublishDiagnostics $publishLogPath $projectNames 'PUBLISH')) {
                [Console]::Error.WriteLine($diagnostic)
            }
            throw "PUBLISH_FAILED:$publishExitCode"
        }

        Get-ChildItem -LiteralPath $appDirectory -Force -File -Recurse -Filter '*.pdb' |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

        Assert-RequiredPackageFiles $appDirectory
        $requestedFrameworks = @(Get-PackageRuntimeRequirements (Join-Path $appDirectory 'Omrina.Desktop.runtimeconfig.json'))

        $runtimeReadmeLines = @($requestedFrameworks | ForEach-Object { "- $_" })
        $packageReadme = @(
            'OMRINA Windows x64 developer validation package',
            '',
            'This is a local Release build for development validation. It is not an installer or a signed product release.',
            'Configuration: Release; target: net10.0-windows10.0.26100.0; RID: win-x64.',
            'The application is .NET framework-dependent. Its runtimeconfig requests:',
            ($runtimeReadmeLines -join "`n"),
            'Install compatible .NET 10 shared frameworks before launching the application.',
            'WindowsAppSDKSelfContained=true includes the Windows App SDK runtime payload with this package; it does not make the .NET application self-contained.',
            'The package does not install system components, drivers, or an installer. No physical scan/print or clean-machine compatibility validation is implied.',
            'Uno MCP and Hot Design development agents are disabled for this validation build.',
            'Dependency license completeness remains to be reviewed before external distribution.',
            'Verify SHA256SUMS.txt before inspecting or transferring the package.'
        ) -join "`n"
        [IO.File]::WriteAllText(
            (Join-Path $stagingPath 'README.txt'),
            $packageReadme + "`n",
            [Text.UTF8Encoding]::new($false))

        Remove-OwnedStagingWorkDirectory (Join-Path $stagingPath 'work')

        $manifestPath = Join-Path $stagingPath 'SHA256SUMS.txt'
        $manifestLines = [Collections.Generic.List[string]]::new()
        $packageFiles = Get-ChildItem -LiteralPath $stagingPath -Force -File -Recurse |
            Where-Object { -not [StringComparer]::OrdinalIgnoreCase.Equals($_.FullName, $manifestPath) } |
            Sort-Object { [IO.Path]::GetRelativePath($stagingPath, $_.FullName).Replace('\', '/') } -CaseSensitive

        foreach ($file in $packageFiles) {
            $relativePath = [IO.Path]::GetRelativePath($stagingPath, $file.FullName).Replace('\', '/')
            if ([IO.Path]::IsPathRooted($relativePath) -or $relativePath.Contains(':') -or ($relativePath -match '(^|/)\.\.?(/|$)')) {
                throw 'MANIFEST_PATH_NOT_RELATIVE'
            }

            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            $manifestLines.Add("$hash  $relativePath")
        }

        $manifestText = ($manifestLines -join "`n") + "`n"
        if ($manifestText -match '%[A-Za-z_][A-Za-z0-9_]*%|\$env:') {
            throw 'MANIFEST_CONTAINS_LOCAL_PATH_OR_ENVIRONMENT_REFERENCE'
        }

        [IO.File]::WriteAllText($manifestPath, $manifestText, [Text.UTF8Encoding]::new($false))

        $leakedFiles = @(Get-LocalPrivacyLeaks $stagingPath)
        if ($leakedFiles.Count -gt 0) {
            throw "PRIVACY_PATH_EMBEDDED:$($leakedFiles -join ',')"
        }

        [IO.Directory]::Move($stagingPath, $finalPath)
        $stagingPath = $null
        Write-Output "PACKAGE_CREATED=$([IO.Path]::GetRelativePath($workspaceRoot, $finalPath).Replace('\', '/'))"
    }
    catch {
        if ($stagingPath -and (Test-Path -LiteralPath $stagingPath -PathType Container)) {
            try {
                Remove-OwnedStagingDirectory $stagingPath
            }
            catch {
                [Console]::Error.WriteLine("PACKAGE_CLEANUP_FAILED:$($_.Exception.GetType().Name)")
            }
        }
        throw
    }
}
catch {
    $message = $_.Exception.Message
    if ($message -match '^(PRIVACY_PATH_EMBEDDED|PRIVACY_SCANNER_|PUBLISH_DIAGNOSTICS_|RESTORE_FAILED|PUBLISH_FAILED|ISOLATED_RELEASE_|RELEASE_DEVELOPMENT_IMPORT_PRESENT|LOCAL_NUGET_PACKAGE_CACHE_NOT_FOUND|REQUIRED_PACKAGE_FILE_MISSING|REQUIRED_PACKAGE_FILE_EMPTY|MANIFEST_PATH_NOT_RELATIVE|MANIFEST_CONTAINS_LOCAL_PATH_OR_ENVIRONMENT_REFERENCE|INSUFFICIENT_DISK_SPACE|OUTPUT_PATH_REPARSE_POINT|OUTPUT_TREE_REPARSE_POINT|OUTPUT_ROOT_NOT_DIRECTORY|OUTPUT_PATH_OUTSIDE_ARTIFACTS|OUTPUT_ALREADY_EXISTS|REFUSED_UNOWNED_STAGING|AUDIT_PATH_MUST_BE_AN_OMRINA_TEMP_PUBLISH_PROBE)') {
        [Console]::Error.WriteLine("PACKAGE_FAILED:$message")
    }
    else {
        [Console]::Error.WriteLine("PACKAGE_FAILED:$($_.Exception.GetType().Name):SCRIPT_LINE=$($_.InvocationInfo.ScriptLineNumber)")
    }

    exit 1
}
