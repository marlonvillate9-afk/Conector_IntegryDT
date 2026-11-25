$ErrorActionPreference='Stop'
$repo = Split-Path $PSScriptRoot -Parent
$envFile = Join-Path $repo '.env'
if (-not (Test-Path $envFile)) { throw "No existe $envFile" }
Get-Content $envFile | ForEach-Object { if($_ -match '^([^#][^=]*)=(.*)$'){ [Environment]::SetEnvironmentVariable($matches[1],$matches[2],'Process') } }
$env:INTEGRY_CLIENT_VERSION='0.3.0'
dotnet run --project (Join-Path $repo 'src\IntegryDT.Client\IntegryDT.Client.csproj')
