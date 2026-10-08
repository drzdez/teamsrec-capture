# Stages the rest of the teamsrec suite for the MSI (build-msi.ps1 -Suite): next to the recording app go
#   teamsrec-review.exe   the window of the review page (Tauri, built from teamsrec-transcribe\desktop),
#   uv.exe                a pinned uv, which builds the Python environment on the window's first start,
#   transcribe\           the teamsrec-transcribe project with its exact lock (uv.lock) and suite.json (the version
#                         and the commit; a new one makes the window refresh the environment after an update),
#   ffmpeg\               ffmpeg + ffprobe (BtbN's GPL shared build, pinned): the window video, reading recordings.
# Output: dotnet\artifacts\suite (the path is printed). PyTorch & co. are not in the MSI: several GB, downloaded by uv.
param(
    [Parameter(Mandatory)][string]$Transcribe,  # a checkout of teamsrec-transcribe
    [Parameter(Mandatory)][string]$Version,     # the suite version (the MSI's)
    [string]$ReviewExe = ""                     # a built teamsrec-review.exe; empty = build it here (Rust + Node)
)
$ErrorActionPreference = "Stop"

# uv pinned by version and SHA-256 (GitHub's digest of the release asset)
$UvVersion = "0.12.9"
$UvSha256 = "ddbfcee1ac615a0499f6aa97b5ec8ebdf3ee4a7714a48055ec2ba0030e3cf810"
# ffmpeg 9.0 (libx264 for the window video needs the GPL build; shared = one set of DLLs for ffmpeg and ffprobe)
$FfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-06-13-06/ffmpeg-n9.0.2-22-g46d8f462ee-win64-gpl-shared-9.0.zip"
$FfmpegSha256 = "9d971d67fea48100b623e65d3ffba480215244931492b23a41374cec7511b1ce"

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts\suite"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force -Path (Join-Path $out "transcribe") | Out-Null
$Transcribe = (Resolve-Path $Transcribe).Path

# the window
if (-not $ReviewExe) {
    Push-Location (Join-Path $Transcribe "desktop")
    try {
        npm ci
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "tauri build failed" }
    } finally { Pop-Location }
    $ReviewExe = Join-Path $Transcribe "desktop\src-tauri\target\release\teamsrec-review.exe"
}
Copy-Item $ReviewExe (Join-Path $out "teamsrec-review.exe")

# uv
$zip = Join-Path $env:TEMP "uv-$UvVersion.zip"
Invoke-WebRequest "https://github.com/astral-sh/uv/releases/download/$UvVersion/uv-x86_64-pc-windows-msvc.zip" -OutFile $zip
$got = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($got -ne $UvSha256) { throw "uv download does not match its SHA-256 ($got)" }
$unz = Join-Path $env:TEMP "uv-$UvVersion"
if (Test-Path $unz) { Remove-Item -Recurse -Force $unz }
Expand-Archive $zip -DestinationPath $unz
Copy-Item (Get-ChildItem $unz -Recurse -Filter uv.exe | Select-Object -First 1).FullName (Join-Path $out "uv.exe")

# ffmpeg: the two programs and their DLLs (not ffplay), with the licence
$fz = Join-Path $env:TEMP "teamsrec-ffmpeg.zip"
Invoke-WebRequest $FfmpegUrl -OutFile $fz
$got = (Get-FileHash $fz -Algorithm SHA256).Hash.ToLowerInvariant()
if ($got -ne $FfmpegSha256) { throw "ffmpeg download does not match its SHA-256 ($got)" }
$fx = Join-Path $env:TEMP "teamsrec-ffmpeg"
if (Test-Path $fx) { Remove-Item -Recurse -Force $fx }
Expand-Archive $fz -DestinationPath $fx
$fbin = (Get-ChildItem $fx -Recurse -Filter ffmpeg.exe | Select-Object -First 1).Directory
$fdst = Join-Path $out "ffmpeg"
New-Item -ItemType Directory -Force -Path $fdst | Out-Null
Copy-Item (Join-Path $fbin "ffmpeg.exe"), (Join-Path $fbin "ffprobe.exe") $fdst
Get-ChildItem $fbin -Filter *.dll | Where-Object { $_.Name -notlike "SDL*" } | Copy-Item -Destination $fdst
Copy-Item (Get-ChildItem $fx -Recurse -Filter "LICENSE*" | Select-Object -First 1).FullName (Join-Path $fdst "LICENSE.txt")
[IO.File]::WriteAllText((Join-Path $fdst "SOURCE.txt"),
    "ffmpeg (GPL), a separate program bundled with teamsrec. Build and sources: $FfmpegUrl`r`nhttps://github.com/BtbN/FFmpeg-Builds`r`n")

# the project: what `uv sync` needs to build and install it, nothing else (no tests, lab, desktop sources)
$dst = Join-Path $out "transcribe"
foreach ($f in "pyproject.toml", "uv.lock", "README.md") { Copy-Item (Join-Path $Transcribe $f) $dst }
New-Item -ItemType Directory -Force -Path (Join-Path $dst "docs") | Out-Null
foreach ($f in "user-guide.md", "install.md", "privacy.md") { Copy-Item (Join-Path $Transcribe "docs\$f") (Join-Path $dst "docs") }
Copy-Item -Recurse (Join-Path $Transcribe "src") $dst
Get-ChildItem $dst -Recurse -Directory -Filter "__pycache__" | Remove-Item -Recurse -Force
$commit = (git -C $Transcribe rev-parse --short HEAD).Trim()
$json = @{ version = $Version; transcribe_commit = $commit } | ConvertTo-Json -Compress
[IO.File]::WriteAllText((Join-Path $dst "suite.json"), $json)  # no BOM, also in Windows PowerShell 5.1

Write-Output $out
