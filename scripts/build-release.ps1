<#
.SYNOPSIS
Publish Alt Henkan for release (self-contained, single file).

.PARAMETER Configuration
Build configuration. Default: Release.

.PARAMETER Runtime
Runtime identifier. Default: win-x64.

.PARAMETER Clean
Delete the publish output directory before publishing.

.PARAMETER NoUiAccess
By default app.uiaccess.manifest (uiAccess="true") is embedded so Alt Henkan can act on windows of
elevated processes; such an exe only starts when it is code-signed and installed under Program Files.
-NoUiAccess produces a plain executable that runs from anywhere without a signature.

.EXAMPLE
.\scripts\build-release.ps1
.\scripts\build-release.ps1 -Clean
.\scripts\build-release.ps1 -NoUiAccess
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$Clean,
    [switch]$NoUiAccess
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$ProjectDir = Join-Path $RepoRoot "src\AltHenkan"
$ProjectPath = Join-Path $ProjectDir "AltHenkan.csproj"
$PublishDir = Join-Path $ProjectDir "bin\$Configuration\net8.0-windows\$Runtime\publish"
$ExePath = Join-Path $PublishDir "AltHenkan.exe"

# An AltHenkan.exe running from this repository's bin folder locks the output file and makes publish
# fail. An installed AltHenkan.exe is left alone.
Get-Process -Name "AltHenkan" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith("$RepoRoot\", [System.StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object {
        Write-Host "Stopping running AltHenkan.exe from the repository: $($_.Path)"
        Stop-Process -Id $_.Id -Force
    }
Start-Sleep -Milliseconds 500

if ($Clean -and (Test-Path $PublishDir)) {
    Remove-Item -LiteralPath $PublishDir -Recurse -Force
}

# The manifest is baked into the intermediate host executables. Always regenerate them so a
# switch between uiAccess and plain builds can never reuse a host with the wrong manifest.
foreach ($hostExe in @("apphost.exe", "singlefilehost.exe")) {
    $hostPath = Join-Path $ProjectDir "obj\$Configuration\net8.0-windows\$Runtime\$hostExe"
    if (Test-Path $hostPath) {
        Remove-Item -LiteralPath $hostPath -Force
    }
}

dotnet restore $ProjectPath
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed ($LASTEXITCODE)." }

# Self-contained single-file publish so the app runs on PCs without the .NET runtime installed.
$publishArgs = @(
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-o", $PublishDir
)
if ($NoUiAccess) {
    Write-Host "uiAccess manifest disabled (-NoUiAccess): plain executable."
} else {
    Write-Host "uiAccess manifest enabled (the exe must be signed and installed under Program Files to run)."
    $publishArgs += "-p:UiAccess=true"
}

dotnet publish $ProjectPath @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

if (-not (Test-Path $ExePath)) {
    throw "Release executable was not created: $ExePath"
}

Write-Host "Release build created:"
Write-Host $ExePath
