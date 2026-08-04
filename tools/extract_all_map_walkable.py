import os, warnings, json
warnings.simplefilter('ignore')

import UnityPy
UnityPy.config.FALLBACK_UNITY_VERSION = '2022.3.20f1'

bundle_dir = r'C:\Jondo\DofusClient\Dofus_Data\StreamingAssets\Content\Map\Data'
output_file = r'C:\Jondo\map_walkable_cells.json'

print("Extracting walkable cells for all maps from Unity AssetBundles...")

map_walkable = {}
processed_bundles = 0

bundle_files = [os.path.join(bundle_dir, f) for f in os.listdir(bundle_dir) if f.endswith('.bundle') and 'mapdata' in f]

for path in bundle_files:
    try:
        env = UnityPy.load(path)
        for obj in env.objects:
            if obj.type.name == 'MonoBehaviour':
                try:
                    tree = obj.read_typetree()
                    name = str(tree.get('m_Name', ''))
                    if name.startswith('map_') and 'mapData' in tree:
                        map_id_str = name.replace('map_', '')
                        if map_id_str.isdigit():
                            map_id = int(map_id_str)
                            cells_data = tree['mapData'].get('cellsData', [])
                            walkable = []
                            for c in cells_data:
                                cell_num = c.get('cellNumber', 0)
                                mov = c.get('mov', 0)
                                non_rp = c.get('nonWalkableDuringRP', 0)
                                monster_blocked = c.get('roleplayMonstersMovementBlocked', 0)
                                
                                # Filter out edge cells (cols 0,1,12,13 and rows 0..5, 35..39) to keep mobs on interior ground
                                r = cell_num // 14
                                col = cell_num % 14
                                if r >= 6 and r <= 34 and col >= 2 and col <= 11:
                                    if mov == 1 and non_rp == 0 and monster_blocked == 0:
                                        walkable.append(cell_num)
                            
                            if walkable:
                                map_walkable[map_id] = walkable
                except Exception:
                    pass
    except Exception:
        pass
    processed_bundles += 1
    if processed_bundles % 20 == 0:
        print(f"Processed {processed_bundles}/{len(bundle_files)} bundles, maps loaded: {len(map_walkable)}...")

with open(output_file, 'w') as f:
    json.dump(map_walkable, f)

print(f"DONE! Successfully extracted walkable cells for {len(map_walkable)} maps to {output_file}!")
