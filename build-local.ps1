[CmdletBinding()]
param(
    [string]$BuildTarget,
    [string]$FlutterPath,
    [string]$OutputPath,
    [switch]$Test
)

$ErrorActionPreference = 'Stop'

function ConvertTo-BuildTarget {
    param(
        [Parameter(Mandatory = $true)] [string]$Choice
    )

    switch ($Choice.Trim().ToLowerInvariant()) {
        '1' { return 'Portable' }
        'portable' { return 'Portable' }
        '2' { return 'Installer' }
        'installer' { return 'Installer' }
        '3' { return 'Both' }
        'both' { return 'Both' }
        '4' { return 'OutputConfig' }
        'outputconfig' { return 'OutputConfig' }
        'output config' { return 'OutputConfig' }
        default { throw "Invalid build selection '$Choice'. Choose 1, 2, 3, or 4." }
    }
}

function Get-BuildTargetPlan {
    param(
        [Parameter(Mandatory = $true)] [string]$BuildTarget
    )

    $target = ConvertTo-BuildTarget -Choice $BuildTarget
    if ($target -eq 'OutputConfig') {
        throw 'Output config is a menu option, not a build target.'
    }

    return [pscustomobject]@{
        BuildTarget = $target
        BuildPortable = $target -in @('Portable', 'Both')
        BuildInstaller = $target -in @('Installer', 'Both')
    }
}

function Get-FullPath {
    param(
        [Parameter(Mandatory = $true)] [string]$Path
    )

    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
    $resolved = Resolve-Path -LiteralPath $expanded -ErrorAction SilentlyContinue
    if ($resolved) {
        return $resolved.Path
    }

    return [System.IO.Path]::GetFullPath($expanded)
}

function ConvertTo-FlutterExecutable {
    param(
        [AllowEmptyString()] [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $candidate = $Path.Trim().Trim('"')
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        return $null
    }

    $candidate = [Environment]::ExpandEnvironmentVariables($candidate)
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        $item = Get-Item -LiteralPath $candidate
        if ($item.Name -ieq 'flutter.bat' -or $item.Name -ieq 'flutter.exe') {
            return $item.FullName
        }
        return $null
    }

    if (Test-Path -LiteralPath $candidate -PathType Container) {
        foreach ($fileName in @('flutter.bat', 'flutter.exe')) {
            $executable = Join-Path $candidate "bin\$fileName"
            if (Test-Path -LiteralPath $executable -PathType Leaf) {
                return (Get-Item -LiteralPath $executable).FullName
            }
        }
    }

    return $null
}

function Resolve-FlutterExecutable {
    param(
        [string]$PreferredPath,
        [Parameter(Mandatory = $true)] [string]$RepositoryRoot
    )

    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($PreferredPath)) {
        $candidates.Add($PreferredPath)
    }

    foreach ($commandName in @('flutter.bat', 'flutter')) {
        $command = Get-Command $commandName -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) {
            $commandPath = $command.Source
            if ([string]::IsNullOrWhiteSpace($commandPath)) {
                $commandPath = $command.Path
            }
            if (-not [string]::IsNullOrWhiteSpace($commandPath)) {
                $candidates.Add($commandPath)
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($env:FLUTTER_ROOT)) {
        $candidates.Add($env:FLUTTER_ROOT)
    }

    $candidates.Add((Join-Path $RepositoryRoot 'flutter_sdk\flutter'))

    foreach ($candidate in $candidates | Select-Object -Unique) {
        $resolved = ConvertTo-FlutterExecutable -Path $candidate
        if ($resolved) {
            return $resolved
        }
    }

    return $null
}

function Read-FlutterExecutable {
    param(
        [string]$PreferredPath,
        [Parameter(Mandatory = $true)] [string]$RepositoryRoot
    )

    $resolved = Resolve-FlutterExecutable -PreferredPath $PreferredPath -RepositoryRoot $RepositoryRoot
    if ($resolved) {
        return $resolved
    }

    while ($true) {
        $manualPath = Read-Host 'Flutter was not found. Enter the Flutter SDK or flutter.bat path (q to cancel)'
        if ($manualPath.Trim().ToLowerInvariant() -eq 'q') {
            throw 'Flutter path is required to continue.'
        }

        $resolved = ConvertTo-FlutterExecutable -Path $manualPath
        if ($resolved) {
            return $resolved
        }

        Write-Warning 'No valid Flutter SDK was found. Enter an SDK directory containing bin\flutter.bat, or enter flutter.bat directly.'
    }
}

