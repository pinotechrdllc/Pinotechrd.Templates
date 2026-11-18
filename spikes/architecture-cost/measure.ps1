#requires -Version 7
<#
.SYNOPSIS
  Measures what the `modular` architecture costs against `clean` for a minimal project:
  number of projects and build times. Scaffolds both skeletons in a temporary folder.
.PARAMETER Modules
  Number of business modules in the modular skeleton.
.PARAMETER Runs
  Times each build is repeated; the median is reported.
#>
param([int]$Modules = 2, [int]$Runs = 3)

$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) ("architecture-cost-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

function New-Project([string]$Root, [string]$Name, [string]$Kind, [string[]]$References) {
    $dir = Join-Path $Root $Name
    dotnet new $Kind --name $Name --output $dir --framework net10.0 --no-restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet new $Kind $Name failed." }
    foreach ($reference in $References) {
        dotnet add (Join-Path $dir "$Name.csproj") reference (Join-Path $Root $reference "$reference.csproj") | Out-Null
    }
    # One real type per project so every project compiles something.
    $type = ($Name -replace '[^A-Za-z0-9]', '') + 'Marker'
    Set-Content -LiteralPath (Join-Path $dir 'Marker.cs') -Value "namespace $Name;`npublic sealed class $type { public int Value { get; init; } }"
    dotnet sln (Join-Path $Root 'App.slnx') add (Join-Path $dir "$Name.csproj") | Out-Null
}

function New-Solution([string]$Root) {
    New-Item -ItemType Directory -Path $Root | Out-Null
    dotnet new sln --name App --output $Root --format slnx | Out-Null
}

function Measure-Build([string]$Root, [scriptblock]$Before) {
    $times = foreach ($run in 1..$Runs) {
        & $Before
        $watch = [Diagnostics.Stopwatch]::StartNew()
        dotnet build (Join-Path $Root 'App.slnx') --nologo --verbosity quiet | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Build failed in $Root." }
        $watch.Stop()
        $watch.Elapsed.TotalSeconds
    }
    [Math]::Round(($times | Sort-Object)[[int][Math]::Floor($Runs / 2)], 1)
}

function Measure-Architecture([string]$Name, [string]$Root, [string]$DomainProject) {
    $projects = (Get-ChildItem -LiteralPath $Root -Recurse -Filter '*.csproj').Count
    $marker = Join-Path $Root $DomainProject 'Marker.cs'
    # Warm up the compiler server so the first measured build does not pay its start.
    dotnet build (Join-Path $Root 'App.slnx') --nologo --verbosity quiet | Out-Null
    $cold = Measure-Build $Root {
        Get-ChildItem -LiteralPath $Root -Recurse -Directory -Include bin, obj | Remove-Item -Recurse -Force
    }
    $noChange = Measure-Build $Root { }
    $domainChange = Measure-Build $Root { Add-Content -LiteralPath $marker -Value "// $([Guid]::NewGuid())" }
    [pscustomobject]@{
        Architecture = $Name; Projects = $projects; 'Cold build (s)' = $cold
        'No change (s)' = $noChange; 'After a domain change (s)' = $domainChange
    }
}

try {
    # clean: one set of layers.
    $clean = Join-Path $work 'clean'
    New-Solution $clean
    New-Project $clean 'App.Domain' classlib @()
    New-Project $clean 'App.Contracts' classlib @()
    New-Project $clean 'App.Resources' classlib @()
    New-Project $clean 'App.Application' classlib @('App.Domain', 'App.Contracts', 'App.Resources')
    New-Project $clean 'App.Infrastructure' classlib @('App.Application')
    New-Project $clean 'App.Api' web @('App.Application', 'App.Infrastructure')
    New-Project $clean 'App.Domain.Tests' xunit @('App.Domain')
    New-Project $clean 'App.Api.Tests' xunit @('App.Api')

    # modular: host, building blocks and five projects per module.
    $modular = Join-Path $work 'modular'
    New-Solution $modular
    New-Project $modular 'App.BuildingBlocks.Domain' classlib @()
    New-Project $modular 'App.BuildingBlocks.Application' classlib @('App.BuildingBlocks.Domain')
    New-Project $modular 'App.BuildingBlocks.Infrastructure' classlib @('App.BuildingBlocks.Application')
    $endpoints = @()
    foreach ($index in 1..$Modules) {
        $module = "App.Module$index"
        New-Project $modular "$module.Contracts" classlib @()
        New-Project $modular "$module.Domain" classlib @('App.BuildingBlocks.Domain')
        New-Project $modular "$module.Application" classlib @("$module.Domain", "$module.Contracts", 'App.BuildingBlocks.Application')
        New-Project $modular "$module.Infrastructure" classlib @("$module.Application", 'App.BuildingBlocks.Infrastructure')
        New-Project $modular "$module.Endpoints" classlib @("$module.Application")
        New-Project $modular "$module.Tests" xunit @("$module.Endpoints", "$module.Infrastructure")
        $endpoints += "$module.Endpoints", "$module.Infrastructure"
    }
    New-Project $modular 'App.Host' web $endpoints
    New-Project $modular 'App.Architecture.Tests' xunit @('App.Host')

    dotnet restore (Join-Path $clean 'App.slnx') --verbosity quiet | Out-Null
    dotnet restore (Join-Path $modular 'App.slnx') --verbosity quiet | Out-Null

    $results = @(
        Measure-Architecture 'clean' $clean 'App.Domain'
        Measure-Architecture "modular ($Modules modules)" $modular 'App.Module1.Domain'
    )
    $results | Format-Table -AutoSize | Out-String | Write-Host
    $global:LASTEXITCODE = 0
}
finally {
    dotnet build-server shutdown | Out-Null
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
