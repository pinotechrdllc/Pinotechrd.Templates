<#
.SYNOPSIS
  Runs the security-schema-per-tenant spike: Q1-Q6 against a throwaway PostgreSQL 17.7 container.
.DESCRIPTION
  Needs Docker and the .NET 10 SDK. Testcontainers starts and removes the container. Q5 also builds the
  OpenApiProbe project twice. Exit code 0 when every gating question passes, 1 otherwise.
#>
$ErrorActionPreference = 'Stop'

docker info --format '{{.ServerVersion}}' *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Error 'Docker is not running; the spike needs it for PostgreSQL.'
    exit 2
}

$project = Join-Path $PSScriptRoot 'Spike/Pinotechrd.Spikes.SecuritySchemaPerTenant.csproj'
dotnet run --project $project -c Release
exit $LASTEXITCODE
