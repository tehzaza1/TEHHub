"""Merge only unambiguous PoE2DB metadata aliases and unique art from cached audit."""
import json
import pathlib
import re
import urllib.parse
from collections import defaultdict
from bs4 import BeautifulSoup

root = pathlib.Path('Plugins/LootValue')
audit = pathlib.Path('artifacts/item-mapping-audit')
report = json.loads((audit / 'report.json').read_text(encoding='utf-8'))
paths = json.loads((root / 'pathBasenameToItemName.json').read_text(encoding='utf-8-sig'))
arts = json.loads((root / 'uniqueArtMapping.json').read_text(encoding='utf-8-sig'))
norm = lambda s: re.sub('[^a-z0-9]', '', s.lower())
path_names = defaultdict(set)
equipment = {'Armours', 'Weapons', 'Rings', 'Amulets', 'Belts', 'Quivers', 'Flasks', 'Jewels'}
for row in report['records']:
    if row.get('path') and row.get('name'):
        # Equipment pricing is restricted to uniques, resolved by uniqueArtMapping.
        if row['path'].split('/')[2] in equipment: continue
        name = row['name']
        if 'DNT' in name or 'UNUSED' in name.upper(): continue
        path_names[norm(row['path'].rsplit('/', 1)[-1])].add(name)

art_names = defaultdict(set)
soup = BeautifulSoup((audit / 'pages/Unique_item.html').read_text(encoding='utf-8'), 'html.parser')
for image in soup.select('a.UniqueItems img'):
    card = image.parent.parent.parent
    name = card.select_one('.uniqueName')
    src = image.get('src', '')
    if name and '/image/' in src:
        art = src.split('/image/', 1)[1].rsplit('.', 1)[0] + '.dds'
        art_names[art].add(name.get_text(' ', strip=True))

# Some unique detail pages are absent from the aggregate unique catalog.
for row in report['records']:
    if row.get('status') != 'no-item-metadata': continue
    file = audit / 'pages' / (urllib.parse.quote(row['source'].removeprefix('https://poe2db.tw/us/'), safe='') + '.html')
    detail = BeautifulSoup(file.read_text(encoding='utf-8'), 'html.parser')
    primary = detail.select_one('.newItemPopup')
    if primary is None or 'UniquePopup' not in primary.get('class', []): continue
    name = detail.select_one('.UniquePopup .itemHeader .itemName:not(.typeLine)')
    image = detail.find('meta', property='og:image')
    if name and image and '/image/Art/2DItems/' in image.get('content', '') and '/Gems/' not in image.get('content', ''):
        art = image['content'].split('/image/', 1)[1].rsplit('.', 1)[0] + '.dds'
        art_names[art].add(name.get_text(' ', strip=True))

skipped = []
added_paths = {}
for key, names in path_names.items():
    if len(names) != 1:
        skipped.append({'kind': 'metadata', 'key': key, 'names': sorted(names)}); continue
    name = next(iter(names))
    if key in paths:
        if paths[key] != name: skipped.append({'kind': 'existing-metadata', 'key': key, 'existing': paths[key], 'names': [name]})
        continue
    paths[key] = name
    added_paths[key] = name

added_arts = {}
existing_arts = {norm(key): key for key in arts}
# Loader creates basename aliases too, so a new full path must not introduce ambiguity there.
basenames = defaultdict(set)
for key, names in list(arts.items()) + [(k, sorted(v)) for k, v in art_names.items()]:
    basenames[norm(key.rsplit('/', 1)[-1].rsplit('.', 1)[0])].update(names)
for key, names in art_names.items():
    basename = norm(key.rsplit('/', 1)[-1].rsplit('.', 1)[0])
    if len(names) != 1 or len(basenames[basename]) != 1:
        skipped.append({'kind': 'unique-art', 'key': key, 'names': sorted(basenames[basename])}); continue
    name = next(iter(names))
    if norm(key) in existing_arts:
        old = arts[existing_arts[norm(key)]]
        if old != [name]: skipped.append({'kind': 'existing-unique-art', 'key': key, 'existing': old, 'names': [name]})
        continue
    arts[key] = [name]
    added_arts[key] = [name]

for file, data in [('pathBasenameToItemName.json', paths), ('uniqueArtMapping.json', arts)]:
    (root / file).write_text(json.dumps(data, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
result = {'addedMetadata': added_paths, 'addedUniqueArt': added_arts, 'skipped': skipped,
          'scope': 'Metadata records exposed by cached linked-item audit; unique art from Unique_item catalog. Existing mappings preserved.'}
pathlib.Path('Documentation/guides/item-mapping-fill.json').write_text(json.dumps(result, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
print('Added metadata:', len(added_paths), 'Added unique art:', len(added_arts), 'Skipped:', len(skipped))