function Resolve-DotnetExecutable {
    $command = Get-Command 'dotnet' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) {
        return $null
    }

    $path = $command.Source
    if ([string]::IsNullOrWhiteSpace($path)) {
        $path = $command.Path
    }

    if ($path -and (Test-Path -LiteralPath $path -PathType Leaf)) {
        return (Get-Item -LiteralPath $path).FullName
    }

    return $null
}

function Resolve-IsccExecutable {
    $candidates = [System.Collections.Generic.List[string]]::new()
    $candidates.Add('C:\Program Files (x86)\Inno Setup 6\ISCC.exe')
    $candidates.Add('C:\Program Files\Inno Setup 6\ISCC.exe')
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'))
    }

    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) {
        $path = $command.Source
        if ([string]::IsNullOrWhiteSpace($path)) {
            $path = $command.Path
        }
        if ($path) {
            $candidates.Add($path)
        }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Get-Item -LiteralPath $candidate).FullName
        }
    }

    return $null
}

function Read-BuildLocalConfig {
    param(
        [Parameter(Mandatory = $true)] [string]$ConfigPath
    )

    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        return [pscustomobject]@{}
    }

    try {
        $content = Get-Content -LiteralPath $ConfigPath -Raw -ErrorAction Stop
        if ([string]::IsNullOrWhiteSpace($content)) {
            return [pscustomobject]@{}
        }
        return ($content | ConvertFrom-Json -ErrorAction Stop)
    }
    catch {
        Write-Warning "Unable to read the build-local config; defaults will be used: $ConfigPath"
        return [pscustomobject]@{}
    }
}

function Write-BuildLocalConfig {
    param(
        [Parameter(Mandatory = $true)] [string]$ConfigPath,
        [string]$BuildTarget = 'Both',
        [AllowEmptyString()] [string]$FlutterPath = '',
        [Parameter(Mandatory = $true)] [string]$OutputPath
    )

    $configDirectory = Split-Path -Parent $ConfigPath
    if ($configDirectory) {
        New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null
    }

    $normalizedTarget = if ([string]::IsNullOrWhiteSpace($BuildTarget)) { 'Both' } else { ConvertTo-BuildTarget -Choice $BuildTarget }
    $normalizedFlutterPath = if ([string]::IsNullOrWhiteSpace($FlutterPath)) { '' } else { Get-FullPath -Path $FlutterPath }
    $payload = [ordered]@{
        BuildTarget = $normalizedTarget
        FlutterPath = $normalizedFlutterPath
        OutputPath = (Get-FullPath -Path $OutputPath)
    }
    $json = $payload | ConvertTo-Json -Depth 3

    $existingConfig = Get-Item -LiteralPath $ConfigPath -Force -ErrorAction SilentlyContinue
    if ($existingConfig) {
        $existingConfig.Attributes = $existingConfig.Attributes -band (-bnot [System.IO.FileAttributes]::Hidden)
    }
    [System.IO.File]::WriteAllText(
        (Get-FullPath -Path $ConfigPath),
        $json,
        [System.Text.UTF8Encoding]::new($false))

    $configItem = Get-Item -LiteralPath $ConfigPath -Force
    $configItem.Attributes = $configItem.Attributes -bor [System.IO.FileAttributes]::Hidden
}

