[CmdletBinding()]
param(
    [string]$AuditExistingPublishDirectory,
    [switch]$TestPrivacyScanner
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $workspaceRoot 'apps\desktop\Omrina.Desktop.csproj'
$targetFramework = 'net10.0-windows10.0.26100.0'
$runtimeIdentifier = 'win-x64'
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

if (-not (Test-Path -LiteralPath (Join-Path $workspaceRoot 'apps\desktop\obj\project.assets.json') -PathType Leaf)) {
    throw 'RESTORED_ASSETS_NOT_FOUND; run the documented offline restore before packaging.'
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
    elseif (-not $TestPrivacyScanner) {
        $driveRoot = [IO.Path]::GetPathRoot($packageRoot)
        $availableBytes = [IO.DriveInfo]::new($driveRoot).AvailableFreeSpace
        if ($availableBytes -lt $minimumFreeBytes) {
            throw 'INSUFFICIENT_DISK_SPACE; at least 1.5 GiB must be available before publishing.'
        }
    }

    $operationId = [Guid]::NewGuid().ToString('N')
    $stagingPath = Join-Path $packageRoot ".staging-$operationId"
    $finalPath = Join-Path $packageRoot "omrina-windows-x64-debug-$operationId"
    if ((Test-Path -LiteralPath $stagingPath) -or (Test-Path -LiteralPath $finalPath)) {
        throw 'OUTPUT_ALREADY_EXISTS'
    }

    New-Item -ItemType Directory -Path $stagingPath -ErrorAction Stop | Out-Null
    Assert-PathChainNoReparsePoint $stagingPath
    try {
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

        $appDirectory = Join-Path $stagingPath 'app'
        $workDirectory = Join-Path $stagingPath 'work'
        $isolatedPathProps = Join-Path $workDirectory 'isolated-paths.props'
        $publishLogPath = Join-Path $workDirectory 'publish.log'
        $null = [IO.Directory]::CreateDirectory($appDirectory)
        $null = [IO.Directory]::CreateDirectory($workDirectory)

        $projectNames = @('Omrina.Desktop', 'Omrina.Core', 'Omrina.Protocol', 'Omrina.Scanning', 'Omrina.Server', 'Omrina.Platform')
        $propsLines = [Collections.Generic.List[string]]::new()
        $propsLines.Add('<Project>')
        $propsLines.Add('  <PropertyGroup>')
        foreach ($projectName in $projectNames) {
            $condition = "'`$(MSBuildProjectName)' == '$projectName'"
            $propsLines.Add("    <IntermediateOutputPath Condition=`"$condition`">`$(MSBuildThisFileDirectory)intermediate\$projectName\`$(Configuration)\`$(TargetFramework)\</IntermediateOutputPath>")
            $propsLines.Add("    <OutputPath Condition=`"$condition`">`$(MSBuildThisFileDirectory)build\$projectName\`$(Configuration)\`$(TargetFramework)\</OutputPath>")
        }
        $propsLines.Add('  </PropertyGroup>')
        $propsLines.Add('</Project>')
        [IO.File]::WriteAllLines($isolatedPathProps, $propsLines, [Text.UTF8Encoding]::new($false))

        $pathMap = "$workspaceRoot=."
        $publishArguments = @(
            'publish',
            $projectPath,
            '--no-restore',
            '--configuration', 'Debug',
            '--framework', $targetFramework,
            '--runtime', $runtimeIdentifier,
            '--self-contained', 'false',
            '-p:SelfContained=false',
            '-p:WindowsAppSDKSelfContained=true',
            '-p:PublishProfile=',
            '-p:DebugSymbols=false',
            '-p:DebugType=None',
            '-p:UnoDisableMCPSupport=true',
            '-p:UnoDisableHotDesignAgent=true',
            '-p:HotDesignPreviewsFolder=',
            '-p:ApplicationPreviewsFolder=.',
            '-p:HotDesignSolutionDir=.',
            '-p:NuGetAudit=false',
            '-p:BuildProjectReferences=true',
            '-p:BuildProjectReferencesInParallel=false',
            "-p:CustomAfterMicrosoftCommonProps=$isolatedPathProps",
            "-p:PathMap=$pathMap",
            '--output', $appDirectory
        )

        & dotnet @publishArguments *> $publishLogPath
        $publishExitCode = $LASTEXITCODE
        if ($publishExitCode -ne 0) {
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
            'This is a local Debug build for development validation. It is not an installer or a signed release.',
            'Configuration: Debug; target: net10.0-windows10.0.26100.0; RID: win-x64.',
            'The application is .NET framework-dependent. Its runtimeconfig requests:',
            $runtimeReadmeLines,
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
    if ($message -match '^(PRIVACY_PATH_EMBEDDED|PRIVACY_SCANNER_|PUBLISH_FAILED|REQUIRED_PACKAGE_FILE_MISSING|REQUIRED_PACKAGE_FILE_EMPTY|MANIFEST_PATH_NOT_RELATIVE|MANIFEST_CONTAINS_LOCAL_PATH_OR_ENVIRONMENT_REFERENCE|INSUFFICIENT_DISK_SPACE|OUTPUT_PATH_REPARSE_POINT|OUTPUT_TREE_REPARSE_POINT|OUTPUT_ROOT_NOT_DIRECTORY|OUTPUT_PATH_OUTSIDE_ARTIFACTS|OUTPUT_ALREADY_EXISTS|REFUSED_UNOWNED_STAGING|AUDIT_PATH_MUST_BE_AN_OMRINA_TEMP_PUBLISH_PROBE)') {
        [Console]::Error.WriteLine("PACKAGE_FAILED:$message")
    }
    else {
        [Console]::Error.WriteLine("PACKAGE_FAILED:$($_.Exception.GetType().Name):SCRIPT_LINE=$($_.InvocationInfo.ScriptLineNumber)")
    }

    exit 1
}
