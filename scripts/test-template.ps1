#requires -Version 7
<#
.SYNOPSIS
  Packs the template package, installs it in an isolated hive, generates a project for
  each case and verifies the result.
.DESCRIPTION
  A case passes when the project is generated, the placeholder name is fully replaced and,
  if the project has code, it builds without warnings and its tests pass.
.PARAMETER Keep
  Keep the working directory after the run, for inspection.
#>
param([switch]$Keep)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) ("pinotechrd-templates-" + [Guid]::NewGuid().ToString('N'))
$packages = Join-Path $work 'packages'
$hive = Join-Path $work 'hive'
$placeholder = 'MyApp'
$projectName = 'Acme.Demo'

# Each case is one combination of template options that must generate a working project.
$cases = @(
    @{ Name = 'clean-standalone'; Options = @() },
    @{ Name = 'modular-standalone'; Options = @('--architecture', 'modular', '--modules', 'Sales,Billing') }
)

function Invoke-Checked([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
}

function Assert-NameReplaced([string]$Root) {
    $entries = Get-ChildItem -LiteralPath $Root -Recurse -Force
    $named = $entries | Where-Object { $_.Name -like "*$placeholder*" }
    if ($named) { throw "Placeholder '$placeholder' left in names: $($named.FullName -join ', ')" }

    $withText = $entries | Where-Object { -not $_.PSIsContainer } |
        Where-Object { Select-String -LiteralPath $_.FullName -SimpleMatch $placeholder -Quiet }
    if ($withText) { throw "Placeholder '$placeholder' left in content: $($withText.FullName -join ', ')" }
}

try {
    New-Item -ItemType Directory -Path $work | Out-Null

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

        # The placeholder project has no code yet; build and test only once it does.
        if (Get-ChildItem -LiteralPath $output -Recurse -Filter '*.csproj') {
            Invoke-Checked "Building $($case.Name)" { dotnet build $solution --configuration Release --nologo -warnaserror }
            Invoke-Checked "Testing $($case.Name)" { dotnet test $solution --configuration Release --no-build --nologo }
        }
    }

    Write-Host '== Invalid option is rejected'
    dotnet new pinotechrd-app --name $projectName --output (Join-Path $work 'invalid') --profile unknown --debug:custom-hive $hive *> $null
    if ($LASTEXITCODE -eq 0) { throw 'An invalid --profile value was accepted.' }

    Write-Host "All $($cases.Count) cases passed."
}
finally {
    if ($Keep) { Write-Host "Working directory kept: $work" }
    elseif (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
