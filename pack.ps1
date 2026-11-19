[CmdletBinding()]
param(
    [string]$GamePath = $env:ADOFAI_GAME_PATH,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($GamePath)) {
    throw 'Set ADOFAI_GAME_PATH or pass -GamePath to the game installation folder.'
}

$GamePath = (Resolve-Path -LiteralPath $GamePath).Path
dotnet build (Join-Path $PSScriptRoot 'Angle.csproj') -c Release "-p:AdofaiGamePath=$GamePath"
if ($LASTEXITCODE -ne 0) {
    throw 'The Angle build failed.'
}

$infoPath = Join-Path $PSScriptRoot 'Info.json'
$info = Get-Content -LiteralPath $infoPath -Raw | ConvertFrom-Json
if ($info.Id -ne 'Angle' -or $info.AssemblyName -ne 'Angle.dll' -or $info.Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw 'Check the mod ID, assembly name and version in Info.json.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$destination = New-Item -ItemType Directory -Force -Path $OutputDirectory
$zipPath = Join-Path $destination.FullName "Angle-$($info.Version).zip"
$entries = [ordered]@{
    'Angle/Angle.dll' = Join-Path $PSScriptRoot 'bin/Release/Angle.dll'
    'Angle/Info.json' = $infoPath
    'Angle/AngleClipboardIcon.png' = Join-Path $PSScriptRoot 'AngleClipboardIcon.png'
    'Angle/LICENSE' = Join-Path $PSScriptRoot 'LICENSE'
}

$stream = [System.IO.File]::Create($zipPath)
try {
    $zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($entry in $entries.GetEnumerator()) {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $entry.Value, $entry.Key, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $zip.Dispose()
    }
}
finally {
    $stream.Dispose()
}
"Created $zipPath"
