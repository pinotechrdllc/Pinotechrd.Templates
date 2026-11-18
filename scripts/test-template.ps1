#requires -Version 7
<#
.SYNOPSIS
  Builds the template source in place, packs the template package, installs it in an isolated
  hive, generates a project for each case and verifies the result.
.DESCRIPTION
  The template source must build as it is (it lists every architecture's projects). A case passes
  when the project is generated with the layout of its architecture, the placeholder name is fully
  replaced, no conditional marker or template configuration is left, its solution lists exactly its
  projects, it builds without warnings and its tests pass. Invalid option values must be rejected.
.PARAMETER Keep
  Keep the working directory after the run, for inspection.
#>
param([switch]$Keep)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$template = Join-Path $repo 'templates/pinotechrd-app'
$work = Join-Path ([IO.Path]::GetTempPath()) ("pinotechrd-templates-" + [Guid]::NewGuid().ToString('N'))
$packages = Join-Path $work 'packages'
$hive = Join-Path $work 'hive'
# Placeholders the template engine must replace everywhere: the project name and the module name.
$placeholders = @('MyApp', 'MyModule')
$projectName = 'Acme.Demo'

# Each case is one combination of template options that must generate a working project.
$cases = @(
    @{ Name = 'clean-standalone'; Architecture = 'clean'; Options = @() },
    @{ Name = 'modular-standalone'; Architecture = 'modular'; Module = 'Sales'; Options = @('--architecture', 'modular', '--module', 'Sales') }
)

# Generated and checked (names, layout, solution) but not built or tested, because they differ from a full case only
# by a name: the default module name, and --module given to clean, which is accepted and ignored (clean layout, no
# module, no module name anywhere; the name is one that appears nowhere else in the template).
$generateOnlyCases = @(
    @{ Name = 'modular-default-module'; Architecture = 'modular'; Module = 'Core'; Options = @('--architecture', 'modular') },
    @{ Name = 'clean-with-module'; Architecture = 'clean'; Module = 'Quokka'; Options = @('--architecture', 'clean', '--module', 'Quokka') }
)

# Paths relative to the generated project. Shared paths must exist in every output. Each architecture's own paths
# must exist in its output (unless Required is $false) and must not exist in the other architecture's output.
# {Module} stands for the module name of the case.
$sharedPaths = @(
    "src/$projectName.Api",
    "src/BuildingBlocks/$projectName.BuildingBlocks.Domain",
    "src/BuildingBlocks/$projectName.BuildingBlocks.Application",
    "src/BuildingBlocks/$projectName.BuildingBlocks.Infrastructure",
    "src/BuildingBlocks/$projectName.BuildingBlocks.Web",
    'contract',
    "tests/$projectName.BuildingBlocks.Tests",
    "tests/$projectName.Api.Tests"
)
$architecturePaths = @{
    clean   = @(
        @{ Path = "src/$projectName.Domain"; Required = $true },
        @{ Path = "src/$projectName.Contracts"; Required = $true },
        @{ Path = "src/$projectName.Resources"; Required = $true },
        @{ Path = "src/$projectName.Application"; Required = $true },
        @{ Path = "src/$projectName.Infrastructure"; Required = $true },
        @{ Path = "tests/$projectName.Domain.Tests"; Required = $true },
        @{ Path = "tests/$projectName.Api.Tests/Architecture/Clean"; Required = $true }
    )
    modular = @(
        @{ Path = 'src/Modules'; Required = $true },
        @{ Path = "src/Modules/{Module}/$projectName.Modules.{Module}.Contracts/$projectName.Modules.{Module}.Contracts.csproj"; Required = $true },
        @{ Path = "src/Modules/{Module}/$projectName.Modules.{Module}.Domain/$projectName.Modules.{Module}.Domain.csproj"; Required = $true },
        @{ Path = "src/Modules/{Module}/$projectName.Modules.{Module}.Application/$projectName.Modules.{Module}.Application.csproj"; Required = $true },
        @{ Path = "src/Modules/{Module}/$projectName.Modules.{Module}.Infrastructure/$projectName.Modules.{Module}.Infrastructure.csproj"; Required = $true },
        @{ Path = "src/Modules/{Module}/$projectName.Modules.{Module}.Endpoints/$projectName.Modules.{Module}.Endpoints.csproj"; Required = $true },
        @{ Path = "src/Modules/{Module}/$projectName.Modules.{Module}.Endpoints/{Module}Module.cs"; Required = $true },
        @{ Path = "src/$projectName.Api/BusinessComposition.Modular.cs"; Required = $true },
        @{ Path = "tests/$projectName.Modules.{Module}.Tests/$projectName.Modules.{Module}.Tests.csproj"; Required = $true },
        @{ Path = "tests/$projectName.Api.Tests/Architecture/Modular"; Required = $true }
    )
}

# A conditional region left behind by the template engine: <!--#if ... -->, <!--#else -->, <!--#endif -->.
$markerPattern = '<!--\s*#(if|elif|else|endif)\b'

function Invoke-Checked([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
}

# The bin and obj folders of the build, without descending into them or into node_modules (npm packages ship their
# own bin folders).
function Get-BuildOutput([string]$Root) {
    foreach ($directory in Get-ChildItem -LiteralPath $Root -Directory -Force) {
        if ($directory.Name -in 'bin', 'obj') { $directory }
        elseif ($directory.Name -ne 'node_modules') { Get-BuildOutput $directory.FullName }
    }
}

# Cleanup must not hide the result of the run: a folder that cannot be removed is reported as a warning.
function Remove-BuildOutput([string]$Root) {
    foreach ($directory in Get-BuildOutput $Root) {
        try { Remove-Item -LiteralPath $directory.FullName -Recurse -Force -ErrorAction Stop }
        catch { Write-Warning "Could not remove $($directory.FullName): $($_.Exception.Message)" }
    }
}

function Assert-Rejected([string]$What, [string[]]$Options) {
    $output = Join-Path $work ('rejected-' + [Guid]::NewGuid().ToString('N'))
    dotnet new pinotechrd-app --name $projectName --output $output @Options --debug:custom-hive $hive *> $null
    if ($LASTEXITCODE -eq 0) { throw "$What was accepted." }
    if (Test-Path -LiteralPath $output) { throw "$What was rejected but files were generated in $output." }
    # The rejection is the expected outcome; do not let its exit code become the script's.
    $global:LASTEXITCODE = 0
}

function Assert-NameReplaced([string]$Root) {
    $entries = Get-ChildItem -LiteralPath $Root -Recurse -Force
    foreach ($placeholder in $placeholders) {
        $named = $entries | Where-Object { $_.Name -like "*$placeholder*" }
        if ($named) { throw "Placeholder '$placeholder' left in names: $($named.FullName -join ', ')" }

        $withText = $entries | Where-Object { -not $_.PSIsContainer } |
            Where-Object { Select-String -LiteralPath $_.FullName -SimpleMatch $placeholder -Quiet }
        if ($withText) { throw "Placeholder '$placeholder' left in content: $($withText.FullName -join ', ')" }
    }
}

# The module class carries the module name, and the host registers it exactly once.
function Assert-ModuleComposed([string]$Root, [string]$Module) {
    $file = Join-Path $Root "src/Modules/$Module/$projectName.Modules.$Module.Endpoints/${Module}Module.cs"
    if (-not (Select-String -LiteralPath $file -SimpleMatch "public sealed class ${Module}Module : IModule" -Quiet)) {
        throw "The module class ${Module}Module is not declared in $file."
    }
    $composition = Join-Path $Root "src/$projectName.Api/BusinessComposition.Modular.cs"
    $registrations = @(Select-String -LiteralPath $composition -SimpleMatch "AddModule<${Module}Module>")
    if ($registrations.Count -ne 1) { throw "The host registers ${Module}Module $($registrations.Count) times, expected once." }
}

# A clean output given --module has no trace of the module: no file, folder or text with its name.
function Assert-NameAbsent([string]$Root, [string]$Module) {
    $entries = Get-ChildItem -LiteralPath $Root -Recurse -Force
    $named = $entries | Where-Object { $_.Name -like "*$Module*" }
    if ($named) { throw "Module name '$Module' found in names of a clean output: $($named.FullName -join ', ')" }
    $withText = $entries | Where-Object { -not $_.PSIsContainer } |
        Where-Object { Select-String -LiteralPath $_.FullName -SimpleMatch $Module -CaseSensitive -Quiet }
    if ($withText) { throw "Module name '$Module' found in the content of a clean output: $($withText.FullName -join ', ')" }
}

function Assert-Layout([string]$Root, [string]$Architecture, [string]$Module) {
    # A clean case has no module; any name works to build the paths it must not contain.
    if (-not $Module) { $Module = 'Core' }
    $own = $architecturePaths[$Architecture]
    $others = $architecturePaths.Keys | Where-Object { $_ -ne $Architecture } | ForEach-Object { $architecturePaths[$_] }
    $mustExist = $sharedPaths + @($own | Where-Object Required | ForEach-Object { $_.Path.Replace('{Module}', $Module) })
    $mustNotExist = @($others | ForEach-Object { $_.Path.Replace('{Module}', $Module) })

    $missing = $mustExist | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Root $_)) }
    if ($missing) { throw "Missing from the $Architecture layout: $($missing -join ', ')" }
    $extra = $mustNotExist | Where-Object { Test-Path -LiteralPath (Join-Path $Root $_) }
    if ($extra) { throw "Not part of the $Architecture layout: $($extra -join ', ')" }

    $config = Get-ChildItem -LiteralPath $Root -Recurse -Directory -Force -Filter '.template.config'
    if ($config) { throw "Template configuration left: $($config.FullName -join ', ')" }

    $withMarkers = Get-ChildItem -LiteralPath $Root -Recurse -File -Force |
        Where-Object { Select-String -LiteralPath $_.FullName -Pattern $markerPattern -Quiet }
    if ($withMarkers) { throw "Conditional markers left in: $($withMarkers.FullName -join ', ')" }
}

