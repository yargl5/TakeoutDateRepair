$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repoRoot 'artifacts'
$exe = Join-Path $output 'TakeoutDateRepair.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Run scripts\build.ps1 first.' }
$runRoot = Join-Path $repoRoot ('test-results\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $runRoot 'TakeoutDateRepair.exe')
$archive = Join-Path $output 'exiftool-test.zip'
$extract = Join-Path $runRoot 'tools'
$assembly = [Reflection.Assembly]::LoadFile($exe)
$resource = $assembly.GetManifestResourceStream('exiftool.zip')
try {
    $stream = [IO.File]::Create($archive)
    try { $resource.CopyTo($stream) } finally { $stream.Dispose() }
} finally { $resource.Dispose() }
Expand-Archive -LiteralPath $archive -DestinationPath $extract
$tool = Join-Path $extract 'ExifTool.exe'
foreach ($test in @('ValidationTests','CleanupTests','AdditionalTests')) {
    $testExe = Join-Path $runRoot ($test + '.exe')
    & $compiler /nologo /target:exe /codepage:65001 "/reference:$exe" /reference:System.Drawing.dll /reference:System.Web.Extensions.dll "/out:$testExe" (Join-Path $repoRoot ('tests\' + $test + '.cs'))
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $test" }
    & $testExe (Join-Path $runRoot ($test + '-data')) $tool
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $test" }
}
Write-Output 'All synthetic test suites passed. No personal photo library was used.'
