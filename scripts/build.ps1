param([string]$ExifToolZip)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $repoRoot '.cache'
$output = Join-Path $repoRoot 'artifacts'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$expectedHash = '577257DD22BAEBE77157D905792D6ED2C5916CD03AA627F40B2175DB12110AC6'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found. Build on Windows with .NET Framework 4.8.' }
New-Item -ItemType Directory -Path $cache,$output -Force | Out-Null
if (!$ExifToolZip) {
    $ExifToolZip = Join-Path $cache 'exiftool-13.59_64.zip'
    if (!(Test-Path -LiteralPath $ExifToolZip)) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri 'https://oliverbetz.de/cms/files/Artikel/ExifTool-for-Windows/exiftool-13.59_64.zip' -OutFile $ExifToolZip -UseBasicParsing
    }
}
$ExifToolZip = (Resolve-Path -LiteralPath $ExifToolZip).Path
if ((Get-FileHash -LiteralPath $ExifToolZip -Algorithm SHA256).Hash -ne $expectedHash) { throw 'ExifTool archive SHA-256 mismatch. Expected the official unmodified 13.59 x64 archive.' }
$exe = Join-Path $output 'TakeoutDateRepair.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll `
    "/resource:$ExifToolZip,exiftool.zip" "/out:$exe" (Join-Path $repoRoot 'src\TakeoutDateRepair.cs')
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Get-FileHash -LiteralPath $exe -Algorithm SHA256
