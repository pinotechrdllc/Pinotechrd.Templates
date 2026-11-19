#requires -Version 7
<#
.SYNOPSIS
  Builds the template source in place, packs the template package, installs it in an isolated
  hive, generates a project for each case and verifies the result.
.DESCRIPTION
  The template source must build as it is (it lists every architecture's projects). A case passes
  when the project is generated with the layout of its architecture, the placeholder name is fully
  replaced, no conditional marker or template configuration is left, its solution lists exactly its
  projects, it builds without warnings, its first restore writes a lock file beside every project, a locked restore in
  CI mode (NuGet audit findings as errors) passes and its tests pass. Invalid option values must be rejected. The
  package must hold only the template content, and no lock file. The modular case then adds the endpoint and tests of scripts/fixtures/module-endpoint to its
  module, and must report the contract as stale, regenerate it, build and pass its tests again.
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
# A case with a Fixture then adds the fixture's files to its output and checks the result (see Invoke-ModuleEndpointFixture);
# ModuleSegment is the route segment the template derives from the module name (kebab case, see its README).
$cases = @(
    @{ Name = 'clean-standalone'; Architecture = 'clean'; Options = @() },
    @{
        Name = 'modular-standalone'; Architecture = 'modular'; Module = 'Sales'; Options = @('--architecture', 'modular', '--module', 'Sales')
        Fixture = 'module-endpoint'; ModuleSegment = 'sales'
    }
)

# Files added to a generated project by a fixture step. Not template content: never packed, never in the source tree.
$fixtures = Join-Path $PSScriptRoot 'fixtures'

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
    "src/$projectName.Api/Properties/launchSettings.json",
    'LICENSE',
    "src/BuildingBlocks/$projectName.BuildingBlocks.Domain",
    "src/BuildingBlocks/$projectName.BuildingBlocks.Application",
    "src/BuildingBlocks/$projectName.BuildingBlocks.Infrastructure",
    "src/BuildingBlocks/$projectName.BuildingBlocks.Web",
    'contract',
    "tests/$projectName.BuildingBlocks.Tests",
    "tests/$projectName.Api.Tests",
    # The migrator command, its tests and the pinned dotnet-ef tool, in both architectures.
    "src/$projectName.Migrator/$projectName.Migrator.csproj",
    "tests/$projectName.Migrator.Tests",
    '.config/dotnet-tools.json',
    # The Access module is part of both architectures.
    "src/Modules/Access/$projectName.Modules.Access.Contracts/$projectName.Modules.Access.Contracts.csproj",
    "src/Modules/Access/$projectName.Modules.Access.Domain/$projectName.Modules.Access.Domain.csproj",
    "src/Modules/Access/$projectName.Modules.Access.Application/$projectName.Modules.Access.Application.csproj",
    "src/Modules/Access/$projectName.Modules.Access.Infrastructure/$projectName.Modules.Access.Infrastructure.csproj",
    "src/Modules/Access/$projectName.Modules.Access.Endpoints/$projectName.Modules.Access.Endpoints.csproj",
    "src/Modules/Access/$projectName.Modules.Access.Endpoints/AccessModule.cs"
)
$architecturePaths = @{
    clean   = @(
        @{ Path = "src/$projectName.Domain"; Required = $true },
        @{ Path = "src/$projectName.Contracts"; Required = $true },
        @{ Path = "src/$projectName.Resources"; Required = $true },
        @{ Path = "src/$projectName.Application"; Required = $true },
        @{ Path = "src/$projectName.Infrastructure"; Required = $true },
        @{ Path = "src/$projectName.Api/BusinessComposition.Clean.cs"; Required = $true },
        @{ Path = "tests/$projectName.Domain.Tests"; Required = $true },
        @{ Path = "tests/$projectName.Api.Tests/Architecture/Clean"; Required = $true }
    )
    modular = @(
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

# The NuGet lock files written by a restore, one beside each project file (not under node_modules).
function Get-LockFile([string]$Root) {
    Get-ChildItem -LiteralPath $Root -Recurse -File -Force -Filter 'packages.lock.json' |
        Where-Object { $_.FullName -notmatch '[\\/]node_modules[\\/]' }
}

# The template ships no lock file (the first restore of a generated project writes them), so the in-place build must
# not leave any behind for the package or the repository.
function Remove-LockFile([string]$Root) {
    foreach ($file in Get-LockFile $Root) {
        try { Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop }
        catch { Write-Warning "Could not remove $($file.FullName): $($_.Exception.Message)" }
    }
}

# The path a generated project follows: its first restore writes a lock file beside every project, and a CI build then
# restores in locked mode with NuGet audit findings as errors. --force makes the restore evaluate every project again
# (otherwise it is skipped as up to date and neither the lock files nor the audit are checked).
function Assert-LockedRestore([string]$Solution) {
    $root = Split-Path -Parent $Solution
    $projects = Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.csproj' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules)[\\/]' }
    $unlocked = $projects | Where-Object { -not (Test-Path -LiteralPath (Join-Path $_.DirectoryName 'packages.lock.json')) }
    if ($unlocked) { throw "Projects without a packages.lock.json after the first restore: $($unlocked.Name -join ', ')" }
    $stray = Get-LockFile $root | Where-Object { $_.DirectoryName -notin $projects.DirectoryName }
    if ($stray) { throw "Lock files not beside a project: $($stray.FullName -join ', ')" }

    Invoke-Checked 'Locked restore in CI mode' {
        dotnet restore $Solution --locked-mode --force -p:ContinuousIntegrationBuild=true --nologo --verbosity quiet
    }
    Write-Host "Locked restore passed for $($projects.Count) projects."
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

# The modules under src/Modules: Access in every output, plus the stamp module in a modular one. The host composes
# Access exactly once, in the shared composition file.
function Assert-Modules([string]$Root, [string]$Architecture, [string]$Module) {
    $expected = @('Access') + @(if ($Architecture -eq 'modular') { $Module })
    $actual = @(Get-ChildItem -LiteralPath (Join-Path $Root 'src/Modules') -Directory -Force | ForEach-Object Name)
    if (Compare-Object ($expected | Sort-Object) ($actual | Sort-Object) -CaseSensitive) {
        throw "The $Architecture output has the modules '$($actual -join ', ')', expected '$($expected -join ', ')'."
    }
    $composition = Join-Path $Root "src/$projectName.Api/BusinessComposition.cs"
    $registrations = @(Select-String -LiteralPath $composition -SimpleMatch 'AddModule<AccessModule>')
    if ($registrations.Count -ne 1) { throw "The host registers AccessModule $($registrations.Count) times, expected once." }
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

    Assert-Modules $Root $Architecture $Module

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

# The package holds the template content and the package metadata only: nothing from scripts/, fixtures included.
function Assert-PackageContent([string]$Package) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $entries = @($zip.Entries | ForEach-Object { $_.FullName -replace '\\', '/' })
    }
    finally {
        $zip.Dispose()
    }
    $outside = $entries | Where-Object { $_ -like 'content/*' -and $_ -notlike 'content/templates/*' }
    if ($outside) { throw "The package has content outside templates/: $($outside -join ', ')" }
    $leaked = $entries | Where-Object { $_ -like 'scripts/*' -or $_ -like '*/scripts/*' -or $_ -like '*/fixtures/module-endpoint/*' }
    if ($leaked) { throw "The package has files of the test scripts: $($leaked -join ', ')" }
    # A lock file of the in-place build names the placeholder projects of both layouts: never template content.
    $locks = $entries | Where-Object { $_ -like '*.lock.json' }
    if ($locks) { throw "The package has lock files: $($locks -join ', ')" }
}

# Copies every *.cs file under $Source to the same relative path under $Destination, replacing the placeholders (keys of
# $Names) in the paths and the content, as the template engine would. The fixture folder is not template content.
function Copy-Fixture([string]$Source, [string]$Destination, [hashtable]$Names) {
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File -Filter '*.cs') {
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        $text = [IO.File]::ReadAllText($file.FullName)
        foreach ($placeholder in $Names.Keys) {
            $relative = $relative.Replace($placeholder, $Names[$placeholder])
            $text = $text.Replace($placeholder, $Names[$placeholder])
        }
        $target = Join-Path $Destination $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        [IO.File]::WriteAllText($target, $text, [Text.UTF8Encoding]::new($false))
        Write-Host "Added $([IO.Path]::GetRelativePath((Split-Path -Parent $Destination), $target))"
    }
}