function Resolve-OutputDirectoryPath {
    param(
        [Parameter(Mandatory = $true)] [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'Output path cannot be empty.'
    }

    $fullPath = Get-FullPath -Path $Path
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
    $item = Get-Item -LiteralPath $fullPath
    if (-not $item.PSIsContainer) {
        throw "Output path is not a directory: $fullPath"
    }

    return $item.FullName
}

function Read-OutputDirectoryPath {
    param(
        [string]$ConfiguredPath,
        [Parameter(Mandatory = $true)] [string]$DefaultPath
    )

    $default = if ([string]::IsNullOrWhiteSpace($ConfiguredPath)) { $DefaultPath } else { $ConfiguredPath }
    while ($true) {
        $enteredPath = Read-Host "Output directory [$default]"
        $selectedPath = if ([string]::IsNullOrWhiteSpace($enteredPath)) { $default } else { $enteredPath }
        try {
            return (Resolve-OutputDirectoryPath -Path $selectedPath)
        }
        catch {
            Write-Warning "Unable to use the output directory: $($_.Exception.Message)"
        }
    }
}

function Read-BuildTarget {
    param(
        [string]$DefaultTarget = 'Both'
    )

    $defaultCandidate = try { ConvertTo-BuildTarget -Choice $DefaultTarget } catch { 'Both' }
    $default = if ($defaultCandidate -in @('Portable', 'Installer', 'Both')) { $defaultCandidate } else { 'Both' }
    while ($true) {
        Write-Host ''
        Write-Host 'Select packages to build:' -ForegroundColor Cyan
        Write-Host '1. portable'
        Write-Host '2. installer'
        Write-Host '3. both'
        Write-Host '4. output config'
        $choice = Read-Host "Enter 1-4 [$default]"
        if ([string]::IsNullOrWhiteSpace($choice)) {
            return $default
        }

        try {
            return (ConvertTo-BuildTarget -Choice $choice)
        }
        catch {
            Write-Warning 'Invalid selection. Enter 1, 2, 3, or 4.'
        }
    }
}

$repoRoot = $PSScriptRoot

function Remove-BuildOutput {
    param(
        [Parameter(Mandatory = $true)] [string]$Path
    )

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Invoke-Stage {
    param(
        [Parameter(Mandatory = $true)] [string]$Name,
        [Parameter(Mandatory = $true)] [scriptblock]$Action
    )

    Write-Host ([Environment]::NewLine + "== $Name ==") -ForegroundColor Cyan
    try {
        $global:LASTEXITCODE = 0
        & $Action
        if ($LASTEXITCODE -ne 0) {
            throw "process exited with code $LASTEXITCODE"
        }
    }
    catch {
        throw "Stage '$Name' failed: $($_.Exception.Message)"
    }
}

function Assert-File {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing: $Path"
    }

    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Length -le 0) {
        throw "$Description is empty: $Path"
    }
}

function Get-ConfigValue {
    param(
        [Parameter(Mandatory = $true)] $Config,
        [Parameter(Mandatory = $true)] [string]$Name
    )

    $property = $Config.PSObject.Properties[$Name]
    if ($property -and $null -ne $property.Value) {
        return [string]$property.Value
    }

    return ''
}

