# -*- coding: utf-8 -*-
"""Primera sonda: cómo es por dentro un bundle de datos del cliente, para saber
qué hay que convertir al formato que espera el emulador."""
import UnityPy, json, sys

# Los bundles del cliente no llevan la versión de Unity escrita dentro; el README
# la trae medida: 6000.3.16f1 (MelonLoader la reporta igual). Sin esto, UnityPy
# no sabe cómo leer los campos.
UnityPy.config.FALLBACK_UNITY_VERSION = "6000.3.16f1"

RUTA = r"C:\Users\rapha\AppData\Local\Ankama\Dofus-dofus3\Dofus_Data\StreamingAssets\Content\Data"

def sondear(bundle):
    env = UnityPy.load(f"{RUTA}\\{bundle}")
    print(f"=== {bundle} ===")
    for obj in env.objects:
        try:
            if obj.type.name in ("MonoBehaviour",):
                arbol = obj.read_typetree()
                # El envoltorio de referencias: lo que dofusdude llama references.RefIds
                claves = list(arbol.keys())
                print(f"  MonoBehaviour keys: {claves[:10]}")
                refs = None
                for k in ("references", "RefIds", "_references"):
                    if k in arbol: refs = arbol[k]; break
                if isinstance(refs, dict) and "RefIds" in refs:
                    filas = refs["RefIds"]
                    print(f"  references.RefIds: {len(filas)} filas")
                    if filas:
                        print(f"  primera fila keys: {list(filas[0].keys())[:10]}")
                        print("  primera fila:", json.dumps(filas[0], ensure_ascii=False)[:600])
                else:
                    print("  contenido:", json.dumps(arbol, ensure_ascii=False)[:600])
                break
            else:
                print(f"  objeto {obj.type.name}")
        except Exception as e:
            print(f"  fallo: {e}")
    print()

for b in ("data_assets_jobsdataroot.asset.bundle",
          "data_assets_skillsdataroot.asset.bundle",
          "data_assets_recipesdataroot.asset.bundle"):
    sondear(b)
