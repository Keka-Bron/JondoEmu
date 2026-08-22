param(
    [string]$Root = $PSScriptRoot,
    [string]$InputJson = "",
    [string]$OutputJson = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($InputJson)) {
    $InputJson = Join-Path $Root "datos\interactive_skills_Giny_Table.json"
}
if ([string]::IsNullOrWhiteSpace($OutputJson)) {
    $OutputJson = Join-Path $Root "datos\interactive_teleports_giny_2.68.json"
}

$elementsPath = Join-Path $Root "datos\interactive_elements.json"
$housesPath = Join-Path $Root "datos\casas_mundo_3.6.10.10.json"
foreach ($path in @($InputJson, $elementsPath, $housesPath)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Fichier requis absent : $path" }
}

$dump = Get-Content -LiteralPath $InputJson -Raw | ConvertFrom-Json
$table = $dump | Where-Object { $_.type -eq "table" -and $_.name -eq "interactive_skills" } |
    Select-Object -First 1
if ($null -eq $table) { throw "Table interactive_skills absente du dump Giny." }

$elements = Get-Content -LiteralPath $elementsPath -Raw | ConvertFrom-Json -AsHashtable
$houses = Get-Content -LiteralPath $housesPath -Raw | ConvertFrom-Json
$houseKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($door in $houses.puertas) {
    [void]$houseKeys.Add("$($door.mapa):$($door.elemento)")
}
foreach ($exit in $houses.salidas.PSObject.Properties) {
    [void]$houseKeys.Add("$($exit.Name):$($exit.Value.elemento)")
}

$teleports = @($table.data | Where-Object ActionIdentifier -eq "Teleport")
$candidates = [System.Collections.Generic.List[object]]::new()
$housesExcluded = 0
$missingElements = 0

foreach ($row in $teleports) {
    $sourceMapId = 0L
    $elementId = 0
    $destinationMapId = 0L
    $destinationCellId = 0
    $destinationText = ([string]$row.Param1 -replace '\r|\n', '').Trim()
    $cellText = ([string]$row.Param2 -replace '\r|\n', '').Trim()
    if (-not [long]::TryParse([string]$row.MapId, [ref]$sourceMapId) -or
        -not [int]::TryParse([string]$row.Identifier, [ref]$elementId) -or
        -not [long]::TryParse($destinationText, [ref]$destinationMapId) -or
        -not [int]::TryParse($cellText, [ref]$destinationCellId)) {
        continue
    }

    $key = "$sourceMapId`:$elementId"
    if ($houseKeys.Contains($key)) {
        $housesExcluded++
        continue
    }

    $element = @($elements[[string]$sourceMapId] |
        Where-Object { [int]$_.e -eq $elementId } | Select-Object -First 1)
    if ($element.Count -eq 0) {
        $missingElements++
        continue
    }

    $candidates.Add([pscustomobject][ordered]@{
        sourceMapId = $sourceMapId
        elementId = $elementId
        sourceCellId = [int]$element[0].c
        gfxId = [int]$element[0].g
        interactiveType = 0
        skillId = 114
        destinationMapId = $destinationMapId
        destinationCellId = $destinationCellId
    })
}

$routes = [System.Collections.Generic.List[object]]::new()
$identicalDuplicates = 0
$ambiguousKeys = 0
foreach ($source in ($candidates | Group-Object sourceMapId, elementId)) {
    $unique = @($source.Group | Sort-Object destinationMapId, destinationCellId -Unique)
    $identicalDuplicates += $source.Count - $unique.Count
    $ambiguous = $unique.Count -gt 1
    if ($ambiguous) { $ambiguousKeys++ }

    foreach ($route in $unique) {
        $routes.Add([pscustomobject][ordered]@{
            sourceMapId = $route.sourceMapId
            elementId = $route.elementId
            sourceCellId = $route.sourceCellId
            gfxId = $route.gfxId
            interactiveType = $route.interactiveType
            skillId = $route.skillId
            destinationMapId = $route.destinationMapId
            destinationCellId = $route.destinationCellId
            sourceVersion = "Giny-2.68"
            confidence = if ($ambiguous) { "ambiguous" } else { "exact-element-match" }
            enabled = -not $ambiguous
        })
    }
}

$orderedRoutes = @($routes | Sort-Object sourceMapId, elementId, destinationMapId, destinationCellId)
$document = [pscustomobject][ordered]@{
    schemaVersion = 1
    generatedFor = "Dofus-3.6.10.10"
    source = "Giny 2.68 interactive_skills"
    teleportRows = $teleports.Count
    housesExcluded = $housesExcluded
    missingCurrentElements = $missingElements
    identicalDuplicatesRemoved = $identicalDuplicates
    ambiguousSourceKeys = $ambiguousKeys
    enabledRoutes = @($orderedRoutes | Where-Object enabled).Count
    routes = $orderedRoutes
}

$json = $document | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($OutputJson, $json + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))
Write-Host "Routes écrites : $($orderedRoutes.Count), actives : $($document.enabledRoutes), maisons exclues : $housesExcluded, ambiguës : $ambiguousKeys."