function Assert-OutputPathIsSafe {
    param(
        [Parameter(Mandatory = $true)] [string]$OutputDirectory,
        [Parameter(Mandatory = $true)] [string[]]$ManagedDirectories
    )

    $outputFullPath = Get-FullPath -Path $OutputDirectory
    foreach ($managedDirectory in $ManagedDirectories) {
        $managedFullPath = Get-FullPath -Path $managedDirectory
        $descendantPrefix = $managedFullPath
        if (-not $descendantPrefix.EndsWith('\')) {
            $descendantPrefix += '\'
        }

        if ($outputFullPath.Equals($managedFullPath, [System.StringComparison]::OrdinalIgnoreCase) -or
            $outputFullPath.StartsWith($descendantPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Output directory cannot be inside a managed build directory: $outputFullPath"
        }
    }
}

function Invoke-PortableAssembly {
    param(
        [Parameter(Mandatory = $true)] [string]$AssembleScript,
        [Parameter(Mandatory = $true)] [string]$GuiRelease,
        [Parameter(Mandatory = $true)] [string]$BackendPublish,
        [Parameter(Mandatory = $true)] [string]$OutputDirectory
    )

    $arguments = @{
        GuiRelease = $GuiRelease
        BackendPublish = $BackendPublish
        OutputDirectory = $OutputDirectory
    }
    & $AssembleScript @arguments
    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "Portable assembly process exited with code $LASTEXITCODE"
    }
}

function Invoke-BuildLocal {
    param(
        [string]$RequestedBuildTarget,
        [string]$RequestedFlutterPath,
        [string]$RequestedOutputPath
    )

    $configPath = Join-Path $repoRoot '.build-local.config.json'
    $guiProject = Join-Path $repoRoot 'oshare_gui'
    $guiRelease = Join-Path $guiProject 'build\windows\x64\runner\Release'
    $backendPublish = Join-Path $repoRoot 'artifacts\backend'
    $portableStage = Join-Path $repoRoot 'deploy-gui'
    $installerOutput = Join-Path $repoRoot 'installer-output'
    $artifacts = Join-Path $repoRoot 'artifacts'
    $assembleScript = Join-Path $repoRoot 'scripts\assemble-portable.ps1'
    $installerScript = Join-Path $repoRoot 'installer\OSharePC.iss'
    $locationPushed = $false

    try {
        Push-Location $repoRoot
        $locationPushed = $true

        $config = Read-BuildLocalConfig -ConfigPath $configPath
        $storedTarget = Get-ConfigValue -Config $config -Name 'BuildTarget'
        if ([string]::IsNullOrWhiteSpace($RequestedBuildTarget)) {
            while ($true) {
                $selection = Read-BuildTarget -DefaultTarget $storedTarget
                if ($selection -ne 'OutputConfig') {
                    $selectedTarget = $selection
                    break
                }

                $configuredOutputPath = Get-ConfigValue -Config $config -Name 'OutputPath'
                $outputDirectory = Read-OutputDirectoryPath -ConfiguredPath $configuredOutputPath -DefaultPath $artifacts
                $configTarget = 'Both'
                try {
                    $candidateTarget = ConvertTo-BuildTarget -Choice $storedTarget
                    if ($candidateTarget -ne 'OutputConfig') {
                        $configTarget = $candidateTarget
                    }
                }
                catch {
                    $configTarget = 'Both'
                }
                Write-BuildLocalConfig `
                    -ConfigPath $configPath `
                    -BuildTarget $configTarget `
                    -FlutterPath (Get-ConfigValue -Config $config -Name 'FlutterPath') `
                    -OutputPath $outputDirectory
                $config = Read-BuildLocalConfig -ConfigPath $configPath
                $storedTarget = $configTarget
                Write-Host "Output path saved: $outputDirectory" -ForegroundColor Green
            }
        }
        else {
            $selectedTarget = ConvertTo-BuildTarget -Choice $RequestedBuildTarget
        }
        $targetPlan = Get-BuildTargetPlan -BuildTarget $selectedTarget

        $storedFlutterPath = Get-ConfigValue -Config $config -Name 'FlutterPath'
        $preferredFlutterPath = if ([string]::IsNullOrWhiteSpace($RequestedFlutterPath)) { $storedFlutterPath } else { $RequestedFlutterPath }
        $flutter = Read-FlutterExecutable -PreferredPath $preferredFlutterPath -RepositoryRoot $repoRoot

        $storedOutputPath = Get-ConfigValue -Config $config -Name 'OutputPath'
        if ([string]::IsNullOrWhiteSpace($RequestedOutputPath) -and [string]::IsNullOrWhiteSpace($storedOutputPath)) {
            $outputDirectory = Resolve-OutputDirectoryPath -Path $artifacts
        }
        elseif ([string]::IsNullOrWhiteSpace($RequestedOutputPath)) {
            try {
                $outputDirectory = Resolve-OutputDirectoryPath -Path $storedOutputPath
            }
            catch {
                throw "Saved output path is unavailable: $storedOutputPath. Choose option 4 (output config) to update it."
            }
        }
        else {
            $outputDirectory = Resolve-OutputDirectoryPath -Path $RequestedOutputPath
        }

        Assert-OutputPathIsSafe -OutputDirectory $outputDirectory -ManagedDirectories @(
            $guiRelease,
            $backendPublish,
            $portableStage,
            $installerOutput)

        $dotnet = Resolve-DotnetExecutable
        $iscc = if ($targetPlan.BuildInstaller) { Resolve-IsccExecutable } else { $null }
        $missingTools = [System.Collections.Generic.List[string]]::new()
        if (-not $dotnet) {
            $missingTools.Add('dotnet SDK (install .NET SDK 10 and make sure dotnet.exe is on PATH)')
        }
        if ($targetPlan.BuildInstaller -and -not $iscc) {
            $missingTools.Add('Inno Setup 6 / ISCC.exe (install Inno Setup 6 or add ISCC.exe to PATH)')
        }
        if ($missingTools.Count -gt 0) {
            throw ('Missing required tools:' + [Environment]::NewLine + ' - ' + ($missingTools -join ([Environment]::NewLine + ' - ')))
        }

        Write-BuildLocalConfig -ConfigPath $configPath -BuildTarget $selectedTarget -FlutterPath $flutter -OutputPath $outputDirectory

        if (-not (Test-Path -LiteralPath $assembleScript -PathType Leaf)) {
            throw "Existing portable assembly script is missing: $assembleScript"
        }
        if ($targetPlan.BuildInstaller -and -not (Test-Path -LiteralPath $installerScript -PathType Leaf)) {
            throw "Existing Inno Setup script is missing: $installerScript"
        }

        $portableZip = Join-Path $outputDirectory 'OSharePC-Portable-win-x64.zip'
        $setupExe = Join-Path $outputDirectory 'OSharePC-Setup-win-x64.exe'

        Invoke-Stage 'Clean local build output' {
            New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
            Remove-BuildOutput -Path $guiRelease
            Remove-BuildOutput -Path $backendPublish
            Remove-BuildOutput -Path $portableStage
            if ($targetPlan.BuildInstaller) {
                Remove-BuildOutput -Path $installerOutput
            }
            if ($targetPlan.BuildPortable -and (Test-Path -LiteralPath $portableZip)) {
                Remove-Item -LiteralPath $portableZip -Force
            }
            if ($targetPlan.BuildInstaller -and (Test-Path -LiteralPath $setupExe)) {
                Remove-Item -LiteralPath $setupExe -Force
            }
        }

        Invoke-Stage 'Flutter dependency setup' {
            Push-Location $guiProject
            try {
                & $flutter @('pub', 'get')
            }
            finally {
                Pop-Location
            }
        }

        Invoke-Stage 'Flutter Windows Release build' {
            Push-Location $guiProject
            try {
                & $flutter @('build', 'windows', '--release', '--no-version-check', '--no-pub')
            }
            finally {
                Pop-Location
            }
            Assert-File -Path (Join-Path $guiRelease 'oshare_gui.exe') -Description 'Flutter release executable'
            if (-not (Test-Path -LiteralPath (Join-Path $guiRelease 'data\flutter_assets') -PathType Container)) {
                throw "Flutter release assets are missing: $(Join-Path $guiRelease 'data\flutter_assets')"
            }
        }

        Invoke-Stage 'C# backend Release win-x64 publish' {
            & $dotnet @(
                'publish',
                (Join-Path $repoRoot 'OShareSender.csproj'),
                '-c', 'Release',
                '-r', 'win-x64',
                '--self-contained', 'true',
                '-o', $backendPublish)
            Assert-File -Path (Join-Path $backendPublish 'OSharePC.exe') -Description 'Published backend executable'
        }

        Invoke-Stage 'Assemble shared portable runtime' {
            Invoke-PortableAssembly `
                -AssembleScript $assembleScript `
                -GuiRelease $guiRelease `
                -BackendPublish $backendPublish `
                -OutputDirectory $portableStage
            Assert-File -Path (Join-Path $portableStage 'oshare_gui.exe') -Description 'Assembled GUI executable'
            if (-not (Test-Path -LiteralPath (Join-Path $portableStage 'data\flutter_assets') -PathType Container)) {
                throw "Assembled Flutter assets are missing: $(Join-Path $portableStage 'data\flutter_assets')"
            }
            Assert-File -Path (Join-Path $portableStage 'engine\OSharePC.exe') -Description 'Assembled backend executable'
        }

        if ($targetPlan.BuildPortable) {
            Invoke-Stage 'Create portable ZIP' {
                Add-Type -AssemblyName System.IO.Compression.FileSystem
                [System.IO.Compression.ZipFile]::CreateFromDirectory(
                    $portableStage,
                    $portableZip,
                    [System.IO.Compression.CompressionLevel]::Optimal,
                    $false)

                Assert-File -Path $portableZip -Description 'Portable ZIP'
                $zip = [System.IO.Compression.ZipFile]::OpenRead($portableZip)
                try {
                    $entries = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
                    foreach ($requiredEntry in @('oshare_gui.exe', 'engine/OSharePC.exe')) {                        if ($entries -notcontains $requiredEntry) {
                            throw "Portable ZIP is incomplete; missing $requiredEntry"
                        }
                    }
                    if (-not ($entries | Where-Object { $_ -like 'data/flutter_assets/*' })) {
                        throw 'Portable ZIP is incomplete; missing data/flutter_assets contents'
                    }
                }
                finally {
                    $zip.Dispose()
                }
            }
        }

        if ($targetPlan.BuildInstaller) {
            Invoke-Stage 'Compile Setup EXE from the shared assembled runtime' {
                New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null
                & $iscc $installerScript
                $intermediate = @(Get-ChildItem -LiteralPath $installerOutput -Filter '*.exe' -File |
                    Sort-Object LastWriteTime -Descending)
                if ($intermediate.Count -ne 1) {
                    throw "Expected exactly one Inno Setup output in $installerOutput, found $($intermediate.Count)"
                }
                Copy-Item -LiteralPath $intermediate[0].FullName -Destination $setupExe -Force
                Assert-File -Path $setupExe -Description 'Final Setup EXE'
            }
        }

        Invoke-Stage 'Verify final packages' {
            if ($targetPlan.BuildPortable) {
                Assert-File -Path $portableZip -Description 'Final portable package'
            }
            if ($targetPlan.BuildInstaller) {
                Assert-File -Path $setupExe -Description 'Final installer package'
            }
        }

        Write-Host ([Environment]::NewLine + '========================================') -ForegroundColor Green
        Write-Host "OSharePC local $($targetPlan.BuildTarget.ToLowerInvariant()) build completed" -ForegroundColor Green
        Write-Host '========================================' -ForegroundColor Green
        if ($targetPlan.BuildPortable) {
            $portableSize = (Get-Item -LiteralPath $portableZip -Force).Length
            Write-Host ('Portable:' + [Environment]::NewLine + $portableZip + [Environment]::NewLine + $portableSize + ' bytes')
        }
        if ($targetPlan.BuildInstaller) {
            $setupSize = (Get-Item -LiteralPath $setupExe -Force).Length
            Write-Host ('Installer:' + [Environment]::NewLine + $setupExe + [Environment]::NewLine + $setupSize + ' bytes')
        }
    }
    finally {
        if ($locationPushed) {
            Pop-Location
        }
    }
}

function Invoke-BuildLocalTests {
    $script:failureCount = 0

    function Assert-Equal {
        param(
            [Parameter(Mandatory = $true)] $Actual,
            [Parameter(Mandatory = $true)] $Expected,
            [Parameter(Mandatory = $true)] [string]$Message
        )
    
        if ($Actual -ne $Expected) {
            $script:failureCount++
            Write-Host "FAIL: $Message. Expected '$Expected', got '$Actual'." -ForegroundColor Red
        }
    }
    
    function Assert-True {
        param(
            [Parameter(Mandatory = $true)] [bool]$Condition,
            [Parameter(Mandatory = $true)] [string]$Message
        )
    
        if (-not $Condition) {
            $script:failureCount++
            Write-Host "FAIL: $Message" -ForegroundColor Red
        }
    }
    
    function Assert-Throws {
        param(
            [Parameter(Mandatory = $true)] [scriptblock]$Action,
            [Parameter(Mandatory = $true)] [string]$Message
        )
    
        $threw = $false
        try {
            & $Action
        }
        catch {
            $threw = $true
        }
    
        if (-not $threw) {
            $script:failureCount++
            Write-Host "FAIL: Expected an exception: $Message" -ForegroundColor Red
        }
    }
    
    Assert-Equal (ConvertTo-BuildTarget -Choice '1') 'Portable' 'Choice 1 must build portable'
    Assert-Equal (ConvertTo-BuildTarget -Choice '2') 'Installer' 'Choice 2 must build installer'
    Assert-Equal (ConvertTo-BuildTarget -Choice '3') 'Both' 'Choice 3 must build both packages'
    Assert-Equal (ConvertTo-BuildTarget -Choice '4') 'OutputConfig' 'Choice 4 must open output configuration'
    Assert-Throws { Get-BuildTargetPlan -BuildTarget '4' } 'Output config must not be treated as a build target'
    
    $portablePlan = Get-BuildTargetPlan -BuildTarget 'Portable'
    Assert-True $portablePlan.BuildPortable 'Portable selection must include the portable package'
    Assert-True (-not $portablePlan.BuildInstaller) 'Portable selection must skip the installer'
    
    $installerPlan = Get-BuildTargetPlan -BuildTarget 'Installer'
    Assert-True (-not $installerPlan.BuildPortable) 'Installer selection must skip the portable package'
    Assert-True $installerPlan.BuildInstaller 'Installer selection must include the installer'
    
    $bothPlan = Get-BuildTargetPlan -BuildTarget 'Both'
    Assert-True $bothPlan.BuildPortable 'Both selection must include the portable package'
    Assert-True $bothPlan.BuildInstaller 'Both selection must include the installer'
    
    $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('oshare-build-local-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    try {
        $flutterSdk = Join-Path $testRoot 'flutter-sdk'
        $flutterBin = Join-Path $flutterSdk 'bin'
        New-Item -ItemType Directory -Path $flutterBin -Force | Out-Null
        $flutterBat = Join-Path $flutterBin 'flutter.bat'
        Set-Content -LiteralPath $flutterBat -Value '@echo off' -Encoding ascii
    
        Assert-Equal (ConvertTo-FlutterExecutable -Path $flutterSdk) ([System.IO.Path]::GetFullPath($flutterBat)) 'A Flutter SDK directory must resolve to bin\flutter.bat'
        Assert-Equal (ConvertTo-FlutterExecutable -Path $flutterBat) ([System.IO.Path]::GetFullPath($flutterBat)) 'A flutter.bat path must be accepted directly'
        Assert-True ($null -eq (ConvertTo-FlutterExecutable -Path (Join-Path $testRoot 'missing-flutter'))) 'A missing Flutter path must be rejected'
    
        $configPath = Join-Path $testRoot '.build-local.config.json'
        Write-BuildLocalConfig -ConfigPath $configPath -BuildTarget 'Installer' -FlutterPath $flutterBat -OutputPath (Join-Path $testRoot 'packages')
        Write-BuildLocalConfig -ConfigPath $configPath -BuildTarget 'Both' -FlutterPath $flutterBat -OutputPath (Join-Path $testRoot 'packages-2')
        Write-BuildLocalConfig -ConfigPath $configPath -BuildTarget 'Both' -FlutterPath '' -OutputPath (Join-Path $testRoot 'packages-3')
        $config = Read-BuildLocalConfig -ConfigPath $configPath
        Assert-Equal $config.BuildTarget 'Both' 'Saved build target must be updated and loaded'
        Assert-Equal $config.FlutterPath '' 'Output configuration must be writable before Flutter is selected'
        Assert-True ((Get-Item -LiteralPath $configPath -Force).Attributes -band [System.IO.FileAttributes]::Hidden) 'The local config must be hidden'
    
    $outputPath = Resolve-OutputDirectoryPath -Path (Join-Path $testRoot 'packages')
    Assert-True (Test-Path -LiteralPath $outputPath -PathType Container) 'The selected output directory must be created'

    $fakeAssembler = Join-Path $testRoot 'fake-assemble.ps1'
    Set-Content -LiteralPath $fakeAssembler -Encoding ascii -Value @'
param(
    [Parameter(Mandatory = $true)] [string]$GuiRelease,
    [Parameter(Mandatory = $true)] [string]$BackendPublish,
    [Parameter(Mandatory = $true)] [string]$OutputDirectory
)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Set-Content -LiteralPath (Join-Path $OutputDirectory 'args.txt') -Value ($GuiRelease + '|' + $BackendPublish)
'@
    $assemblyOutput = Join-Path $testRoot 'assembled'
    Invoke-PortableAssembly -AssembleScript $fakeAssembler -GuiRelease 'gui-release' -BackendPublish 'backend-publish' -OutputDirectory $assemblyOutput
    Assert-Equal (Get-Content -LiteralPath (Join-Path $assemblyOutput 'args.txt') -Raw).Trim() 'gui-release|backend-publish' 'Portable assembly arguments must bind to named parameters'
}
    finally {
        if (Test-Path -LiteralPath $testRoot) {
            Remove-Item -LiteralPath $testRoot -Recurse -Force
        }
    }
    
    if ($script:failureCount -gt 0) {
        throw "$failureCount build-local test(s) failed."
    }
    
    Write-Host 'build-local tests passed.' -ForegroundColor Green
}

if ($Test) {
    try {
        Invoke-BuildLocalTests
        exit 0
    }
    catch {
        Write-Error "build-local tests failed: $($_.Exception.Message)"
        exit 1
    }
}

try {
    Invoke-BuildLocal -RequestedBuildTarget $BuildTarget -RequestedFlutterPath $FlutterPath -RequestedOutputPath $OutputPath
    exit 0
}
catch {
    Write-Error "OSharePC local build failed: $($_.Exception.Message)"
    exit 1
}
