#requires -Version 7
<#
.SYNOPSIS
  Spike: proves how the dotnet new engine builds two project layouts from one source tree.
.DESCRIPTION
  Installs the spike template (and variants of it made on the fly) in an isolated hive,
  generates each case, and prints one PASS / FAIL / NOT SUPPORTED line per question of the
  spike. Exits 0 only when every mechanism the plan relies on (questions 1 to 7) works.
  Everything runs in a temp folder that pins the repository SDK through its global.json.
.PARAMETER Keep
  Keep the working directory after the run, for inspection.
#>
param([switch]$Keep)

$ErrorActionPreference = 'Stop'
$spike = $PSScriptRoot
$repo = Split-Path -Parent (Split-Path -Parent $spike)
$template = Join-Path $spike 'template'
$work = Join-Path ([IO.Path]::GetTempPath()) ("pinotechrd-spike-layouts-" + [Guid]::NewGuid().ToString('N'))
$hive = Join-Path $work 'hive'
$name = 'Acme.Demo'
$results = [ordered]@{}
$notes = [Collections.Generic.List[string]]::new()

function Set-Result([int]$Question, [string]$Status, [string]$Evidence) {
    $results["$Question"] = [pscustomobject]@{ Status = $Status; Evidence = $Evidence }
}

function Invoke-Dotnet([string[]]$Arguments) {
    $text = & dotnet @Arguments 2>&1 | Out-String
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = $text }
}

function Install-Template([string]$Path) {
    $result = Invoke-Dotnet @('new', 'install', $Path, '--debug:custom-hive', $hive)
    if ($result.ExitCode -ne 0) { throw "dotnet new install $Path failed ($($result.ExitCode)):`n$($result.Text)" }
}

function New-Output([string]$ShortName, [string]$Case, [string[]]$Options = @()) {
    $output = Join-Path $work "out/$Case"
    $result = Invoke-Dotnet (@('new', $ShortName, '--name', $name, '--output', $output) + $Options + @('--debug:custom-hive', $hive))
    $result | Add-Member Path $output -PassThru
}

# A variant is a copy of the template whose template.json is changed by $Edit (a hashtable).
function New-Variant([string]$Variant, [scriptblock]$Edit) {
    $path = Join-Path $work "variants/$Variant"
    Copy-Item -LiteralPath $template -Destination $path -Recurse
    $file = Join-Path $path '.template.config/template.json'
    $json = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable
    $json.identity = "$($json.identity).$Variant"
    $json.shortName = "spike-layouts-$Variant"
    & $Edit $json $path
    $json | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $file
    Install-Template $path
    $json.shortName
}

