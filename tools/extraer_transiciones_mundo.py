# -*- coding: utf-8 -*-
"""Extrae los interactivos vivos y el grafo de transiciones del cliente Dofus 3.

Uso normal::

    python tools/extraer_transiciones_mundo.py

Se pueden indicar otra instalación y otros destinos con ``--client``,
``--elements-output`` y ``--transitions-output``.  El resultado no contiene una
fecha de generación y se ordena siempre por identificadores, por lo que dos
ejecuciones sobre los mismos bundles producen exactamente los mismos JSON.

Hay dos espacios de identificadores que NO se deben mezclar:

* ``pathType == 32`` significa Interactive en el grafo de pathfinding.
* el tipo enviado en ``jss.f11.f6`` pertenece a InteractivesDataRoot y el grafo
  no lo contiene.

Asimismo, ``sourceCellId`` es la casilla de la que se toma la transición. El
cliente no guarda una casilla de aparición. ``reciprocalSourceCellIds`` es sólo
la lista derivada de casillas de salida de la arista inversa exacta; no se
presenta como una casilla de destino autoritativa.
"""

from __future__ import annotations

import argparse
import collections
import hashlib
import json
import os
import re
import sys
import warnings
from pathlib import Path
from typing import Any, Iterable

import UnityPy


UNITY_VERSION = "6000.3.16f1"
EXPECTED_CLIENT_VERSION = "3.6.10.10"
MAP_BUNDLE_RE = re.compile(r"mapdata_assets_world_(\d+)\.bundle$")
MAP_NAME_RE = re.compile(r"map_(\d+)$")
VISUAL_CLASSES = {
    "ClientInteractiveElementTransform",
    "ClientInteractiveAnimatedElementTransform",
}


def fail(message: str) -> "None":
    raise RuntimeError(message)


def sha256_file(path: Path) -> bytes:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.digest()


def aggregate_sha256(paths: Iterable[Path]) -> str:
    """Hash the ordered file names and their individual content hashes."""

    digest = hashlib.sha256()
    for path in paths:
        encoded_name = path.name.encode("utf-8")
        digest.update(len(encoded_name).to_bytes(4, "little"))
        digest.update(encoded_name)
        digest.update(sha256_file(path))
    return digest.hexdigest()


def parse_version(version_file: Path) -> str:
    if not version_file.is_file():
        fail(f"Falta el fichero de versión: {version_file}")
    for line in version_file.read_text(encoding="utf-8-sig").splitlines():
        key, separator, value = line.partition("=")
        if separator and key.strip().lower() == "version":
            return value.strip()
    fail(f"No hay una línea Version=... en {version_file}")


def streaming_assets_from(client: Path) -> Path:
    candidates = (
        client,
        client / "StreamingAssets",
        client / "Dofus_Data" / "StreamingAssets",
    )
    for candidate in candidates:
        if (candidate / "Content" / "Map" / "Data").is_dir() and (
            candidate / "aa" / "StandaloneWindows64"
        ).is_dir():
            return candidate.resolve()
    fail(
        "No encuentro Dofus_Data/StreamingAssets bajo la ruta indicada: "
        f"{client}"
    )


def numeric_map_bundles(map_data_dir: Path) -> list[tuple[int, Path]]:
    result: list[tuple[int, Path]] = []
    for path in map_data_dir.glob("mapdata_assets_world_*.bundle"):
        match = MAP_BUNDLE_RE.fullmatch(path.name)
        if match:
            result.append((int(match.group(1)), path))
    result.sort(key=lambda item: item[0])
    if not result:
        fail(f"No hay bundles de mapas en {map_data_dir}")
    if len({world for world, _ in result}) != len(result):
        fail("Hay dos bundles con el mismo identificador de mundo.")
    return result


