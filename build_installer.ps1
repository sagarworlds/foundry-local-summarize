param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained = $false
)

$ErrorActionPreference = "Stop"

Write-Host "Publishing the application..."
$publishArgs = @("publish", "src\FoundrySummarizer.Wpf\FoundrySummarizer.Wpf.csproj", "-c", $Configuration, "-r", $Runtime)

if ($SelfContained) {
    $publishArgs += "--self-contained"
    $publishArgs += "true"
} else {
    $publishArgs += "--self-contained"
    $publishArgs += "false"
}

& dotnet $publishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed."
    exit $LASTEXITCODE
}

$isccPath = "C:\Users\SPatil\AppData\Local\Programs\Inno Setup 6\ISCC.exe"

if (Test-Path $isccPath) {
    Write-Host "Compiling the Inno Setup script..."
    & $isccPath "installer\setup.iss"
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Installer created successfully in installer\Output" -ForegroundColor Green
    } else {
        Write-Error "Inno Setup compilation failed."
        exit $LASTEXITCODE
    }
} else {
    Write-Host "Inno Setup compiler (ISCC.exe) not found at $isccPath." -ForegroundColor Yellow
    Write-Host "Please install Inno Setup 6 or manually compile installer\setup.iss." -ForegroundColor Yellow
}
