param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\NetBypass.App\NetBypass.App.csproj'
$publishDirectory = Join-Path $root 'artifacts\publish\win-x64'
$releaseDirectory = Join-Path $root 'artifacts\release'
$installerScript = Join-Path $root 'installer\NetBypass.iss'
$innoCompiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'

if (-not (Test-Path -LiteralPath $innoCompiler)) {
    throw 'Inno Setup 6 не найден. Установите его с https://jrsoftware.org/isinfo.php'
}

Remove-Item -LiteralPath $publishDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null

dotnet test (Join-Path $root 'NetBypass.sln') -c Release
if ($LASTEXITCODE -ne 0) {
    throw 'Тесты завершились с ошибкой.'
}

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw 'Публикация приложения завершилась с ошибкой.'
}

$portableArchive = Join-Path $releaseDirectory "NetBypass-v$Version-win-x64-portable.zip"
Remove-Item -LiteralPath $portableArchive -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $portableArchive -CompressionLevel Optimal

& $innoCompiler "/DAppVersion=$Version" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw 'Сборка установщика завершилась с ошибкой.'
}

$checksumPath = Join-Path $releaseDirectory 'SHA256SUMS.txt'
$releaseFiles = Get-ChildItem -LiteralPath $releaseDirectory -File |
    Where-Object Name -ne 'SHA256SUMS.txt'
$checksums = foreach ($file in $releaseFiles) {
    $hash = Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $($file.Name)"
}
Set-Content -LiteralPath $checksumPath -Value $checksums -Encoding utf8NoBOM

Get-ChildItem -LiteralPath $releaseDirectory -File |
    Select-Object Name, @{Name = 'SizeMB'; Expression = { [math]::Round($_.Length / 1MB, 2) } }