# The path a developer follows on day one in a modular project: add an endpoint to the module, see the committed contract
# reported as stale, regenerate it with the documented command, and get routing, tagging and validation right. Runs from
# inside the generated project, after its own build and tests passed.
function Invoke-ModuleEndpointFixture([string]$Root, [hashtable]$Case) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $source = Join-Path $fixtures $Case.Fixture
    $names = @{ MyApp = $projectName; MyModule = $Case.Module }
    $solution = Join-Path $Root "$projectName.slnx"
    $apiTests = Join-Path $Root "tests/$projectName.Api.Tests/$projectName.Api.Tests.csproj"
    $contract = Join-Path $Root 'contract/openapi.v1.json'
    $path = "/api/v1/$($Case.ModuleSegment)/widgets"

    Write-Host "== Fixture $($Case.Fixture): add the module endpoint and its tests"
    $testSource = Join-Path $source 'tests'
    if (-not (Get-ChildItem -LiteralPath $testSource -Recurse -File -Filter '*.cs')) {
        throw "Fixture $($Case.Fixture): no fixture test. Add the test of the endpoint (*.cs) under $testSource."
    }
    Copy-Fixture (Join-Path $source 'src') (Join-Path $Root 'src') $names
    Copy-Fixture $testSource (Join-Path $Root 'tests') $names

    Write-Host "== Fixture $($Case.Fixture): the committed contract is reported as stale"
    Invoke-Checked 'Building with the fixture' { dotnet build $solution --configuration Release --nologo -warnaserror }
    $staleTest = 'FullyQualifiedName~.Contract.ContractIsCurrentTests.ServedDocumentEqualsTheCommittedContract'
    $report = dotnet test $apiTests --configuration Release --no-build --nologo --filter $staleTest 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0) { throw "The contract test passed with a new endpoint and the old contract: a stale contract is not detected.`n$report" }
    if (-not $report.Contains($path, [StringComparison]::Ordinal)) { throw "The contract test failed, but without naming $path.`n$report" }
    $global:LASTEXITCODE = 0
    Write-Host "Stale contract detected: the contract test fails and names $path."

    Write-Host "== Fixture $($Case.Fixture): regenerate the contract"
    $before = (Get-FileHash -LiteralPath $contract).Hash
    Invoke-Checked 'Regenerating the contract' { dotnet build "src/$projectName.Api" -p:UpdateContract=true --nologo }
    if ((Get-FileHash -LiteralPath $contract).Hash -eq $before) { throw "Regenerating the contract did not change $contract." }
    $paths = (Get-Content -LiteralPath $contract -Raw | ConvertFrom-Json -AsHashtable).paths
    $operation = $paths[$path].post
    if (-not $operation) { throw "The regenerated contract has no POST $path." }
    if ($operation.operationId -cne 'CreateWidget') { throw "POST $path has the operationId '$($operation.operationId)', expected 'CreateWidget'." }
    $tags = @($operation.tags)
    if ($tags.Count -ne 1 -or $tags[0] -cne $Case.Module) { throw "POST $path has the tags '$($tags -join ', ')', expected '$($Case.Module)'." }
    $elsewhere = @($paths.Keys | Where-Object { $_ -like '*/widgets' -and $_ -cne $path })
    if ($elsewhere) { throw "The endpoint is also mapped outside the module prefix: $($elsewhere -join ', ')" }

    # The whole suite already passed before the fixture; running it again would add about a minute. What the fixture can
    # break runs again: the contract, architecture and layout tests, the module's tests and the fixture tests themselves.
    Write-Host "== Fixture $($Case.Fixture): build and test with the regenerated contract"
    Invoke-Checked 'Building after the regeneration' { dotnet build $solution --configuration Release --nologo -warnaserror }
    $moduleTests = Join-Path $Root "tests/$projectName.Modules.$($Case.Module).Tests/$projectName.Modules.$($Case.Module).Tests.csproj"
    $affected = 'FullyQualifiedName~.Contract.|FullyQualifiedName~.Architecture.|FullyQualifiedName~.Layout.|FullyQualifiedName~.Fixtures.'
    $ran = @()
    foreach ($run in @(@{ Project = $moduleTests; Filter = $null }, @{ Project = $apiTests; Filter = $affected })) {
        $filter = if ($run.Filter) { @('--filter', $run.Filter) } else { @() }
        # Run in this scope, not through Invoke-Checked, so the captured lines stay readable here.
        dotnet test $run.Project --configuration Release --no-build --nologo --logger 'console;verbosity=normal' @filter |
            Tee-Object -Variable lines | Write-Host
        if ($LASTEXITCODE -ne 0) { throw "Testing $(Split-Path -Leaf $run.Project) after the regeneration failed with exit code $LASTEXITCODE." }
        $ran += @($lines | Where-Object { $_ -match '^\s*Passed\s+\S+\.Fixtures\.' })
    }
    # A fixture test outside a Fixtures namespace would be skipped by the filter and pass unseen.
    if ($ran.Count -eq 0) { throw "No fixture test ran: fixture tests must be in a namespace ending in .Fixtures (see $testSource)." }
    Write-Host "Fixture tests passed: $($ran.Count)."

    Write-Host ("Fixture {0} added {1:N0} s." -f $Case.Fixture, $timer.Elapsed.TotalSeconds)
}

