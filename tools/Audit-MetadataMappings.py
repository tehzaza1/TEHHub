import json, pathlib, re, urllib.parse
from collections import defaultdict
from bs4 import BeautifulSoup

root = pathlib.Path('artifacts/item-mapping-audit')
old = json.loads((root/'report.json').read_text(encoding='utf-8'))
mapping = json.loads(pathlib.Path('Plugins/LootValue/pathBasenameToItemName.json').read_text(encoding='utf-8-sig'))
norm = lambda s: re.sub('[^a-z0-9]', '', s.lower())
excluded = {'Armours','Weapons','Rings','Amulets','Belts','Quivers','Flasks','Jewels','Charms'}
records=[]; no_metadata=[]; skipped=[]; aliases=defaultdict(list)
for entry in old['records']:
    url=entry['source']; file=root/'pages'/(urllib.parse.quote(url.removeprefix('https://poe2db.tw/us/'),safe='')+'.html')
    if not file.exists():
        skipped.append({'source':url,'reason':'cache missing'}); continue
    soup=BeautifulSoup(file.read_text(encoding='utf-8'),'html.parser')
    title=soup.find('meta',property='og:title')
    title=title.get('content','').split(' - PoE2DB')[0].strip() if title else ''
    found=False
    seen=set()
    for tr in soup.select('tr'):
        td=tr.find_all('td',recursive=False)
        if len(td)!=2: continue
        field=td[0].get_text(' ',strip=True); path=td[1].get_text(' ',strip=True)
        if field not in ('Type','ItemType') or not path.startswith('Metadata/Items/'): continue
        found=True
        if path in seen: continue
        seen.add(path)
        # Keep metadata names separate from unique art names on mixed pages.
        name=title
        for parent in tr.parents:
            header=parent.select_one('.itemHeader .itemName:not(.typeLine)')
            if header:
                name=header.get_text(' ',strip=True); break
            if parent.name=='body': break
        if not name:
            fallback=soup.select_one('.itemHeader .itemName.typeLine') or soup.select_one('h1')
            if fallback: name=fallback.get_text(' ',strip=True)
        row={'source':url,'field':field,'path':path,'name':name,'title':title}
        if any(x in name.upper() for x in ('DNT','UNUSED','REMOVED SKILL')):
            skipped.append(dict(row,reason='unavailable marker')); continue
        category=path.split('/')[2]
        if category in excluded:
            skipped.append(dict(row,reason='equipment handled only by unique art')); continue
        key=norm(path.rsplit('/',1)[-1]); row['key']=key
        records.append(row); aliases[key].append(row)
    if not found: no_metadata.append(url)
missing={}; conflicts={}; covered=0
for key, rows in aliases.items():
    names={r['name'] for r in rows}
    if len(names)!=1 or (key in mapping and mapping[key] not in names):
        conflicts[key]={'existing':mapping.get(key),'candidates':sorted(names),'evidence':rows}
    elif key not in mapping: missing[key]={'name':next(iter(names)),'evidence':rows}
    else: covered+=1
result={'scope':'Independent cached catalog audit; metadata ONLY, includes every Type and ItemType table per page. No network refresh. Non-unique equipment excluded, unique equipment belongs to art map.','pages':len(old['records']),'metadataRecords':len(records),'distinctAliases':len(aliases),'coveredAliases':covered,'missing':missing,'conflicts':conflicts,'noMetadataPages':no_metadata,'skipped':skipped,'records':records,'catalogErrors':old.get('errors',[])}
(root/'independent-metadata-report.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:(len(v) if isinstance(v,(list,dict)) else v) for k,v in result.items() if k!='scope'},indent=2))
