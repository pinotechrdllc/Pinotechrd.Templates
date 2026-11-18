#requires -Version 7
<#
.SYNOPSIS
  Measures what the `modular` architecture costs against `clean` on the projects the template really generates.
.DESCRIPTION
  Packs the template from a copy of its sources (the repository is only read), installs the package in an isolated
  hive and generates Acme.Demo three times in a temporary folder: `clean`, `modular --module Sales`, and the same
  modular project with a second module (Inventory) copied from the first by hand, the way a developer would do it
  before the module script exists.

  For each project it counts projects, C# files and project files (bin and obj excluded) and times, in the Debug
  configuration and from inside the generated folder (so its global.json picks the SDK):
    - restore, after `dotnet build-server shutdown` and deleting bin and obj (the NuGet cache stays warm);
    - cold build with --no-restore, right after that restore, with the build servers still stopped;
    - build with no change, twice: right after the cold build, then again (the steady state);
    - the tests that do not need Docker, with --no-build (filter in $testFilter);
    - build after a change inside a method body of the innermost business project (a comment appended to its
      marker file; the public surface does not change);
    - build after a change of the public surface of that project (a public type appended to the same file).
  The runs are interleaved (run 1 of every layout, then run 2...) so a change in the machine load hits every layout.
.PARAMETER Runs
  Repetitions of each measure; the median and the range are reported.
.PARAMETER Keep
  Keep the working folder for inspection.
#>
param([int]$Runs = 3, [switch]$Keep)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '../..')
$work = Join-Path ([IO.Path]::GetTempPath()) ("architecture-cost-" + [Guid]::NewGuid().ToString('N'))
$projectName = 'Acme.Demo'
$firstModule = 'Sales'
$secondModule = 'Inventory'

# The test classes that start the shared PostgreSQL container (Testcontainers). Everything else runs without Docker.
$dockerClasses = @(
    'OpenedConnectionSettingsTests', 'DataSourceBehaviorTests', 'DatabaseTelemetryTests', 'LiveProbeTests',
    'ReadinessFailureModeTests', 'ReadinessTests', 'SpanErrorTests'
)
$testFilter = ($dockerClasses | ForEach-Object { "FullyQualifiedName!~.Database.$_." }) -join '&'

function Invoke-Checked([string]$What, [scriptblock]$Command) {
    $output = & $Command 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | Select-Object -Last 40 | Write-Host
        throw "$What failed with exit code $LASTEXITCODE."
    }
    $output
}

function Measure-Seconds([string]$What, [scriptblock]$Command) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $output = Invoke-Checked $What $Command
    $watch.Stop()
    [pscustomobject]@{ Seconds = $watch.Elapsed.TotalSeconds; Output = $output }
}

# Copies a folder without the build output, so packing never reads or writes inside the repository.
function Copy-Source([string]$From, [string]$To) {
    New-Item -ItemType Directory -Path $To -Force | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $From -Force) {
        if ($entry.PSIsContainer) {
            if ($entry.Name -notin 'bin', 'obj', 'node_modules', '.vs', 'TestResults') {
                Copy-Source $entry.FullName (Join-Path $To $entry.Name)
            }
        }
        else { Copy-Item -LiteralPath $entry.FullName -Destination $To }
    }
}

function Get-SourceFiles([string]$Root, [string[]]$Patterns) {
    Get-ChildItem -LiteralPath $Root -Recurse -File -Include $Patterns |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules)[\\/]' }
}

function Measure-Size([string]$Root) {
    $projects = @(Get-SourceFiles $Root '*.csproj')
    $tests = @($projects | Where-Object { $_.BaseName -like '*.Tests' })
    $code = @(Get-SourceFiles $Root '*.cs')
    $projectFiles = @(Get-SourceFiles $Root '*.csproj', '*.slnx', '*.props', '*.targets')
    $lines = { param($files) ($files | ForEach-Object { (Get-Content -LiteralPath $_.FullName).Count } | Measure-Object -Sum).Sum }
    [pscustomobject]@{
        Production = $projects.Count - $tests.Count; Tests = $tests.Count
        CodeFiles = $code.Count; CodeLines = & $lines $code
        ProjectFiles = $projectFiles.Count; ProjectLines = & $lines $projectFiles
    }
}