def extract_live_map_elements(
    bundles: list[tuple[int, Path]],
) -> tuple[dict[tuple[int, int], dict[str, Any]], int, collections.Counter[str]]:
    elements: dict[tuple[int, int], dict[str, Any]] = {}
    map_ids: set[int] = set()
    classes: collections.Counter[str] = collections.Counter()

    for bundle_number, (world_id, path) in enumerate(bundles, start=1):
        environment = UnityPy.load(str(path))
        maps: list[tuple[int, dict[str, Any]]] = []

        for obj in environment.objects:
            if obj.type.name != "MonoBehaviour":
                continue
            tree = obj.read_typetree()
            match = MAP_NAME_RE.fullmatch(str(tree.get("m_Name", "")))
            if match and isinstance(tree.get("mapData"), dict):
                maps.append((int(match.group(1)), tree))

        for map_id, tree in sorted(maps, key=lambda item: item[0]):
            if map_id in map_ids:
                fail(f"Mapa duplicado en los bundles: {map_id}")
            map_ids.add(map_id)
            if map_id >> 18 != world_id:
                fail(
                    f"map_{map_id} está en world_{world_id}, pero mapId >> 18 "
                    f"vale {map_id >> 18}."
                )

            references = tree.get("references", {}).get("RefIds", [])
            by_rid = {
                int(reference["rid"]): reference
                for reference in references
                if isinstance(reference, dict) and "rid" in reference
            }
            handles = tree["mapData"].get("interactiveElements", [])
            for handle in handles:
                rid = int(handle["rid"])
                reference = by_rid.get(rid)
                if reference is None:
                    fail(f"map_{map_id}: interactive rid {rid} no resuelto localmente.")

                class_name = str(reference.get("type", {}).get("class", ""))
                if class_name not in VISUAL_CLASSES:
                    fail(
                        f"map_{map_id}: rid {rid} tiene clase interactiva inesperada "
                        f"{class_name!r}."
                    )
                data = reference.get("data")
                if not isinstance(data, dict):
                    fail(f"map_{map_id}: rid {rid} no contiene data.")
                for required in ("m_interactionId", "cellId", "gfxId"):
                    if required not in data:
                        fail(f"map_{map_id}: rid {rid} no contiene {required}.")

                element_id = int(data["m_interactionId"])
                key = (map_id, element_id)
                if key in elements:
                    fail(
                        "La clave (mapId,m_interactionId) no es única: "
                        f"({map_id},{element_id})."
                    )
                cell_id = int(data["cellId"])
                if not 0 <= cell_id <= 559:
                    fail(f"map_{map_id}: casilla interactiva inválida {cell_id}.")

                elements[key] = {
                    "elementCellId": cell_id,
                    "gfxId": int(data["gfxId"]),
                    "visualClass": class_name,
                    "requiresServerUpdate": bool(data.get("requiresServerUpdate", 0)),
                }
                classes[class_name] += 1

        if bundle_number % 50 == 0 or bundle_number == len(bundles):
            print(
                f"    mapas: {bundle_number}/{len(bundles)} bundles, "
                f"{len(map_ids):,} mapas, {len(elements):,} interactivos",
                flush=True,
            )

    return elements, len(map_ids), classes


def find_world_graph(bundle_path: Path) -> dict[str, Any]:
    environment = UnityPy.load(str(bundle_path))
    for obj in environment.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        tree = obj.read_typetree()
        if str(tree.get("m_Name", "")).lower() == "world-graph":
            return tree
    fail(f"No encuentro Assets/Content/World/world-graph.asset en {bundle_path}")


def paired_dictionary(value: Any, context: str) -> list[tuple[Any, Any]]:
    if not isinstance(value, dict):
        fail(f"{context} no es un diccionario serializado.")
    keys = value.get("m_keys")
    values = value.get("m_values")
    if not isinstance(keys, list) or not isinstance(values, list):
        fail(f"{context} no contiene m_keys/m_values.")
    if len(keys) != len(values):
        fail(f"{context}: m_keys y m_values tienen tamaños distintos.")
    return list(zip(keys, values))


def signed_direction(value: Any) -> int:
    raw = int(value)
    # En el asset actual m_direction se serializa como byte: Invalid (-1) es 255.
    return raw - 256 if 128 <= raw <= 255 else raw


