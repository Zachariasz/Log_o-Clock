[CmdletBinding()]
param(
    [string] $AppVersion = "1.142.3",
    [string] $CertificatePath,
    [string] $CertificatePassword,
    [string] $TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $root "work\dotnet10\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { "dotnet" }
$outputRoot = Join-Path $root "outputs"
$publishDirectory = Join-Path $outputRoot "LogOClock-$AppVersion-win-x64"
$installerOutput = Join-Path $outputRoot "installer"
$project = Join-Path $root "src\ProjectTimeTracker.Windows\ProjectTimeTracker.Windows.csproj"
$installerProject = Join-Path $root "installer\ProjectTimeTracker.Installer.wixproj"

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
if (-not $env:DOTNET_CLI_HOME) {
    $env:DOTNET_CLI_HOME = Join-Path $root "work"
}
if (-not $env:NUGET_PACKAGES) {
    $env:NUGET_PACKAGES = Join-Path $root "work\nuget"
}

New-Item -ItemType Directory -Force -Path $publishDirectory, $installerOutput | Out-Null

& $dotnet restore $project `
    --runtime win-x64 `
    --ignore-failed-sources `
    -p:NuGetAudit=false `
    -p:BuildInParallel=false `
    -p:UseSharedCompilation=false `
    -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw "Runtime-pack restore failed." }

& $dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $publishDirectory `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded `
    -p:DebugSymbols=false `
    -p:BuildInParallel=false `
    -p:UseSharedCompilation=false `
    -nodeReuse:false `
    -maxcpucount:1
if ($LASTEXITCODE -ne 0) { throw "Application publish failed." }

& $dotnet build $installerProject `
    --configuration Release `
    -p:NuGetAudit=false `
    -p:AppPublishDir=$publishDirectory `
    -p:ProductVersion=$AppVersion `
    -p:OutputPath=$installerOutput `
    -p:SuppressValidation=true `
    -p:BuildInParallel=false `
    -nodeReuse:false `
    -maxcpucount:1
if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }

$installer = Get-Item -LiteralPath (Join-Path $installerOutput "LogOClock-Setup-$AppVersion.msi") -ErrorAction SilentlyContinue
if (-not $installer) { throw "The installer build completed without producing an MSI." }

if ($CertificatePath) {
    $signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\signtool\.exe$" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $signTool) { throw "signtool.exe was not found in the Windows SDK." }

    & $signTool.FullName sign /fd SHA256 /f $CertificatePath /p $CertificatePassword /tr $TimestampUrl /td SHA256 $installer.FullName
    if ($LASTEXITCODE -ne 0) { throw "Installer signing failed." }
}

Write-Host "Application: $publishDirectory"
Write-Host "Installer:   $($installer.FullName)"
if (-not $CertificatePath) {
    Write-Warning "The MSI is unsigned. Pass -CertificatePath and -CertificatePassword for a production-signed installer."
}