function Remove-BuildOutput([string]$Root) {
    Get-ChildItem -LiteralPath $Root -Recurse -Directory -Force -Include bin, obj |
        Where-Object { Test-Path -LiteralPath $_.FullName } |
        Remove-Item -Recurse -Force
}

# Copies the generated module to a second name: folders, files and text inside the module and its test project, then
# the four places outside it that name a module: the solution, the host project, the README layout table and the host
# composition.
function Add-ModuleByCopy([string]$Root, [string]$From, [string]$To) {
    $pairs = @(
        @{ Source = "src/Modules/$From"; Target = "src/Modules/$To" },
        @{ Source = "tests/$projectName.Modules.$From.Tests"; Target = "tests/$projectName.Modules.$To.Tests" }
    )
    foreach ($pair in $pairs) {
        $target = Join-Path $Root $pair.Target
        Copy-Source (Join-Path $Root $pair.Source) $target
        # Deepest paths first, so a folder is renamed after the entries inside it.
        Get-ChildItem -LiteralPath $target -Recurse -Force | Sort-Object { $_.FullName.Length } -Descending |
            Where-Object { $_.Name -clike "*$From*" } |
            ForEach-Object { Rename-Item -LiteralPath $_.FullName -NewName ($_.Name.Replace($From, $To)) }
        Get-ChildItem -LiteralPath $target -Recurse -File | ForEach-Object {
            $text = [IO.File]::ReadAllText($_.FullName)
            if ($text.Contains($From)) { [IO.File]::WriteAllText($_.FullName, $text.Replace($From, $To)) }
        }
    }

    $solution = Join-Path $Root "$projectName.slnx"
    $xml = [xml](Get-Content -LiteralPath $solution -Raw)
    $folder = $xml.SelectSingleNode("//Folder[@Name='/src/Modules/$From/']")
    $copy = $folder.CloneNode($true)
    $copy.SetAttribute('Name', "/src/Modules/$To/")
    foreach ($project in $copy.SelectNodes('Project')) { $project.SetAttribute('Path', $project.Path.Replace($From, $To)) }
    $folder.ParentNode.InsertAfter($copy, $folder) | Out-Null
    $testProject = $xml.SelectSingleNode("//Project[@Path='tests/$projectName.Modules.$From.Tests/$projectName.Modules.$From.Tests.csproj']")
    $testCopy = $testProject.CloneNode($true)
    $testCopy.SetAttribute('Path', $testProject.Path.Replace($From, $To))
    $testProject.ParentNode.InsertAfter($testCopy, $testProject) | Out-Null
    $xml.Save($solution)

    $host_ = Join-Path $Root "src/$projectName.Api/$projectName.Api.csproj"
    $line = "<ProjectReference Include=`"..\Modules\$From\$projectName.Modules.$From.Endpoints\$projectName.Modules.$From.Endpoints.csproj`" />"
    $text = [IO.File]::ReadAllText($host_)
    if (-not $text.Contains($line)) { throw "The host project does not reference the $From module as expected." }
    [IO.File]::WriteAllText($host_, $text.Replace($line, "$line`n    $($line.Replace($From, $To))"))

    # The README layout table names every project; a test fails when one on disk is missing from it.
    $readme = Join-Path $Root 'README.md'
    $rowPattern = '^\| `(src/Modules/' + $From + '/|tests/' + [regex]::Escape("$projectName.Modules.$From.Tests") + '`)'
    $rows = foreach ($row in [IO.File]::ReadAllLines($readme)) {
        $row
        if ($row -match $rowPattern) { $row.Replace($From, $To) }
    }
    [IO.File]::WriteAllLines($readme, [string[]]$rows)

    $composition = Join-Path $Root "src/$projectName.Api/BusinessComposition.Modular.cs"
    $text = [IO.File]::ReadAllText($composition)
    $using = "using $projectName.Modules.$From.Endpoints;"
    $add = "builder.AddModule<${From}Module>();"
    if (-not ($text.Contains($using) -and $text.Contains($add))) { throw "The host composition does not register the $From module as expected." }
    $text = $text.Replace($using, "$using`n$($using.Replace($From, $To))")
    $text = $text.Replace($add, "$add`n        $($add.Replace($From, $To))")
    [IO.File]::WriteAllText($composition, $text)
}