def extract_graph(
    tree: dict[str, Any],
    map_elements: dict[tuple[int, int], dict[str, Any]],
) -> tuple[list[dict[str, Any]], list[dict[str, Any]], dict[str, Any]]:
    vertices: list[dict[str, int]] = []
    for map_key, zones in paired_dictionary(tree.get("m_vertices"), "m_vertices"):
        for zone_key, vertex in paired_dictionary(zones, f"m_vertices[{map_key}]"):
            if not isinstance(vertex, dict):
                fail(f"Vértice inválido en el mapa {map_key}.")
            normalized = {
                "mapId": int(vertex["m_mapId"]),
                "zoneId": int(vertex["m_zoneId"]),
                "uid": int(vertex["m_uid"]),
            }
            if normalized["mapId"] != int(map_key):
                fail(f"El vértice {normalized['uid']} no coincide con su clave de mapa.")
            if normalized["zoneId"] != int(zone_key):
                fail(f"El vértice {normalized['uid']} no coincide con su clave de zona.")
            vertices.append(normalized)

    edges: list[dict[str, Any]] = []
    reverse_lookup: dict[tuple[int, int], list[dict[str, Any]]] = {}
    type_counts: collections.Counter[int] = collections.Counter()
    transition_count = 0

    for source_key, destinations in paired_dictionary(tree.get("m_edges"), "m_edges"):
        for target_key, edge in paired_dictionary(
            destinations, f"m_edges[{source_key}]"
        ):
            if not isinstance(edge, dict):
                fail(f"Arista inválida {source_key}->{target_key}.")
            source = edge.get("m_from")
            target = edge.get("m_to")
            transitions = edge.get("m_transitions")
            if not isinstance(source, dict) or not isinstance(target, dict):
                fail(f"Arista {source_key}->{target_key} sin extremos.")
            if not isinstance(transitions, list):
                fail(f"Arista {source_key}->{target_key} sin transiciones.")
            if int(source["m_uid"]) != int(source_key):
                fail(f"La clave de origen no coincide en {source_key}->{target_key}.")
            if int(target["m_uid"]) != int(target_key):
                fail(f"La clave de destino no coincide en {source_key}->{target_key}.")

            normalized_transitions: list[dict[str, Any]] = []
            for transition_index, transition in enumerate(transitions):
                normalized = {
                    "pathType": int(transition["m_type"]),
                    "direction": signed_direction(transition["m_direction"]),
                    "skillId": int(transition["m_skillId"]),
                    "criterion": str(transition.get("m_criterion", "")),
                    "transitionMapId": int(transition["m_transitionMapId"]),
                    "sourceCellId": int(transition["m_cellId"]),
                    "elementId": int(transition["m_id"]),
                    "transitionIndex": transition_index,
                }
                normalized_transitions.append(normalized)
                type_counts[normalized["pathType"]] += 1
                transition_count += 1

            source_uid = int(source_key)
            target_uid = int(target_key)
            reverse_lookup[(source_uid, target_uid)] = normalized_transitions
            edges.append(
                {
                    "sourceUid": source_uid,
                    "sourceMapId": int(source["m_mapId"]),
                    "sourceZoneId": int(source["m_zoneId"]),
                    "targetUid": target_uid,
                    "targetMapId": int(target["m_mapId"]),
                    "targetZoneId": int(target["m_zoneId"]),
                    "transitions": normalized_transitions,
                }
            )

    routes_by_element: dict[tuple[int, int], list[dict[str, Any]]] = (
        collections.defaultdict(list)
    )
    source_cell_matches = 0
    interactive_route_count = 0
    matched_interactive_route_count = 0
    transition_target_mismatches = 0
    unmatched_element_keys: set[tuple[int, int]] = set()

    for edge in edges:
        for transition in edge["transitions"]:
            if transition["pathType"] != 32:
                continue
            interactive_route_count += 1
            if transition["skillId"] < 0 or transition["elementId"] < 0:
                fail("Una transición Interactive no contiene skillId/elementId válidos.")

            key = (edge["sourceMapId"], transition["elementId"])
            element = map_elements.get(key)
            if element is None:
                # Los bundles Content/Map y worldassets se publican en snapshots
                # escalonados. No se inventa geometría para una arista huérfana: queda
                # contabilizada y se excluye de la vista consumible por el servidor.
                unmatched_element_keys.add(key)
                continue
            matched_interactive_route_count += 1
            if element["elementCellId"] == transition["sourceCellId"]:
                source_cell_matches += 1
            if transition["transitionMapId"] != edge["targetMapId"]:
                transition_target_mismatches += 1

            reciprocal = reverse_lookup.get(
                (edge["targetUid"], edge["sourceUid"]), []
            )
            reciprocal_cells = sorted(
                {int(candidate["sourceCellId"]) for candidate in reciprocal}
            )
            route = {
                "pathType": 32,
                "sourceCellId": transition["sourceCellId"],
                "direction": transition["direction"],
                "skillId": transition["skillId"],
                "criterion": transition["criterion"],
                "targetMapId": edge["targetMapId"],
                "transitionMapId": transition["transitionMapId"],
                "sourceZoneId": edge["sourceZoneId"],
                "targetZoneId": edge["targetZoneId"],
                "sourceVertexUid": edge["sourceUid"],
                "targetVertexUid": edge["targetUid"],
                "reciprocalSourceCellIds": reciprocal_cells,
            }
            routes_by_element[key].append(route)

    route_sort_key = lambda route: (
        route["targetMapId"],
        route["targetZoneId"],
        route["targetVertexUid"],
        route["skillId"],
        route["sourceCellId"],
        route["direction"],
        route["criterion"],
        route["transitionMapId"],
    )
    output_elements: list[dict[str, Any]] = []
    criterion_elements = 0
    multiple_target_elements = 0
    multiple_skill_elements = 0
    unconditional_single_target_skill = 0
    reciprocal_one_route_count = 0
    safe_routes: list[dict[str, Any]] = []

    for key in sorted(routes_by_element):
        routes = sorted(routes_by_element[key], key=route_sort_key)
        target_ids = {route["targetMapId"] for route in routes}
        skill_ids = {route["skillId"] for route in routes}
        has_criterion = any(route["criterion"] for route in routes)
        if has_criterion:
            criterion_elements += 1
        if len(target_ids) > 1:
            multiple_target_elements += 1
        if len(skill_ids) > 1:
            multiple_skill_elements += 1
        if len(target_ids) == 1 and len(skill_ids) == 1 and not has_criterion:
            unconditional_single_target_skill += 1
        reciprocal_one_route_count += sum(
            len(route["reciprocalSourceCellIds"]) == 1 for route in routes
        )

        map_id, element_id = key
        element = map_elements[key]

        # Vista plana consumible por el servidor. Una misma definición segura puede
        # aparecer varias veces en el grafo por vértice/zona; se consolida por los
        # campos que realmente seleccionan una transición de juego.
        if len(target_ids) == 1 and len(skill_ids) == 1 and not has_criterion:
            consolidated: dict[
                tuple[int, int, int, str], set[int]
            ] = collections.defaultdict(set)
            for route in routes:
                identity = (
                    route["sourceCellId"],
                    route["skillId"],
                    route["targetMapId"],
                    route["criterion"],
                )
                consolidated[identity].update(route["reciprocalSourceCellIds"])
            for identity in sorted(consolidated):
                source_cell_id, skill_id, target_map_id, criterion = identity
                reciprocal_cells = sorted(consolidated[identity])
                safe_routes.append(
                    {
                        "fromMapId": map_id,
                        "elementId": element_id,
                        "elementCellId": element["elementCellId"],
                        "gfxId": element["gfxId"],
                        "skillId": skill_id,
                        "targetMapId": target_map_id,
                        "sourceCellId": source_cell_id,
                        "derivedArrivalCellId": (
                            reciprocal_cells[0] if len(reciprocal_cells) == 1 else None
                        ),
                        "criterion": criterion,
                        "targetCount": len(target_ids),
                        "ambiguous": False,
                        "arrivalAmbiguous": len(reciprocal_cells) != 1,
                        "reciprocalSourceCellIds": reciprocal_cells,
                        "pathType": 32,
                        "protocolInteractiveTypeId": None,
                    }
                )

        output_elements.append(
            {
                "mapId": map_id,
                "elementId": element_id,
                "elementCellId": element["elementCellId"],
                "gfxId": element["gfxId"],
                "visualClass": element["visualClass"],
                "requiresServerUpdate": element["requiresServerUpdate"],
                "routes": routes,
            }
        )

    counts = {
        "graphMapKeys": len(paired_dictionary(tree.get("m_vertices"), "m_vertices")),
        "graphVertices": len(vertices),
        "graphEdges": len(edges),
        "graphTransitions": transition_count,
        "graphTransitionsByPathType": {
            str(key): type_counts[key] for key in sorted(type_counts)
        },
        "interactiveRoutes": interactive_route_count,
        "interactiveRoutesMatchedToLiveMapElement": matched_interactive_route_count,
        "interactiveRoutesMissingLiveMapElement": (
            interactive_route_count - matched_interactive_route_count
        ),
        "interactiveElementKeysMissingLiveMapElement": len(unmatched_element_keys),
        "interactiveElements": len(output_elements),
        "sourceCellEqualsElementCellRoutes": source_cell_matches,
        "sourceCellDiffersFromElementCellRoutes": (
            matched_interactive_route_count - source_cell_matches
        ),
        "transitionMapDiffersFromTargetMapRoutes": transition_target_mismatches,
        "elementsWithCriterion": criterion_elements,
        "elementsWithMultipleTargets": multiple_target_elements,
        "elementsWithMultipleSkills": multiple_skill_elements,
        "unconditionalSingleTargetSkillElements": unconditional_single_target_skill,
        "routesWithOneReciprocalSourceCell": reciprocal_one_route_count,
        "safeRoutes": len(safe_routes),
        "safeRoutesWithDerivedArrivalCell": sum(
            route["derivedArrivalCellId"] is not None for route in safe_routes
        ),
    }
    safe_routes.sort(
        key=lambda route: (
            route["fromMapId"],
            route["elementId"],
            route["skillId"],
            route["targetMapId"],
            route["sourceCellId"],
        )
    )
    return output_elements, safe_routes, counts


