param(
    [string] $Configuration = 'Release',
    [string] $ArtifactsPath
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
foreach ($module in Get-ChildItem -LiteralPath $root -Directory -Filter '*.Server') {
    $project = Join-Path $module.FullName "Tests/$($module.Name).Tests.csproj"
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { continue }

    $arguments = @('test', $project, '--configuration', $Configuration, '--nologo',
                   '--logger', "trx;LogFileName=$($module.Name).trx", '--results-directory',
                   (Join-Path $root 'TestResults'))
    if ($ArtifactsPath) { $arguments += @('--artifacts-path', $ArtifactsPath) }
    dotnet @arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
