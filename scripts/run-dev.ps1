$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\Northpass\Northpass.csproj'
dotnet run --project $project