def write_compact_elements(
    destination: Path, elements: dict[tuple[int, int], dict[str, Any]]
) -> None:
    by_map: dict[str, list[dict[str, int]]] = collections.defaultdict(list)
    for (map_id, element_id), element in sorted(elements.items()):
        by_map[str(map_id)].append(
            {
                "e": element_id,
                "c": element["elementCellId"],
                "g": element["gfxId"],
            }
        )
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(by_map, stream, ensure_ascii=False, separators=(",", ":"))
        stream.write("\n")


def write_transition_document(destination: Path, document: dict[str, Any]) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(document, stream, ensure_ascii=False, indent=2)
        stream.write("\n")


def parse_arguments() -> argparse.Namespace:
    repository_root = Path(__file__).resolve().parent.parent
    local_app_data = Path(os.environ.get("LOCALAPPDATA", ""))
    default_client = local_app_data / "Ankama" / "Dofus-dofus3"
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client", type=Path, default=default_client)
    parser.add_argument(
        "--elements-output",
        type=Path,
        default=repository_root / "datos" / f"interactive_elements_{EXPECTED_CLIENT_VERSION}.json",
    )
    parser.add_argument(
        "--transitions-output",
        type=Path,
        default=repository_root
        / "datos"
        / f"world_interactive_transitions_{EXPECTED_CLIENT_VERSION}.json",
    )
    parser.add_argument("--unity-version", default=UNITY_VERSION)
    return parser.parse_args()


