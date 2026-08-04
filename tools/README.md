# Herramientas de Extracción de Datos de Dofus 3

Este directorio contiene los scripts en Python para regenerar los ficheros JSON de datos a partir de los bundles descompilados del cliente Dofus 3:

## Requisitos

- **Python 3.10+**
- **UnityPy**: `pip install UnityPy`
- Copia instalada del cliente de Dofus 3.

## Scripts Disponibles

1. **`extract_all_map_walkable.py`**:
   - Extrae la transitabilidad de mapa en roleplay para todos los mapas.
   - Genera: `map_walkable_cells.json`.

2. **`extract_fight_cells.py`**:
   - Extrae las celdas de combate y casillas opacas (línea de visión) de 17.222 mapas.
   - Genera: `map_fight_cells.json`.

3. **`extract_character_xp.py`**:
   - Extrae la tabla oficial de experiencia por nivel (1.889 niveles).
   - Genera: `character_xp.json`.

## Uso

```bash
python tools/extract_all_map_walkable.py
python tools/extract_fight_cells.py
python tools/extract_character_xp.py
```
