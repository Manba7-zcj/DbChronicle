param(
    [string]$Configuration = "Release",
    [switch]$StandardPackage
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "XmlValueIntelliSense.csproj"
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"

if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found"
}

$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
$vsInstall = & $vswhere -latest -products * -property installationPath | Select-Object -First 1
$vsixUtil = Join-Path $vsInstall "VSSDK\VisualStudioIntegration\Tools\Bin\VsixUtil.exe"

if (-not $msbuild) {
    throw "MSBuild not found"
}

$outputDir = Join-Path $root ("bin\" + $Configuration)
$vsix = Join-Path $outputDir "XmlValueIntelliSense.vsix"

if (-not $StandardPackage) {
    if (-not (Test-Path $vsixUtil)) {
        throw "VsixUtil.exe not found: $vsixUtil"
    }

    & $msbuild $project /t:Restore,Build /p:Configuration=$Configuration /p:CreateVsixContainer=false /v:m /nologo
    if ($LASTEXITCODE -ne 0) {
        throw "build failed"
    }

    $assembly = Join-Path $outputDir "XmlValueIntelliSense.dll"
    $objDir = Join-Path $root ("obj\" + $Configuration)
    $filesJson = Join-Path $objDir "files.pack.json"

    New-Item -ItemType Directory -Force $objDir | Out-Null
    $payload = @{
        files = @(
            @{
                culture = ""
                installRoot = ""
                ngen = $null
                path = $assembly
                targetPath = ""
                vsixSubPath = ""
            }
        )
    } | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($filesJson, $payload, [System.Text.UTF8Encoding]::new($false))

    if (Test-Path $vsix) {
        Remove-Item -LiteralPath $vsix -Force
    }

    & $vsixUtil package -outputPath $vsix -sourceManifest (Join-Path $root "source.extension.vsixmanifest") -files $filesJson -is64BitBuild -compressionLevel Normal
    if ($LASTEXITCODE -ne 0) {
        throw "vsix packaging failed"
    }

    $repackDir = Join-Path ([System.IO.Path]::GetTempPath()) ("XmlValueIntelliSense-pack-" + [Guid]::NewGuid().ToString("N"))
    $stageDir = Join-Path $repackDir "stage"
    $packageZip = Join-Path $repackDir "package.zip"

    New-Item -ItemType Directory -Force (Join-Path $stageDir "_rels") | Out-Null

    Copy-Item -LiteralPath $vsix -Destination $packageZip -Force
    Expand-Archive -LiteralPath $packageZip -DestinationPath $stageDir -Force
    Remove-Item -LiteralPath $packageZip -Force

    $contentTypes = Join-Path $stageDir "[Content_Types].xml"
    [System.IO.File]::WriteAllText($contentTypes, '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" /><Default Extension="vsixmanifest" ContentType="text/xml" /><Default Extension="dll" ContentType="application/octet-stream" /><Default Extension="json" ContentType="application/json" /></Types>', [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $stageDir "_rels\.rels"), '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships" />', [System.Text.UTF8Encoding]::new($false))

    $tar = Join-Path $env:SystemRoot "System32\tar.exe"
    if (-not (Test-Path $tar)) {
        throw "tar.exe not found: $tar"
    }

    $repackZip = Join-Path $repackDir "repack.zip"
    & $tar --format=zip -cf $repackZip -C $stageDir "[Content_Types].xml" "_rels/.rels" "extension.vsixmanifest" "manifest.json" "catalog.json" "XmlValueIntelliSense.dll"
    if ($LASTEXITCODE -ne 0) {
        throw "vsix repack failed"
    }

    Copy-Item -LiteralPath $repackZip -Destination $vsix -Force

    try {
        Remove-Item -LiteralPath $repackDir -Recurse -Force -ErrorAction Stop
    } catch {
    }
} else {
    & $msbuild $project /t:Restore,Build /p:Configuration=$Configuration /p:CreateVsixContainer=true /v:m /nologo
    if ($LASTEXITCODE -ne 0) {
        throw "build failed"
    }

    if (-not (Test-Path $vsix)) {
        throw "VSIX was not produced: $vsix"
    }
}

Copy-Item -LiteralPath (Join-Path $root "install.cmd") -Destination $outputDir -Force

Write-Host ("VSIX: " + $vsix)