def main() -> int:
    arguments = parse_arguments()
    UnityPy.config.FALLBACK_UNITY_VERSION = arguments.unity_version
    warnings.filterwarnings("ignore", message="No valid Unity version found")

    streaming_assets = streaming_assets_from(arguments.client)
    client_version = parse_version(streaming_assets / "version")
    if client_version != EXPECTED_CLIENT_VERSION:
        fail(
            f"Este extractor está validado para {EXPECTED_CLIENT_VERSION}; el cliente "
            f"indica {client_version}. Revalida esquema y conteos antes de importarlo."
        )

    map_bundles = numeric_map_bundles(streaming_assets / "Content" / "Map" / "Data")
    world_bundle = (
        streaming_assets
        / "aa"
        / "StandaloneWindows64"
        / "worldassets_assets_all.bundle"
    )
    if not world_bundle.is_file():
        fail(f"Falta el bundle del grafo mundial: {world_bundle}")

    print(f"[1/4] Extrayendo {len(map_bundles)} bundles de mapas...", flush=True)
    map_elements, map_count, visual_class_counts = extract_live_map_elements(
        map_bundles
    )

    print("[2/4] Leyendo world-graph.asset y cruzando elementos...", flush=True)
    graph_tree = find_world_graph(world_bundle)
    transition_elements, safe_routes, graph_counts = extract_graph(
        graph_tree, map_elements
    )

    print("[3/4] Calculando huellas de los assets de origen...", flush=True)
    world_hash = sha256_file(world_bundle).hex()
    map_hash = aggregate_sha256(path for _, path in map_bundles)

    counts = {
        "mapBundles": len(map_bundles),
        "maps": map_count,
        "mapInteractiveElements": len(map_elements),
        "mapInteractiveElementsByVisualClass": {
            key: visual_class_counts[key] for key in sorted(visual_class_counts)
        },
        **graph_counts,
    }
    document = {
        "schemaVersion": 1,
        "clientVersion": client_version,
        "unityVersion": arguments.unity_version,
        "sources": {
            "mapBundles": "Dofus_Data/StreamingAssets/Content/Map/Data/mapdata_assets_world_*.bundle",
            "mapBundlesAggregateSha256": map_hash,
            "worldGraph": "Dofus_Data/StreamingAssets/aa/StandaloneWindows64/worldassets_assets_all.bundle::Assets/Content/World/world-graph.asset",
            "worldGraphBundleSha256": world_hash,
        },
        "semantics": {
            "elementKey": "(mapId,elementId=m_interactionId)",
            "elementCellId": "Cell used to state the visual interactive in jss.f15.",
            "pathType": "World-pathfinding enum; 32 means Interactive. It is NOT the jss interactive type.",
            "sourceCellId": "Source-map cell from which the transition is taken.",
            "targetMapId": "Destination vertex map. For pathType 32 it equals transitionMapId in this asset set.",
            "targetCellId": "Not stored in the client world graph.",
            "reciprocalSourceCellIds": "Derived source cells on the exact reciprocal target->source edge; candidates only, never asserted as authoritative spawn cells.",
            "criterion": "Server/game-state route criterion; preserve alternatives and do not pick an unmatched route.",
        },
        "counts": counts,
        "safeRoutes": safe_routes,
        "elements": transition_elements,
    }

    print("[4/4] Escribiendo JSON deterministas...", flush=True)
    write_compact_elements(arguments.elements_output, map_elements)
    write_transition_document(arguments.transitions_output, document)

    print(
        f"[+] {arguments.elements_output}: {len(map_elements):,} elementos vivos",
        flush=True,
    )
    print(
        f"[+] {arguments.transitions_output}: "
        f"{counts['interactiveRoutes']:,} rutas / "
        f"{counts['interactiveElements']:,} elementos",
        flush=True,
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (KeyError, TypeError, ValueError, RuntimeError) as error:
        print(f"[!] {error}", file=sys.stderr)
        raise SystemExit(1)
