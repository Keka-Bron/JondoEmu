param(
    [Parameter(Mandatory)] [string] $DofusClientDir,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$client = (Resolve-Path -LiteralPath $DofusClientDir).Path
$interop = Join-Path $client 'MelonLoader/Il2CppAssemblies'
if (-not (Test-Path -LiteralPath (Join-Path $client 'MelonLoader/net6/MelonLoader.dll') -PathType Leaf) -or
    -not (Test-Path -LiteralPath $interop -PathType Container) -or
    -not (Get-ChildItem -LiteralPath $interop -Filter '*.dll' -File)) {
    throw "Expected MelonLoader and generated Il2Cpp assemblies in $client."
}

foreach ($mod in 'JondoFix', 'JondoModLoader', 'Heroes', 'Headhunters') {
    $project = Join-Path $root "$mod/$mod.csproj"
    dotnet build $project --configuration $Configuration --nologo `
        "-p:DofusClientDir=$client" -p:SkipModDeploy=true
    if ($LASTEXITCODE -ne 0) { throw "Building $mod failed." }
}