function Get-Stat([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    $median = if ($sorted.Count % 2) { $sorted[[int][Math]::Floor($sorted.Count / 2)] }
    else { ($sorted[$sorted.Count / 2 - 1] + $sorted[$sorted.Count / 2]) / 2 }
    [pscustomobject]@{ Median = $median; Min = $sorted[0]; Max = $sorted[-1] }
}

function Format-Stat($Stat) { '{0:N1} ({1:N1} to {2:N1})' -f $Stat.Median, $Stat.Min, $Stat.Max }

function Get-CpuLoad { (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average }

# One run of every timed step on one generated project. The marker file is restored at the end of the run.
function Invoke-Run($Layout) {
    $solution = Join-Path $Layout.Root "$projectName.slnx"
    $original = [IO.File]::ReadAllText($Layout.Marker)
    Push-Location -LiteralPath $Layout.Root
    try {
        dotnet build-server shutdown *> $null
        Remove-BuildOutput $Layout.Root
        $result = [ordered]@{}
        $result.Restore = (Measure-Seconds 'restore' { dotnet restore $solution --nologo --verbosity quiet }).Seconds
        $result.Cold = (Measure-Seconds 'cold build' { dotnet build $solution --no-restore --nologo --verbosity quiet }).Seconds
        $result.FirstNoChange = (Measure-Seconds 'first no-change build' { dotnet build $solution --no-restore --nologo --verbosity quiet }).Seconds
        $result.NoChange = (Measure-Seconds 'no-change build' { dotnet build $solution --no-restore --nologo --verbosity quiet }).Seconds
        $test = Measure-Seconds 'tests' { dotnet test $solution --no-build --nologo --filter $testFilter }
        $result.Tests = $test.Seconds
        $result.TestCount = (($test.Output | Out-String) | Select-String -AllMatches 'Passed:\s+(\d+)').Matches |
            ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum | Select-Object -ExpandProperty Sum

        [IO.File]::AppendAllText($Layout.Marker, "`n// measurement $([Guid]::NewGuid())`n")
        $result.BodyChange = (Measure-Seconds 'build after a body change' { dotnet build $solution --no-restore --nologo --verbosity quiet }).Seconds

        $probe = 'MeasurementProbe' + [Guid]::NewGuid().ToString('N')
        [IO.File]::AppendAllText($Layout.Marker, "`n/// <summary>Added by the measurement.</summary>`npublic sealed class $probe { }`n")
        $result.PublicChange = (Measure-Seconds 'build after a public change' { dotnet build $solution --no-restore --nologo --verbosity quiet }).Seconds
        [pscustomobject]$result
    }
    finally {
        [IO.File]::WriteAllText($Layout.Marker, $original)
        Pop-Location
    }
}

try {
    New-Item -ItemType Directory -Path $work | Out-Null
    $source = Join-Path $work 'source'
    $hive = Join-Path $work 'hive'
    New-Item -ItemType Directory -Path $source | Out-Null
    foreach ($file in 'Pinotechrd.Templates.csproj', 'README.md', 'global.json') { Copy-Item -LiteralPath (Join-Path $repo $file) -Destination $source }
    Copy-Source (Join-Path $repo 'templates') (Join-Path $source 'templates')

    Write-Host '== Pack and install in an isolated hive'
    Invoke-Checked 'dotnet pack' { dotnet pack (Join-Path $source 'Pinotechrd.Templates.csproj') --configuration Release --output (Join-Path $work 'packages') --nologo --verbosity quiet } | Out-Null
    $package = Get-ChildItem -LiteralPath (Join-Path $work 'packages') -Filter '*.nupkg' | Select-Object -First 1
    Invoke-Checked 'dotnet new install' { dotnet new install $package.FullName --debug:custom-hive $hive } | Out-Null

    $layouts = @(
        @{ Name = 'clean'; Options = @(); Marker = "src/$projectName.Domain/DomainAssembly.cs" },
        @{ Name = 'modular, 1 module'; Options = @('--architecture', 'modular', '--module', $firstModule)
            Marker = "src/Modules/$firstModule/$projectName.Modules.$firstModule.Domain/${firstModule}DomainAssembly.cs" },
        @{ Name = 'modular, 2 modules'; Options = @('--architecture', 'modular', '--module', $firstModule); SecondModule = $secondModule
            Marker = "src/Modules/$firstModule/$projectName.Modules.$firstModule.Domain/${firstModule}DomainAssembly.cs" }
    )
    $index = 0
    foreach ($layout in $layouts) {
        $index++
        $layout.Root = Join-Path $work "out$index"
        $options = $layout.Options
        Write-Host "== Generate: $($layout.Name)"
        Invoke-Checked "Generating $($layout.Name)" { dotnet new pinotechrd-app --name $projectName --output $layout.Root @options --debug:custom-hive $hive } | Out-Null
        if ($layout.SecondModule) { Add-ModuleByCopy $layout.Root $firstModule $layout.SecondModule }
        $layout.Marker = Join-Path $layout.Root $layout.Marker
        $layout.Size = Measure-Size $layout.Root
        $layout.Runs = @()
    }

    Push-Location -LiteralPath $layouts[0].Root
    $sdk = dotnet --version
    Pop-Location
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    $ram = [Math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
    $os = Get-CimInstance Win32_OperatingSystem

    foreach ($run in 1..$Runs) {
        foreach ($layout in $layouts) {
            $load = Get-CpuLoad
            Write-Host "== Run $run of ${Runs}: $($layout.Name) (CPU load before: $load%)"
            $entry = Invoke-Run $layout
            $layout.Runs += $entry
            Write-Host ($entry | ConvertTo-Json -Compress)
        }
    }

    # Measure key and column title, in the order the steps run.
    $measures = [ordered]@{
        Restore = 'Restore'; Cold = 'Cold build'; FirstNoChange = 'No change, first'; NoChange = 'No change'
        Tests = 'Tests'; BodyChange = 'Body change in Domain'; PublicChange = 'Public change in Domain'
    }
    $median = { param($layout, $key) (Get-Stat @($layout.Runs | ForEach-Object { $_.$key })).Median }

    $lines = @(
        "Machine: $($cpu.Name.Trim()), $($cpu.NumberOfCores) cores, $($cpu.NumberOfLogicalProcessors) logical processors, $ram GB RAM, $($os.Caption) $($os.Version). .NET SDK $sdk. $(Get-Date -Format 'yyyy-MM-dd')."
        "Runs: $Runs, interleaved. Debug configuration. Times in seconds: median (min to max)."
        "Tests: dotnet test --no-build --filter `"$testFilter`""
        ''
        '| Layout | Projects (prod + tests) | C# files / lines | Project files / lines | Tests passed |'
        '| --- | --- | --- | --- | --- |'
    )
    foreach ($layout in $layouts) {
        $size = $layout.Size
        $counts = @($layout.Runs | ForEach-Object { $_.TestCount } | Sort-Object -Unique) -join '/'
        $lines += "| $($layout.Name) | $($size.Production) + $($size.Tests) | $($size.CodeFiles) / $($size.CodeLines) | $($size.ProjectFiles) / $($size.ProjectLines) | $counts |"
    }

    $lines += ''
    $lines += '| Layout | ' + ($measures.Values -join ' | ') + ' |'
    $lines += '| --- |' + (' --- |' * $measures.Count)
    foreach ($layout in $layouts) {
        $cells = foreach ($key in $measures.Keys) { Format-Stat (Get-Stat @($layout.Runs | ForEach-Object { $_.$key })) }
        $lines += "| $($layout.Name) | $($cells -join ' | ') |"
    }

    $lines += ''
    $lines += '| Ratio of medians against clean | ' + ($measures.Values -join ' | ') + ' |'
    $lines += '| --- |' + (' --- |' * $measures.Count)
    foreach ($layout in $layouts | Select-Object -Skip 1) {
        $cells = foreach ($key in $measures.Keys) { '{0:N2}' -f ((& $median $layout $key) / (& $median $layouts[0] $key)) }
        $lines += "| $($layout.Name) | $($cells -join ' | ') |"
    }

    $lines += ''
    $lines += 'Every run (seconds):'
    foreach ($layout in $layouts) {
        foreach ($entry in $layout.Runs) {
            $values = foreach ($key in $measures.Keys) { "$($measures[$key].ToLowerInvariant()) $('{0:N1}' -f $entry.$key)" }
            $lines += "- $($layout.Name): $($values -join ', ') ($($entry.TestCount) tests passed)"
        }
    }
    $lines | Write-Host
    $global:LASTEXITCODE = 0
}
finally {
    dotnet build-server shutdown *> $null
    if ($Keep) { Write-Host "Working folder kept: $work" }
    elseif (Test-Path -LiteralPath $work) {
        try { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Stop }
        catch { Write-Warning "Could not remove the working folder ${work}: $($_.Exception.Message)" }
    }
}
