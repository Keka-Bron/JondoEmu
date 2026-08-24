# -*- coding: utf-8 -*-
"""
Saca los oficios del cliente instalado y los deja donde el emulador los espera.

El cliente de Dofus 3 lleva sus catálogos enteros en bundles de Unity, bajo
Dofus_Data/StreamingAssets/Content/Data/data_assets_*dataroot.asset.bundle.
Dentro, cada uno es un MonoBehaviour con un campo «references» cuya lista
«RefIds» trae filas {rid, type, data} — exactamente el envoltorio que el emulador
lee con DofusDudeCatalog (datos/JsonFromDofusDude/*.json). Este script copia esa
estructura tal cual al JSON: nada se transforma, nada se inventa; los datos son
los del propio cliente 3.6.10.10 instalado en la máquina.

Se corre tal cual:  python tools/extraer_oficios.py  (requiere: pip install UnityPy)
"""
import json
import os
import sys

import UnityPy

# Los bundles no llevan la versión de Unity escrita dentro; el README la trae
# medida: 6000.3.16f1. Sin esto UnityPy no sabe leer los campos.
UnityPy.config.FALLBACK_UNITY_VERSION = "6000.3.16f1"

RAIZ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# El cliente instalado. Si se muda, se cambia aquí —o se pasa por línea de órdenes.
CLIENTE = r"C:\Users\rapha\AppData\Local\Ankama\Dofus-dofus3"
DATOS_CLIENTE = os.path.join(CLIENTE, "Dofus_Data", "StreamingAssets",
                             "Content", "Data")
DESTINO = os.path.join(RAIZ, "datos", "JsonFromDofusDude")

BUNDLES = {
    "jobs.json": "data_assets_jobsdataroot.asset.bundle",
    "skills.json": "data_assets_skillsdataroot.asset.bundle",
    "recipes.json": "data_assets_recipesdataroot.asset.bundle",
}


def filas_del_bundle(ruta_bundle):
    """Las RefIds del primer MonoBehaviour del bundle, tal cual vienen."""
    env = UnityPy.load(ruta_bundle)
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        arbol = obj.read_typetree()
        referencias = arbol.get("references")
        if isinstance(referencias, dict) and isinstance(referencias.get("RefIds"), list):
            return referencias["RefIds"]
    return None


def main():
    if len(sys.argv) > 1:
        global DATOS_CLIENTE
        DATOS_CLIENTE = sys.argv[1]

    if not os.path.isdir(DATOS_CLIENTE):
        print(f"[!] No encuentro el cliente en {DATOS_CLIENTE}")
        sys.exit(1)

    os.makedirs(DESTINO, exist_ok=True)

    for destino, bundle in BUNDLES.items():
        ruta = os.path.join(DATOS_CLIENTE, bundle)
        if not os.path.exists(ruta):
            print(f"[!] Falta {bundle} en el cliente; {destino} no se toca.")
            continue

        filas = filas_del_bundle(ruta)
        if not filas:
            print(f"[!] {bundle} no trae references.RefIds; no se toca {destino}.")
            continue

        # El envoltorio entero, como lo lee DofusDudeCatalog.Rows: sólo se
        # selecciona la parte que usa y se conserva su forma.
        documento = {"references": {"RefIds": filas}}
        salida = os.path.join(DESTINO, destino)
        with open(salida, "w", encoding="utf-8") as fh:
            json.dump(documento, fh, ensure_ascii=False, separators=(",", ":"))

        cuantos = sum(1 for f in filas if isinstance(f.get("data"), dict))
        print(f"[+] {destino}: {cuantos} filas ({bundle})")

    print(f"[i] Los dumps quedan en {DESTINO}; el servidor los importa al arrancar.")


if __name__ == "__main__":
    main()
