param(
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$output = New-Item -ItemType Directory -Force $OutDir
$stage = [IO.Directory]::CreateTempSubdirectory('jondo-server-mods-')

try {
    $modules = [ordered]@{
        Cazacabezas = 'cazacabezas'
        Heroes = 'heroes'
    }
    if (Test-Path -LiteralPath (Join-Path $root 'HeroProgressionSync.Server/HeroProgressionSync.Server.csproj') -PathType Leaf) {
        $modules['HeroProgressionSync'] = 'hero-progression-sync'
    }
    foreach ($module in $modules.Keys) {
        $id = $modules[$module]
        $project = Join-Path $root "$module.Server/$module.Server.csproj"
        $package = Join-Path $stage.FullName $id
        dotnet build $project --configuration $Configuration --nologo -m:1 -nr:false -p:StageServerMod=true "-p:${module}PackageDirectory=$package"
        if ($LASTEXITCODE -ne 0) { throw "Building $id failed." }

        Compress-Archive -LiteralPath $package -DestinationPath (Join-Path $output.FullName "$id-server-mod.zip") -Force
    }
}
finally {
    # This is the absolute path returned when creating this run's temporary directory.
    $stage.Delete($true)
}