try {
    New-Item -ItemType Directory -Path $work | Out-Null

    # The source tree lists every architecture's projects, so it builds as it is. Its build output and the lock files
    # its restore writes are removed afterwards so they never reach the package or the repository.
    Write-Host '== Build the template source in place'
    try {
        Invoke-Checked 'In-place build' {
            dotnet build (Join-Path $template 'MyApp.slnx') --configuration Release --nologo -warnaserror
        }
    }
    finally {
        Remove-BuildOutput $template
        Remove-LockFile $template
    }

    Write-Host '== Pack'
    Invoke-Checked 'dotnet pack' {
        dotnet pack (Join-Path $repo 'Pinotechrd.Templates.csproj') --configuration Release --output $packages --nologo --verbosity quiet
    }
    $package = Get-ChildItem -LiteralPath $packages -Filter '*.nupkg' | Select-Object -First 1
    if (-not $package) { throw 'No package was produced.' }
    Assert-PackageContent $package.FullName

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
        $shipped = Get-LockFile $output
        if ($shipped) { throw "The generated project has lock files before its first restore: $($shipped.FullName -join ', ')" }

        # Built from inside the output, so the SDK is the one its own global.json pins, as for a user.
        Push-Location -LiteralPath $output
        try {
            Write-Host "SDK: $(dotnet --version)"
            Invoke-Checked "Building $($case.Name)" { dotnet build $solution --configuration Release --nologo -warnaserror }
            $lockTimer = [Diagnostics.Stopwatch]::StartNew()
            Assert-LockedRestore $solution
            Write-Host ("Lock file checks took {0:N1} s." -f $lockTimer.Elapsed.TotalSeconds)
            Invoke-Checked "Testing $($case.Name)" { dotnet test $solution --configuration Release --no-build --nologo }
            if ($case.Fixture) { Invoke-ModuleEndpointFixture $output $case }
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