function Get-Files([string]$Root) {
    Get-ChildItem -LiteralPath $Root -Recurse -Force -File |
        ForEach-Object { [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/') } | Sort-Object
}

function Get-Dirs([string]$Root) {
    Get-ChildItem -LiteralPath $Root -Recurse -Force -Directory |
        ForEach-Object { [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/') } | Sort-Object
}

function Test-SameList([string[]]$Actual, [string[]]$Expected) {
    -not (Compare-Object @($Actual) @($Expected) -SyncWindow 0)
}

function Get-ExpectedFiles([string]$Architecture, [string]$Module) {
    $shared = @("$name.slnx", 'README.md', "src/$name.Api/$name.Api.csproj", "src/$name.Api/Program.cs", "src/$name.Api/Startup.cs")
    if ($Architecture -eq 'clean') {
        $own = @("src/$name.Api/Startup.Clean.cs",
            "src/$name.Application/$name.Application.csproj", "src/$name.Application/ApplicationMarker.cs",
            "src/$name.Domain/$name.Domain.csproj", "src/$name.Domain/DomainMarker.cs")
    }
    else {
        $folder = "src/Modules/$Module/$name.Modules.$Module.Domain"
        $own = @("src/$name.Api/Startup.Modular.cs", "$folder/$name.Modules.$Module.Domain.csproj", "$folder/${Module}Module.cs")
    }
    ($shared + $own) | Sort-Object
}

function Get-LeftPlaceholders([string]$Root) {
    $left = @()
    $left += Get-ChildItem -LiteralPath $Root -Recurse -Force | Where-Object { $_.Name -match 'MyApp|MyModule' } | ForEach-Object Name
    $left += Get-ChildItem -LiteralPath $Root -Recurse -Force -File |
        Where-Object { Select-String -LiteralPath $_.FullName -Pattern 'MyApp|MyModule' -Quiet } | ForEach-Object { "$($_.Name) (content)" }
    $left
}

function Test-NoMarkers([string]$File) {
    -not (Select-String -LiteralPath $File -Pattern '#if|#else|#elif|#endif|<!--' -Quiet)
}

function Get-SolutionProjects([string]$Root) {
    ([xml](Get-Content -LiteralPath (Join-Path $Root "$name.slnx") -Raw)).SelectNodes('//Project') | ForEach-Object Path | Sort-Object
}

function Get-ApiReferences([string]$Root) {
    ([xml](Get-Content -LiteralPath (Join-Path $Root "src/$name.Api/$name.Api.csproj") -Raw)).SelectNodes('//ProjectReference') |
        ForEach-Object Include | Sort-Object
}

# Builds the generated solution with warnings as errors and runs the Api; returns its output.
function Invoke-BuildAndRun([string]$Root) {
    $build = Invoke-Dotnet @('build', (Join-Path $Root "$name.slnx"), '--nologo', '-warnaserror', '-v', 'q')
    if ($build.ExitCode -ne 0) {
        $errors = ($build.Text -split "`n" | Where-Object { $_ -match ': error ' } | ForEach-Object { ($_ -replace '\s*\[[^\]]*\]\s*$', '' -replace '^.*\\', '').Trim() } | Select-Object -Unique -First 2) -join ' | '
        return [pscustomobject]@{ Built = $false; Output = $null; Errors = $errors }
    }
    $run = Invoke-Dotnet @('run', '--project', (Join-Path $Root "src/$name.Api/$name.Api.csproj"), '--no-build')
    [pscustomobject]@{ Built = $true; Output = $run.Text.Trim(); Errors = '' }
}

try {
    New-Item -ItemType Directory -Path $work | Out-Null
    # Generated projects build outside the repository; pin the same SDK the repository uses.
    Copy-Item -LiteralPath (Join-Path $repo 'global.json') -Destination $work
    Push-Location $work
    $sdk = (& dotnet --version).Trim()
    Write-Host "SDK $sdk, work folder $work"

    Install-Template $template

    # Main template: exclude modifiers on the computed symbol 'modular'.
    $clean = New-Output 'spike-layouts' 'clean'
    $sales = New-Output 'spike-layouts' 'modular-sales' @('--architecture', 'modular', '--module', 'Sales')
    $core = New-Output 'spike-layouts' 'modular-default' @('--architecture', 'modular')
    $main = @(
        @{ Out = $clean; Arch = 'clean'; Module = '' },
        @{ Out = $sales; Arch = 'modular'; Module = 'Sales' },
        @{ Out = $core; Arch = 'modular'; Module = 'Core' })
    foreach ($case in $main) {
        if ($case.Out.ExitCode -ne 0) { throw "Generation failed for $($case.Out.Path):`n$($case.Out.Text)" }
    }

    # Variant: include modifiers instead of exclude modifiers.
    $includeName = New-Variant 'include' {
        param($json)
        $json.sources = @(@{
                include   = @('MyApp.slnx', 'README.md', 'src/MyApp.Api/MyApp.Api.csproj', 'src/MyApp.Api/Program.cs', 'src/MyApp.Api/Startup.cs')
                modifiers = @(
                    @{ condition = '(modular)'; include = @('src/Modules/**', 'src/MyApp.Api/Startup.Modular.cs') },
                    @{ condition = '(!modular)'; include = @('src/MyApp.Domain/**', 'src/MyApp.Application/**', 'src/MyApp.Api/Startup.Clean.cs') })
            })
    }
    $includeClean = New-Output $includeName 'include-clean'
    $includeSales = New-Output $includeName 'include-sales' @('--architecture', 'modular', '--module', 'Sales')

    # ---- Q1: sources[].modifiers with condition + exclude (and the include form).
    $q1 = @()
    foreach ($case in $main) {
        $files = Get-Files $case.Out.Path
        if (-not (Test-SameList $files (Get-ExpectedFiles $case.Arch $case.Module))) { $q1 += "$(Split-Path -Leaf $case.Out.Path) files: $($files -join ', ')" }
    }
    if (Test-Path (Join-Path $clean.Path 'src/Modules')) { $q1 += 'clean has src/Modules' }
    $includeSame = ($includeClean.ExitCode -eq 0) -and ($includeSales.ExitCode -eq 0) -and
        (Test-SameList (Get-Files $includeClean.Path) (Get-Files $clean.Path)) -and
        (Test-SameList (Get-Files $includeSales.Path) (Get-Files $sales.Path))
    if ($q1) { Set-Result 1 'FAIL' ($q1 -join '; ') }
    else {
        Set-Result 1 'PASS' ("exclude form: clean has $(@(Get-Files $clean.Path).Count) files and no src/Modules or Startup.Modular.cs; modular has $(@(Get-Files $sales.Path).Count) files and no Domain/Application/Startup.Clean.cs. " +
            "include form gives identical file lists: $includeSame")
    }

    # ---- Q2: computed symbol in modifier conditions and in file regions.
    $readmeClean = Get-Content -LiteralPath (Join-Path $clean.Path 'README.md') -Raw
    $readmeSales = Get-Content -LiteralPath (Join-Path $sales.Path 'README.md') -Raw
    $regionsOk = ($readmeClean -match 'Layout: clean') -and ($readmeClean -notmatch 'Layout: modular') -and
        ($readmeSales -match 'Layout: modular') -and ($readmeSales -notmatch 'Layout: clean')
    if ($results["1"].Status -eq 'PASS' -and $regionsOk) { Set-Result 2 'PASS' 'modifiers conditioned on (modular)/(!modular) removed the right files; #if (modular) regions picked the right branch in both outputs' }
    else { Set-Result 2 'FAIL' "modifiers: $($results["1"].Status); regions picked right branch: $regionsOk" }

    # ---- Q3: conditional regions in .slnx (default processing, no specialCustomOperations).
    $expectSlnClean = @("src/$name.Api/$name.Api.csproj", "src/$name.Application/$name.Application.csproj", "src/$name.Domain/$name.Domain.csproj") | Sort-Object
    $expectSlnSales = @("src/$name.Api/$name.Api.csproj", "src/Modules/Sales/$name.Modules.Sales.Domain/$name.Modules.Sales.Domain.csproj") | Sort-Object
    $slnOk = (Test-SameList (Get-SolutionProjects $clean.Path) $expectSlnClean) -and (Test-SameList (Get-SolutionProjects $sales.Path) $expectSlnSales) -and
        (Test-NoMarkers (Join-Path $clean.Path "$name.slnx")) -and (Test-NoMarkers (Join-Path $sales.Path "$name.slnx"))

    # Fallback, proven either way: two solution files, one excluded, the other renamed by the modifier.
    $twoName = New-Variant 'twosolutions' {
        param($json, $path)
        $source = Get-Content -LiteralPath (Join-Path $path 'MyApp.slnx') -Raw
        $cleanSln = $source -replace '(?s)<!--#if \(modular\) -->.*?<!--#else -->\r?\n', '' -replace '<!--#endif -->\r?\n', ''
        $modularSln = $source -replace '(?s)<!--#else -->.*?<!--#endif -->\r?\n', '' -replace '<!--#if \(modular\) -->\r?\n', ''
        Set-Content -LiteralPath (Join-Path $path 'MyApp.Clean.slnx') -Value $cleanSln -NoNewline
        Set-Content -LiteralPath (Join-Path $path 'MyApp.Modular.slnx') -Value $modularSln -NoNewline
        Remove-Item -LiteralPath (Join-Path $path 'MyApp.slnx')
        $json.sources[0].modifiers[0].exclude += 'MyApp.Modular.slnx'
        $json.sources[0].modifiers[0].rename = @{ 'MyApp.Clean.slnx' = 'MyApp.slnx' }
        $json.sources[0].modifiers[1].exclude += 'MyApp.Clean.slnx'
        $json.sources[0].modifiers[1].rename = @{ 'MyApp.Modular.slnx' = 'MyApp.slnx' }
    }
    $twoClean = New-Output $twoName 'two-clean'
    $twoSales = New-Output $twoName 'two-sales' @('--architecture', 'modular', '--module', 'Sales')
    $fallbackOk = ($twoClean.ExitCode -eq 0) -and ($twoSales.ExitCode -eq 0) -and
        (Test-SameList (Get-Files $twoClean.Path) (Get-Files $clean.Path)) -and (Test-SameList (Get-Files $twoSales.Path) (Get-Files $sales.Path)) -and
        (Test-SameList (Get-SolutionProjects $twoClean.Path) $expectSlnClean) -and (Test-SameList (Get-SolutionProjects $twoSales.Path) $expectSlnSales)
    if ($slnOk) { Set-Result 3 'PASS' "<!--#if (modular) --> / <!--#else --> / <!--#endif --> processed by default in .slnx; right projects listed, no markers left. Fallback (two .slnx + exclude + modifier rename) also works: $fallbackOk" }
    elseif ($fallbackOk) { Set-Result 3 'PASS' 'regions in .slnx did NOT work; fallback (two .slnx + exclude + modifier rename) works' }
    else { Set-Result 3 'FAIL' "regions in .slnx: $slnOk; fallback: $fallbackOk" }

    # ---- Q4: regions in .csproj and README.md.
    $refsClean = Get-ApiReferences $clean.Path
    $refsSales = Get-ApiReferences $sales.Path
    $csprojOk = (Test-SameList $refsClean @("..\$name.Application\$name.Application.csproj")) -and
        (Test-SameList $refsSales @("..\Modules\Sales\$name.Modules.Sales.Domain\$name.Modules.Sales.Domain.csproj")) -and
        (Test-NoMarkers (Join-Path $clean.Path "src/$name.Api/$name.Api.csproj")) -and (Test-NoMarkers (Join-Path $sales.Path "src/$name.Api/$name.Api.csproj"))
    $mdOk = $regionsOk -and ($readmeClean -match 'Shared text') -and ($readmeSales -match 'Shared text') -and
        (Test-NoMarkers (Join-Path $clean.Path 'README.md')) -and (Test-NoMarkers (Join-Path $sales.Path 'README.md'))
    if ($csprojOk -and $mdOk) { Set-Result 4 'PASS' 'csproj: one ProjectReference per layout, no markers; README.md: <!--#if --> HTML-comment syntax works, right branch kept, markers removed' }
    else { Set-Result 4 'FAIL' "csproj ok: $csprojOk; README ok: $mdOk" }

    # ---- Q5: module name in content, file names and folder names.
    $q5 = @()
    foreach ($case in $main | Where-Object Arch -eq 'modular') {
        $m = $case.Module
        $class = Join-Path $case.Out.Path "src/Modules/$m/$name.Modules.$m.Domain/${m}Module.cs"
        $project = Join-Path $case.Out.Path "src/Modules/$m/$name.Modules.$m.Domain/$name.Modules.$m.Domain.csproj"
        if (-not (Test-Path -LiteralPath $project)) { $q5 += "missing $project" ; continue }
        $text = Get-Content -LiteralPath $class -Raw
        if ($text -notmatch "namespace $([regex]::Escape("$name.Modules.$m.Domain"));" -or $text -notmatch "class ${m}Module\b" -or $text -notmatch "Name = `"$m`"") { $q5 += "unexpected content in $class" }
    }
    # Names that collide with other words in the layout, and names that are not identifiers.
    $collisions = @()
    foreach ($m in 'Api', 'Domain', 'Modules', 'MyApp', 'Sales-Orders', 'sales') {
        $out = New-Output 'spike-layouts' "module-$m" @('--architecture', 'modular', '--module', $m)
        $left = @(Get-LeftPlaceholders $out.Path).Count
        $br = Invoke-BuildAndRun $out.Path
        $collisions += if ($br.Built) { "$m=builds('$($br.Output)', placeholders left: $left)" } else { "$m=build FAILS ($($br.Errors))" }
    }
    $notes.Add("Q5 module names: $($collisions -join '; ')")
    if ($q5) { Set-Result 5 'FAIL' ($q5 -join '; ') }
    else { Set-Result 5 'PASS' "replaces+fileRename 'MyModule' renamed text, files and folders: src/Modules/Sales/$name.Modules.Sales.Domain/$name.Modules.Sales.Domain.csproj with class SalesModule; default gives Core/CoreModule" }

    # ---- Q7 (checked before building, so bin/obj do not count): no template config, nothing extra, no placeholders.
    $q7 = @()
    foreach ($case in $main) {
        $leaf = Split-Path -Leaf $case.Out.Path
        if (Get-Dirs $case.Out.Path | Where-Object { $_ -match '\.template\.config' }) { $q7 += "$leaf has .template.config" }
        $left = Get-LeftPlaceholders $case.Out.Path
        if ($left) { $q7 += "$leaf placeholders: $($left -join ', ')" }
        if (-not (Test-SameList (Get-Files $case.Out.Path) (Get-ExpectedFiles $case.Arch $case.Module))) { $q7 += "$leaf has unexpected files" }
    }
    if ($q7) { Set-Result 7 'FAIL' ($q7 -join '; ') }
    else { Set-Result 7 'PASS' 'no .template.config, file lists exactly as expected, no MyApp/MyModule in names or content (clean, modular Sales, modular default)' }

    # ---- Q6: generated solutions build (warnings as errors) and the partial method compiles away.
    $expectRun = @{ clean = 'api,application+domain'; 'modular-sales' = 'api,Sales'; 'modular-default' = 'api,Core' }
    $q6 = @(); $q6Seen = @()
    foreach ($case in $main) {
        $leaf = Split-Path -Leaf $case.Out.Path
        $br = Invoke-BuildAndRun $case.Out.Path
        if (-not $br.Built) { $q6 += "$leaf build failed: $($br.Errors)"; continue }
        $q6Seen += "$leaf='$($br.Output)'"
        if ($br.Output -ne $expectRun[$leaf]) { $q6 += "$leaf printed '$($br.Output)', expected '$($expectRun[$leaf])'" }
    }
    if ($q6) { Set-Result 6 'FAIL' ($q6 -join '; ') }
    else { Set-Result 6 'PASS' "all three build with -warnaserror; Api output: $($q6Seen -join ', ') (unimplemented partial calls compiled away)" }

    # ---- Q8: what the engine can enforce on options.
    $rulesName = New-Variant 'rules' {
        param($json)
        $json.symbols.module.isEnabled = '(architecture == "modular")'
        $json.symbols.module.isRequired = '(architecture == "modular")'
    }
    $modularNoModule = New-Output $rulesName 'rules-modular-nomodule' @('--architecture', 'modular')
    $cleanWithModule = New-Output $rulesName 'rules-clean-module' @('--module', 'Sales')
    $badChoice = New-Output 'spike-layouts' 'bad-choice' @('--architecture', 'hexagonal')
    $badName = New-Output 'spike-layouts' 'bad-name' @('--architecture', 'modular', '--module', 'Sales-Orders')
    $warned = $badName.ExitCode -eq 0 -and ((Get-Content -LiteralPath (Join-Path $badName.Path 'README.md') -Raw) -match 'not a valid C# identifier')
    $formsName = New-Variant 'forms' {
        param($json, $path)
        $json.symbols.module.Remove('replaces')
        $json.symbols.module.Remove('fileRename')
        $json.forms = @{
            stripNonIdentifier = @{ identifier = 'replace'; pattern = '[^A-Za-z0-9]'; replacement = '' }
            firstUpper         = @{ identifier = 'firstUpperCase' }
            title              = @{ identifier = 'titleCase' }
            moduleIdentifier   = @{ identifier = 'chain'; steps = @('stripNonIdentifier', 'firstUpper') }
            moduleTitle        = @{ identifier = 'chain'; steps = @('title', 'stripNonIdentifier') }
        }
        $json.symbols.moduleName = @{ type = 'derived'; valueSource = 'module'; valueTransform = 'moduleIdentifier'; replaces = 'MyModule'; fileRename = 'MyModule' }
        $json.symbols.moduleTitle = @{ type = 'derived'; valueSource = 'module'; valueTransform = 'moduleTitle'; replaces = 'TitlePlaceholder' }
        Set-Content -LiteralPath (Join-Path $path 'forms.txt') -Value 'title=TitlePlaceholder'
    }
    $normalised = foreach ($m in 'sales', 'sales-orders', 'SalesOrders', 'order lines') {
        $out = New-Output $formsName "forms-$($m -replace ' ', '_')" @('--architecture', 'modular', '--module', $m)
        $folder = (Get-ChildItem -LiteralPath (Join-Path $out.Path 'src/Modules') -Directory | Select-Object -First 1).Name
        $title = ((Get-Content -LiteralPath (Join-Path $out.Path 'forms.txt') -Raw).Trim() -replace '^title=', '')
        "'$m'->$folder (titleCase chain: $title)"
    }
    $q8 = @(
        "invalid choice rejected: exit $($badChoice.ExitCode)",
        "isRequired '(architecture == ""modular"")' without --module: exit $($modularNoModule.ExitCode), files created: $(if (Test-Path $modularNoModule.Path) { @(Get-Files $modularNoModule.Path).Count } else { 0 })",
        "isEnabled false but --module given (clean): exit $($cleanWithModule.ExitCode) (accepted silently)",
        "regexMatch flags 'Sales-Orders' in a region: $warned, generation exit $($badName.ExitCode) (not rejected)",
        "derived+forms normalise: $($normalised -join ', ')")
    $q8Ok = $badChoice.ExitCode -ne 0 -and $modularNoModule.ExitCode -ne 0 -and $warned
    Set-Result 8 ($(if ($q8Ok) { 'NOT SUPPORTED' } else { 'FAIL' })) ('rejecting a text value by pattern is not supported; what exists: ' + ($q8 -join '; '))

    # ---- Q9: AddValidation in a class library, observed from outside.
    $probe = Join-Path $work 'validation-probe'
    Copy-Item -LiteralPath (Join-Path $spike 'validation-probe') -Destination $probe -Recurse -Exclude 'bin', 'obj'
    $probeRun = Invoke-Dotnet @('run', '--project', (Join-Path $probe 'Probe.Runner/Probe.Runner.csproj'), '--nologo')
    $lines = $probeRun.Text -split "`r?`n" | Where-Object { $_ -match '^(none|empty|endpoint|both): resolvers' }
    $emptyLine = $lines | Where-Object { $_ -like 'empty:*' }
    $endpointLine = $lines | Where-Object { $_ -like 'endpoint:*' }
    $q9Ok = $probeRun.ExitCode -eq 0 -and $emptyLine -match 'GeneratedValidatableInfoResolver from Probe\.Empty' -and $emptyLine -match 'knowsCreateOrder=False' -and
        $endpointLine -match 'GeneratedValidatableInfoResolver from Probe\.WithEndpoint' -and $endpointLine -match 'knowsCreateOrder=True'
    Set-Result 9 ($(if ($q9Ok) { 'PASS' } else { 'FAIL' })) ($(if ($probeRun.ExitCode -eq 0) { $lines -join ' || ' } else { $probeRun.Text }))
}
finally {
    Pop-Location -ErrorAction SilentlyContinue
    if ($Keep) { Write-Host "Working directory kept: $work" }
    elseif (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}

Write-Host ''
foreach ($q in $results.Keys | Sort-Object { [int]$_ }) { Write-Host ("Q{0} {1}: {2}" -f $q, $results[$q].Status, $results[$q].Evidence) }
foreach ($note in $notes) { Write-Host "Note: $note" }

$required = 1..7 | Where-Object { -not $results.Contains("$_") -or $results["$_"].Status -ne 'PASS' }
if ($required) { Write-Host "Mechanisms not proven: $($required -join ', ')"; exit 1 }
Write-Host 'All mechanisms the plan relies on (1 to 7) work.'
exit 0
