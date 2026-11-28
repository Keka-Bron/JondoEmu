param(
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $Configuration = 'Release',
    [string] $BundledModsDirectory,
    [switch] $SkipArchives
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$output = New-Item -ItemType Directory -Force $OutDir
$stage = [IO.Directory]::CreateTempSubdirectory('jondo-server-mods-')

try {
    $modules = [ordered]@{
        Headhunters = 'headhunters'
        Heroes = 'heroes'
    }
    foreach ($module in $modules.Keys) {
        $id = $modules[$module]
        $project = Join-Path $root "$module.Server/$module.Server.csproj"
        $package = Join-Path $stage.FullName $id
        dotnet build $project --configuration $Configuration --nologo -m:1 -nr:false -p:StageServerMod=true "-p:${module}PackageDirectory=$package"
        if ($LASTEXITCODE -ne 0) { throw "Building $id failed." }

        if ($BundledModsDirectory -and $id -in 'headhunters', 'heroes') {
            Copy-Item -LiteralPath $package -Destination (Join-Path $BundledModsDirectory $id) -Recurse
        }
        if (-not $SkipArchives) {
            Compress-Archive -LiteralPath $package -DestinationPath (Join-Path $output.FullName "$id-server-mod.zip") -Force
        }
    }
}
finally {
    # This is the absolute path returned when creating this run's temporary directory.
    $stage.Delete($true)
}
