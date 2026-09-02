# Install or update the Dataverse Modelling MCP setup tool (dotnet global tool)
$ErrorActionPreference = "Stop"

$packageId = "Dataverse.ModellingMcp.Setup"
$command   = "dataverse-modelling-mcp"

$installed = dotnet tool list -g | Select-String $packageId
if ($installed) {
    Write-Host "Updating $packageId..." -ForegroundColor Cyan
    dotnet tool update -g $packageId
} else {
    Write-Host "Installing $packageId..." -ForegroundColor Cyan
    dotnet tool install -g $packageId
}

Write-Host "Running setup..." -ForegroundColor Cyan
& $command
