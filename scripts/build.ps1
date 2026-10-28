#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$GameRoot = $env:BEAT_SABER_DIR,
    [string]$BuildDirectory = '',
    [string]$OutputDirectory = '',
    [string]$DotnetPath = 'C:/Program Files/dotnet/dotnet.exe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build_local_paths.ps1')

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-AssemblyVersion([string]$Path) {
    try {
        return ([Reflection.AssemblyName]::GetAssemblyName($Path).Version).ToString()
    } catch {
        return ''
    }
}

function Resolve-Absolute([string]$Path) {
    return [IO.Path]::GetFullPath($Path)
}

function Assert-File([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file is missing: $Path"
    }
}

function Write-Utf8NoBom([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
}

if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    $BuildDirectory = Join-Path $RepoRoot '.tmp/beatsage-mod/build'
} else {
    $BuildDirectory = Resolve-RepoBuildPath $BuildDirectory
}
if (![string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Resolve-RepoBuildPath $OutputDirectory
}
if ($OutputDirectory -and (Test-Path -LiteralPath (Join-Path $OutputDirectory 'Plugins') -PathType Container)) {
    $otherPlugins = @(Get-ChildItem -LiteralPath (Join-Path $OutputDirectory 'Plugins') -File -Filter '*.dll' | Where-Object Name -ne 'BeatSage.dll')
    if ($otherPlugins.Count) { throw 'OutputDirectory contains another plugin DLL; use a clean package directory.' }
}

Assert-File (Join-Path $GameRoot 'BeatSaberVersion.txt')
if ((Get-Content -LiteralPath (Join-Path $GameRoot 'BeatSaberVersion.txt') -Raw).Trim() -ne '1.45.2_28848') {
    throw 'This build targets Beat Saber 1.45.2_28848.'
}
Assert-File $DotnetPath
$sharedRoot = Join-Path $RepoRoot 'vendor/LocalModUI'
$sharedBuild = Join-Path $sharedRoot 'scripts/build.ps1'
Assert-File $sharedBuild
& $sharedBuild -GameRoot $GameRoot -BuildDirectory (Join-Path $sharedRoot '.tmp/build') -OutputDirectory (Join-Path $sharedRoot '.tmp/package') | Out-Null

$frameworkReferenceDirectory = Join-Path $env:USERPROFILE '.nuget/packages/microsoft.netframework.referenceassemblies.net472/1.0.3/build/.NETFramework/v4.7.2'

$sourceRoot = Join-Path $PSScriptRoot 'beat_sage'
$project = Join-Path $sourceRoot 'BeatSage.csproj'
$generatorProject = Join-Path $sharedRoot 'scripts/contract_generator/ContractGenerator.csproj'
$schema = Join-Path $sourceRoot 'Contracts/contracts.schema.json'
$endpoints = Join-Path $sourceRoot 'Contracts/endpoints.json'
$manifest = Join-Path $sourceRoot 'manifest.json'
$notices = Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.txt'
$licenseDirectory = Join-Path $RepoRoot 'licenses'
foreach ($required in @(
    $project, $generatorProject, $schema, $endpoints, $manifest, $notices,
    (Join-Path $licenseDirectory 'NJsonSchema.txt'),
    (Join-Path $licenseDirectory 'LGPL-2.1.txt'),
    (Join-Path $licenseDirectory 'Microsoft-dotnet-MIT.txt')
)) { Assert-File $required }

$managed = Join-Path $GameRoot 'Beat Saber_Data/Managed'
$requiredReferences = @(
    'Main.dll', 'DataModels.dll', 'BeatmapCore.dll', 'GameplayCore.dll', 'BeatSaber.ViewSystem.dll',
    'BGLib.AppFlow.dll', 'BGLib.UnityExtension.dll', 'IPA.Loader.dll', 'HMUI.dll', 'HMLib.dll',
    'VRUI.dll', 'VRUI.Interfaces.dll', 'Zenject.dll', 'UnityEngine.dll', 'UnityEngine.CoreModule.dll',
    'UnityEngine.UI.dll', 'UnityEngine.UIModule.dll', 'UnityEngine.TextRenderingModule.dll',
    'UnityEngine.AudioModule.dll', 'UnityEngine.ImageConversionModule.dll', 'Unity.TextMeshPro.dll',
    'netstandard.dll'
)
foreach ($referenceName in $requiredReferences) { Assert-File (Join-Path $managed $referenceName) }
foreach ($referencePath in @(
    (Join-Path $GameRoot 'Libs/0Harmony.dll'),
    (Join-Path $GameRoot 'Libs/Newtonsoft.Json.dll'),
    (Join-Path $GameRoot 'Plugins/BS_Utils.dll'),
    (Join-Path $GameRoot 'Plugins/SongCore.dll'),
    (Join-Path $GameRoot 'Plugins/BSML.dll')
    (Join-Path $GameRoot 'Libs/BeatSaberPlaylistsLib.dll')
)) { Assert-File $referencePath }

$generatedDirectory = Join-Path $BuildDirectory 'generated'
$generatorOutput = Join-Path $BuildDirectory 'generator/bin'
$mainOutput = Join-Path $BuildDirectory 'bin'
$mainObj = Join-Path $BuildDirectory 'obj/main'
$sharedOutput = Join-Path $BuildDirectory 'shared'
$sharedObj = Join-Path $BuildDirectory 'shared-obj'
[IO.Directory]::CreateDirectory($BuildDirectory) | Out-Null
[IO.Directory]::CreateDirectory($generatorOutput) | Out-Null
[IO.Directory]::CreateDirectory($mainOutput) | Out-Null
[IO.Directory]::CreateDirectory($mainObj) | Out-Null
if (Test-Path -LiteralPath $generatedDirectory) {
    $generatedRoot = Resolve-Absolute $generatedDirectory
    $buildRoot = Resolve-RepoBuildPath $BuildDirectory
    if (!$generatedRoot.StartsWith($buildRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Generated contract path escaped the build directory.'
    }
    Remove-Item -LiteralPath $generatedDirectory -Recurse -Force
}
[IO.Directory]::CreateDirectory($generatedDirectory) | Out-Null

$generatorBuildArguments = @(
    'build', $generatorProject, '--configuration', 'Release',
    ('-p:GameRoot=' + $GameRoot), ('-p:OutputPath=' + $generatorOutput + '/'),
    ('-p:BaseIntermediateOutputPath=' + (Join-Path $BuildDirectory 'generator/obj') + '/')
)
& $DotnetPath @generatorBuildArguments
if ($LASTEXITCODE -ne 0) { throw 'Beat Sage contract generator compilation failed.' }
$generatorDll = Join-Path $generatorOutput 'BeatSage.ContractGenerator.dll'
Assert-File $generatorDll
$generatorArguments = @(
    $generatorDll, '--schema', $schema, '--endpoints', $endpoints, '--output', $generatedDirectory,
    '--namespace', 'BeatSage.Contracts', '--root-name', 'ContractDocument'
)
& $DotnetPath @generatorArguments
if ($LASTEXITCODE -ne 0) { throw 'Beat Sage contract generation failed.' }
Assert-File (Join-Path $generatedDirectory 'ContractDocument.cs')
Assert-File (Join-Path $generatedDirectory 'contract.generation.json')

$restoreArguments = @(
    'restore', $project, '--configfile', (Join-Path $RepoRoot 'NuGet.Config'),
    ('-p:GameRoot=' + $GameRoot), ('-p:BaseIntermediateOutputPath=' + $mainObj + '/'),
    ('-p:OutputPath=' + $mainOutput + '/'), ('-p:ContractGeneratedDirectory=' + $generatedDirectory),
    ('-p:SharedSongImportIntermediateOutputPath=' + $sharedObj + '/'),
    ('-p:SharedSongImportOutputPath=' + $sharedOutput + '/'),
    ('-p:FrameworkPathOverride=' + $frameworkReferenceDirectory),
    '-p:RestoreIgnoreProjectReferences=true', '-p:SkipSharedSongImport=true'
)
if (!(Test-Path -LiteralPath (Join-Path $RepoRoot 'NuGet.Config') -PathType Leaf)) {
    $restoreArguments = @(
        'restore', $project,
        ('-p:GameRoot=' + $GameRoot), ('-p:BaseIntermediateOutputPath=' + $mainObj + '/'),
        ('-p:OutputPath=' + $mainOutput + '/'), ('-p:ContractGeneratedDirectory=' + $generatedDirectory),
        ('-p:SharedSongImportIntermediateOutputPath=' + $sharedObj + '/'),
        ('-p:SharedSongImportOutputPath=' + $sharedOutput + '/'),
        ('-p:FrameworkPathOverride=' + $frameworkReferenceDirectory),
        '-p:RestoreIgnoreProjectReferences=true', '-p:SkipSharedSongImport=true'
    )
}
& $DotnetPath @restoreArguments
if ($LASTEXITCODE -ne 0) { throw 'Beat Sage dependency restore failed.' }
if (!(Test-Path -LiteralPath $frameworkReferenceDirectory -PathType Container)) {
    throw "Missing net472 reference assemblies after restore: $frameworkReferenceDirectory"
}

$sharedProject = Join-Path $PSScriptRoot 'shared_song_import/SongPackageIO.csproj'
if (Test-Path -LiteralPath $sharedProject -PathType Leaf) {
    $sharedRestoreArguments = @(
        'restore', $sharedProject,
        ('-p:GameRoot=' + $GameRoot),
        ('-p:BaseIntermediateOutputPath=' + $sharedObj + '/'),
        ('-p:OutputPath=' + $sharedOutput + '/'),
        ('-p:FrameworkPathOverride=' + $frameworkReferenceDirectory)
    )
    & $DotnetPath @sharedRestoreArguments
    if ($LASTEXITCODE -ne 0) { throw 'Shared song importer dependency restore failed.' }
}

$buildArguments = @(
    'build', $project, '--configuration', 'Release', '--no-restore',
    ('-p:GameRoot=' + $GameRoot), ('-p:BaseIntermediateOutputPath=' + $mainObj + '/'),
    ('-p:OutputPath=' + $mainOutput + '/'), ('-p:ContractGeneratedDirectory=' + $generatedDirectory),
    ('-p:SharedSongImportIntermediateOutputPath=' + $sharedObj + '/'),
    ('-p:SharedSongImportOutputPath=' + $sharedOutput + '/'),
    ('-p:FrameworkPathOverride=' + $frameworkReferenceDirectory)
)
& $DotnetPath @buildArguments
if ($LASTEXITCODE -ne 0) { throw 'Beat Sage compilation failed.' }

$builtPlugin = Join-Path $mainOutput 'BeatSage.dll'
Assert-File $builtPlugin
$loadedAssembly = [Reflection.Assembly]::LoadFile($builtPlugin)
$resourceNames = @($loadedAssembly.GetManifestResourceNames())
foreach ($requiredResource in @('BeatSage.Contracts.schema.json', 'BeatSage.Contracts.endpoints.json', 'BeatSage.manifest.json')) {
    if ($resourceNames -notcontains $requiredResource) { throw "Missing embedded resource: $requiredResource" }
}
$generationManifest = Get-Content -LiteralPath (Join-Path $generatedDirectory 'contract.generation.json') -Raw | ConvertFrom-Json
if ((Get-Sha256 $schema) -ne [string]$generationManifest.schemaSha256) { throw 'Generated DTO schema hash is stale.' }
if ((Get-Sha256 $endpoints) -ne [string]$generationManifest.registrySha256) { throw 'Generated DTO endpoint hash is stale.' }
if ((Get-Sha256 (Join-Path $generatedDirectory 'ContractDocument.cs')) -ne [string]$generationManifest.generatedSha256) { throw 'Generated DTO hash is stale.' }

$runtimeAssemblyNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($runtimeAssemblyName in @(
    'SongPackageIO.dll', 'NJsonSchema.dll', 'NJsonSchema.Annotations.dll', 'Namotion.Reflection.dll',
    'TagLibSharp.dll', 'System.Text.Json.dll', 'System.Text.Encodings.Web.dll', 'System.Buffers.dll',
    'System.Threading.Tasks.Extensions.dll', 'Microsoft.Bcl.AsyncInterfaces.dll', 'System.IO.Pipelines.dll',
    'System.Memory.dll', 'System.Numerics.Vectors.dll', 'System.Runtime.CompilerServices.Unsafe.dll',
    'System.ValueTuple.dll'
)) { [void]$runtimeAssemblyNames.Add($runtimeAssemblyName) }
$runtimeDependencyFiles = New-Object System.Collections.Generic.List[object]
foreach ($runtimeAssemblyName in $runtimeAssemblyNames) {
    $runtimePath = Join-Path $mainOutput $runtimeAssemblyName
    if (!(Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
        throw "Runtime dependency is missing from the build output: $runtimePath"
    }
    [void]$runtimeDependencyFiles.Add((Get-Item -LiteralPath $runtimePath))
}
$runtimeDependencyReport = @($runtimeDependencyFiles.ToArray() | ForEach-Object {
    [ordered]@{ name = $_.Name; path = $_.FullName; sha256 = Get-Sha256 $_.FullName }
})
$managedAssemblyNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($gameAssembly in Get-ChildItem -LiteralPath $managed -File -Filter '*.dll') {
    [void]$managedAssemblyNames.Add($gameAssembly.Name)
}
$managedRuntimeDependencyReport = New-Object System.Collections.Generic.List[object]
foreach ($file in $runtimeDependencyFiles.ToArray()) {
    if (!$managedAssemblyNames.Contains($file.Name)) { continue }
    $managedPath = Join-Path $managed $file.Name
    $managedSha256 = Get-Sha256 $managedPath
    $sourceSha256 = Get-Sha256 $file.FullName
    if ($managedSha256 -ne $sourceSha256) {
        Write-Warning "Runtime dependency $($file.Name) resolves to a different game Managed assembly; the game copy is intentionally used."
    }
    [void]$managedRuntimeDependencyReport.Add([ordered]@{
        name = $file.Name
        sourceSha256 = $sourceSha256
        sourceVersion = Get-AssemblyVersion $file.FullName
        managedPath = $managedPath
        managedSha256 = $managedSha256
        managedVersion = Get-AssemblyVersion $managedPath
        hashMatch = ($managedSha256 -eq $sourceSha256)
    })
}

$provenance = [ordered]@{
    plugin = 'BeatSage'
    version = ((Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).version)
    gameBuild = '1.45.2_28848'
    generated = [ordered]@{
        schemaSha256 = [string]$generationManifest.schemaSha256
        registrySha256 = [string]$generationManifest.registrySha256
        dtoSha256 = [string]$generationManifest.generatedSha256
        endpointCount = [int]$generationManifest.endpointCount
    }
    output = $builtPlugin
    sha256 = Get-Sha256 $builtPlugin
    runtimeDependencies = $runtimeDependencyReport
    managedRuntimeDependencies = @($managedRuntimeDependencyReport.ToArray())
}

if (![string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $packagePlugins = Join-Path $OutputDirectory 'Plugins'
    $packageLibs = Join-Path $OutputDirectory 'Libs'
    $packageLicenses = Join-Path $OutputDirectory 'licenses'
    [IO.Directory]::CreateDirectory($packagePlugins) | Out-Null
    [IO.Directory]::CreateDirectory($packageLibs) | Out-Null
    [IO.Directory]::CreateDirectory($packageLicenses) | Out-Null
    $packagePlugin = Join-Path $packagePlugins 'BeatSage.dll'
    Copy-Item -LiteralPath $builtPlugin -Destination $packagePlugin -Force
    $runtimeDependencies = New-Object System.Collections.Generic.List[object]
    $baseGameDependencyNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void]$baseGameDependencyNames.Add('Newtonsoft.Json.dll')
    foreach ($file in $runtimeDependencyFiles.ToArray()) {
            if ($managedAssemblyNames.Contains($file.Name) -or $baseGameDependencyNames.Contains($file.Name)) { continue }
        $sourceSha256 = Get-Sha256 $file.FullName
        $destination = Join-Path $packageLibs $file.Name
        if (Test-Path -LiteralPath $destination -PathType Leaf) {
            $existingSha256 = Get-Sha256 $destination
            if ($existingSha256 -ne $sourceSha256) {
                Write-Verbose "Replacing package dependency $($file.Name) with the current build version."
            }
        }
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        if ((Get-Sha256 $destination) -ne $sourceSha256) {
            throw "Packaged dependency hash mismatch: $destination"
        }
        [void]$runtimeDependencies.Add([ordered]@{ name = $file.Name; path = $destination; sha256 = $sourceSha256 })
    }
    $provenance.output = $packagePlugin
    $provenance.sha256 = Get-Sha256 $packagePlugin
    $provenance.runtimeDependencies = @($runtimeDependencies.ToArray())
    $provenancePath = Join-Path $OutputDirectory 'provenance.json'
    Copy-Item -LiteralPath $notices -Destination (Join-Path $OutputDirectory 'THIRD-PARTY-NOTICES.txt') -Force
    $licenseFiles = New-Object System.Collections.Generic.List[object]
    foreach ($licenseFile in Get-ChildItem -LiteralPath $licenseDirectory -File -Filter '*.txt') {
        $destination = Join-Path $packageLicenses $licenseFile.Name
        Copy-Item -LiteralPath $licenseFile.FullName -Destination $destination -Force
        $sha256 = Get-Sha256 $destination
        [void]$licenseFiles.Add([ordered]@{ name = $licenseFile.Name; path = $destination; sha256 = $sha256 })
    }
    $provenance.licenses = @($licenseFiles.ToArray())
    Write-Utf8NoBom $provenancePath ($provenance | ConvertTo-Json -Depth 8)
    $sumLines = New-Object System.Collections.Generic.List[string]
    [void]$sumLines.Add($provenance.sha256 + '  Plugins/BeatSage.dll')
    foreach ($dependency in $runtimeDependencies.ToArray()) {
        [void]$sumLines.Add(([string]$dependency.sha256) + '  Libs/' + ([string]$dependency.name))
    }
    foreach ($licenseFile in $licenseFiles.ToArray()) {
        [void]$sumLines.Add(([string]$licenseFile.sha256) + '  licenses/' + ([string]$licenseFile.name))
    }
    $sumLines.ToArray() | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
}

$reportPath = Join-Path $BuildDirectory 'build-report.json'
Write-Utf8NoBom $reportPath ($provenance | ConvertTo-Json -Depth 8)
Write-Output $builtPlugin
if (![string]::IsNullOrWhiteSpace($OutputDirectory)) { Write-Output (Join-Path $OutputDirectory 'Plugins/BeatSage.dll') }
