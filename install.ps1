# Install or update dataverse-mcp dotnet global tool
$ErrorActionPreference = "Stop"

$installed = dotnet tool list -g | Select-String "dataverse-mcp"
if ($installed) {
    Write-Host "Updating dataverse-mcp..." -ForegroundColor Cyan
    dotnet tool update -g dataverse-mcp
} else {
    Write-Host "Installing dataverse-mcp..." -ForegroundColor Cyan
    dotnet tool install -g dataverse-mcp
}

Write-Host "Running setup..." -ForegroundColor Cyan
dataverse-mcp setup
