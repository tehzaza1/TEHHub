"""Audit PoE2DB's linked item catalog; cache pages and never merge ambiguous aliases."""
import concurrent.futures
import json
import pathlib
import re
import urllib.request
import urllib.parse
from collections import defaultdict
from bs4 import BeautifulSoup

BASE = 'https://poe2db.tw/us/'
OUT = pathlib.Path('artifacts/item-mapping-audit')
CACHE = OUT / 'pages'
CACHE.mkdir(parents=True, exist_ok=True)
errors = []

def fetch(url):
    file = CACHE / (urllib.parse.quote(url.removeprefix(BASE), safe='') + '.html')
    try:
        if file.exists(): return file.read_text(encoding='utf-8')
        req = urllib.request.Request(url, headers={'User-Agent': 'Mozilla/5.0'})
        text = urllib.request.urlopen(req, timeout=20).read().decode()
        file.write_text(text, encoding='utf-8')
        return text
    except Exception as error:
        errors.append({'url': url, 'error': str(error)})
        return ''

def norm(value): return re.sub('[^a-z0-9]', '', value.lower())

index = BeautifulSoup(fetch(BASE + 'Items'), 'html.parser')
categories = {urllib.parse.urljoin(BASE, a['href']) for a in index.select('#Item a[href]')}
categories.add(BASE + 'Currency')
links = set()
with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
    for html in pool.map(fetch, sorted(categories)):
        soup = BeautifulSoup(html, 'html.parser')
        for image in soup.select('img[src*="Art/2DItems"]'):
            a = image.find_parent('a', href=True)
            if a:
                url = urllib.parse.urljoin(BASE, a['href'])
                if url.startswith(BASE): links.add(url.split('#')[0])
print('Categories:', len(categories), 'item links:', len(links), flush=True)
rows = []

def parse(url):
    soup = BeautifulSoup(fetch(url), 'html.parser')
    fields = {}
    for tr in soup.select('tr'):
        cells = tr.find_all('td', recursive=False)
        if len(cells) == 2:
            k, v = (c.get_text(' ', strip=True) for c in cells)
            if k == 'ItemType' and v.startswith('Metadata/Items/'): fields['Type'] = v
            elif k in ('Type', 'Icon') and (k != 'Type' or v.startswith('Metadata/Items/')): fields[k] = v
    if not fields.get('Type', '').startswith('Metadata/Items/'):
        return {'source': url, 'status': 'no-item-metadata'}
    title = soup.find('meta', property='og:title')
    name = title.get('content', '') if title else ''
    name = name.split(' - PoE2DB')[0].strip()
    return {'source': url, 'name': name, 'path': fields['Type'], 'icon': fields.get('Icon', '')}

with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
    for i, row in enumerate(pool.map(parse, sorted(links)), 1):
        rows.append(row)
        if i % 100 == 0: print('Parsed:', i, flush=True)

mapping = json.loads(pathlib.Path('Plugins/LootValue/pathBasenameToItemName.json').read_text(encoding='utf-8-sig'))
aliases = defaultdict(set)
excluded = []
equipment = {'Armours', 'Weapons', 'Rings', 'Amulets', 'Belts', 'Quivers', 'Flasks', 'Jewels'}
for row in rows:
    if not row.get('name') or 'path' not in row: continue
    if row['path'].split('/')[2] in equipment or 'DNT' in row['name'] or 'UNUSED' in row['name'].upper() or row['name'] == 'Removed Skill':
        excluded.append(row)
        continue
    aliases[norm(row['path'].rsplit('/', 1)[-1])].add(row['name'])
missing = {key: next(iter(names)) for key, names in aliases.items() if len(names) == 1 and key not in mapping}
conflicts = {key: sorted(names) for key, names in aliases.items() if len(names) > 1 or (key in mapping and mapping[key] not in names)}
report = {'categories': sorted(categories), 'linkedItems': len(links), 'records': rows,
          'errors': errors, 'missing': missing, 'conflicts': conflicts, 'excluded': excluded,
          'scope': 'Items category links plus Currency; only detail pages exposing Metadata/Items Type. Not proof of every hidden game item.'}
(OUT / 'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print('Metadata records:', sum('path' in r for r in rows), 'missing:', len(missing), 'conflicts:', len(conflicts), 'errors:', len(errors), flush=True)
