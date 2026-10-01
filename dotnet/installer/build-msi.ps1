# Builds the per-user MSI of teamsrec-capture: a self-contained single-file publish, then the WiX project.
# Output: dotnet\installer\bin\x64\Release\en-US\teamsrec-capture-<version>-x64.msi (the path is printed).
# The version is <Version> from the app's .csproj; the GitHub workflow runs the same script.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\TeamsRec.Capture\TeamsRec.Capture.csproj"
$publish = Join-Path $root "artifacts\publish"
$version = (dotnet msbuild $proj -getProperty:Version).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "unexpected version '$version'" }

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish $proj -c $Configuration -r win-x64 --self-contained true -o $publish `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

dotnet build (Join-Path $PSScriptRoot "TeamsRec.Capture.Installer.wixproj") -c $Configuration `
    -p:ProductVersion=$version -p:PublishDir=$publish
if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }

# localized builds (Strings.wxl) land in a culture subfolder
$msi = Get-ChildItem (Join-Path $PSScriptRoot "bin\x64\$Configuration") -Recurse -Filter "teamsrec-capture-$version-x64.msi" |
    Select-Object -First 1
if (-not $msi) { throw "MSI teamsrec-capture-$version-x64.msi not found" }
Write-Output $msi.FullName
