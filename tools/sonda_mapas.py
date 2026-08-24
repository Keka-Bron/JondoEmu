# -*- coding: utf-8 -*-
"""Sonda final: dónde viven los ids de mapa del cliente y si los 4 de las
mazmorras rotas existen en alguno de los catálogos de mapa."""
import os
import UnityPy, json

UnityPy.config.FALLBACK_UNITY_VERSION = "6000.3.16f1"
CLIENTE = r"C:\Users\rapha\AppData\Local\Ankama\Dofus-dofus3\Dofus_Data\StreamingAssets\Content\Data"
BUSCADOS = {232784389, 232785413, 232786435, 232787459}

for bundle in ("data_assets_mapsinformationdataroot.asset.bundle",
               "data_assets_mapreferencesdataroot.asset.bundle",
               "data_assets_mapscoordinatesdataroot.asset.bundle"):
    env = UnityPy.load(os.path.join(CLIENTE, bundle))
    encontrados = {}
    total_ids = set()
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        arbol = obj.read_typetree()
        # tanto si viene por references.RefIds como por objectsById
        refs = arbol.get("references")
        filas = []
        if isinstance(refs, dict) and isinstance(refs.get("RefIds"), list):
            filas = [f.get("data", {}) for f in refs["RefIds"] if isinstance(f.get("data"), dict)]
        for fila in filas:
            i = fila.get("id", fila.get("mapId"))
            if i is not None:
                total_ids.add(i)
                if i in BUSCADOS:
                    encontrados[i] = json.dumps(fila, ensure_ascii=False)[:120]
        if not filas:
            claves = [k for k in arbol.keys() if not k.startswith("m_")]
            print(f"{bundle}: claves {claves[:8]}")
    print(f"{bundle}: {len(total_ids):,} ids; buscados presentes: {len(encontrados)}")
    for k, v in encontrados.items():
        print(f"   {k}: {v}")