# The solution lists exactly the projects of the output: none missing on disk, and none left out (a project left out
# of the solution is not built and its tests do not run).
function Assert-SolutionMatchesProjects([string]$Solution) {
    $root = Split-Path -Parent $Solution
    $listed = @(([xml](Get-Content -LiteralPath $Solution -Raw)).SelectNodes('//Project') | ForEach-Object { $_.Path -replace '\\', '/' })
    $absent = $listed | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root $_)) }
    if ($absent) { throw "The solution lists projects that do not exist: $($absent -join ', ')" }

    $onDisk = Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.csproj' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules)[\\/]' } |
        ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName) -replace '\\', '/' }
    $unlisted = $onDisk | Where-Object { $_ -notin $listed }
    if ($unlisted) { throw "Projects missing from the solution: $($unlisted -join ', ')" }
}

try {
    New-Item -ItemType Directory -Path $work | Out-Null

    # The source tree lists every architecture's projects, so it builds as it is. Its build output is removed
    # afterwards so it never reaches the package or the repository.
    Write-Host '== Build the template source in place'
    try {
        Invoke-Checked 'In-place build' {
            dotnet build (Join-Path $template 'MyApp.slnx') --configuration Release --nologo -warnaserror
        }
    }
    finally {
        Remove-BuildOutput $template
    }

    Write-Host '== Pack'
    Invoke-Checked 'dotnet pack' {
        dotnet pack (Join-Path $repo 'Pinotechrd.Templates.csproj') --configuration Release --output $packages --nologo --verbosity quiet
    }
    $package = Get-ChildItem -LiteralPath $packages -Filter '*.nupkg' | Select-Object -First 1
    if (-not $package) { throw 'No package was produced.' }

    Write-Host '== Install'
    Invoke-Checked 'dotnet new install' { dotnet new install $package.FullName --debug:custom-hive $hive | Out-Null }

    foreach ($case in $cases) {
        Write-Host "== Case $($case.Name)"
        $output = Join-Path $work $case.Name
        $options = $case.Options
        Invoke-Checked "Generating $($case.Name)" {
            dotnet new pinotechrd-app --name $projectName --output $output @options --debug:custom-hive $hive | Out-Null
        }

        $solution = Join-Path $output "$projectName.slnx"
        if (-not (Test-Path -LiteralPath $solution)) { throw "Missing solution: $solution" }
        Assert-NameReplaced $output
        Assert-Layout $output $case.Architecture $case.Module
        if ($case.Architecture -eq 'modular') { Assert-ModuleComposed $output $case.Module }
        Assert-SolutionMatchesProjects $solution

        # Built from inside the output, so the SDK is the one its own global.json pins, as for a user.
        Push-Location -LiteralPath $output
        try {
            Write-Host "SDK: $(dotnet --version)"
            Invoke-Checked "Building $($case.Name)" { dotnet build $solution --configuration Release --nologo -warnaserror }
            Invoke-Checked "Testing $($case.Name)" { dotnet test $solution --configuration Release --no-build --nologo }
        }
        finally {
            Pop-Location
        }
    }

    foreach ($case in $generateOnlyCases) {
        Write-Host "== Case $($case.Name) (generate only)"
        $output = Join-Path $work $case.Name
        $options = $case.Options
        Invoke-Checked "Generating $($case.Name)" {
            dotnet new pinotechrd-app --name $projectName --output $output @options --debug:custom-hive $hive | Out-Null
        }
        Assert-NameReplaced $output
        Assert-Layout $output $case.Architecture $case.Module
        if ($case.Architecture -eq 'modular') { Assert-ModuleComposed $output $case.Module }
        else { Assert-NameAbsent $output $case.Module }
        Assert-SolutionMatchesProjects (Join-Path $output "$projectName.slnx")
    }

    Write-Host '== Invalid options are rejected'
    Assert-Rejected 'An invalid --profile value' @('--profile', 'unknown')
    Assert-Rejected 'An invalid --architecture value' @('--architecture', 'unknown')

    Write-Host "All $($cases.Count + $generateOnlyCases.Count) cases passed."
}
finally {
    if ($Keep) { Write-Host "Working directory kept: $work" }
    elseif (Test-Path -LiteralPath $work) {
        try { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Stop }
        catch { Write-Warning "Could not remove the working directory ${work}: $($_.Exception.Message)" }
    }
}
