import json,re,pathlib,urllib.parse
from collections import defaultdict
from bs4 import BeautifulSoup
ROOT=pathlib.Path('artifacts/item-mapping-audit')
raw=json.loads(pathlib.Path('Plugins/LootValue/uniqueArtMapping.json').read_text(encoding='utf-8-sig'))
norm=lambda x:re.sub('[^a-z0-9]','',x.lower().strip())
keys=defaultdict(set)
for art,names in raw.items():
 if not names:continue
 for key in (art.strip().lower(),norm(art),pathlib.PurePosixPath(art).stem.lower(),norm(pathlib.PurePosixPath(art).stem)):
  keys[key].add(names[0])
records={}; ambiguous=[]; noicon=[]; excluded=[]; encoding=[]
for file in ROOT.joinpath('pages').glob('*.html'):
 text=file.read_text(encoding='utf-8')
 if 'UniquePopup' not in text:continue
 soup=BeautifulSoup(text,'html.parser')
 for popup in soup.select('.UniquePopup'):
  header=popup.select_one('.itemHeader .itemName')
  if not header:continue
  name=header.get_text(' ',strip=True)
  typeline=popup.select_one('.itemHeader .typeLine')
  base=typeline.get_text(' ',strip=True) if typeline else ''
  if 'Strongbox' in base:
   excluded.append({'name':name,'base':base,'reason':'world object, not item'});continue
  if re.search(r'DNT|UNUSED',name,re.I):continue
  pane=popup.find_parent(class_='tab-pane')
  icons=[]
  if pane:
   for tr in pane.select('tr'):
    cells=tr.find_all('td',recursive=False)
    if len(cells)==2 and cells[0].get_text(strip=True)=='Icon':
     icon=cells[1].get_text(strip=True)
     if icon.startswith('Art/2DItems/'):icons.append(icon+'.dds')
  # Icon fields can be multiple for aggregate tabs; prefer this popup's adjacent item image.
  images=popup.parent.select('.itemboximage img')
  imageicons=[]
  for img in images:
   src=img.get('src',''); m=re.search(r'(Art/2DItems/[^?]+?)\.(?:webp|png|dds)(?:\?|$)',src)
   if m:imageicons.append(m.group(1)+'.dds')
  if len(set(imageicons))==1:icons=imageicons
  icons=sorted(set(icons))
  if len(icons)!=1:
   (ambiguous if icons else noicon).append({'name':name,'file':file.name,'icons':icons});continue
  icon=icons[0]
  # Unique items only; currency/sanctum relic uniques remain valid mappings too.
  record={'name':name,'icon':icon,'source':'https://poe2db.tw/us/'+urllib.parse.unquote(file.stem)}
  records[(name,icon)]=record
missing=[]; wrong=[]; covered=0
for record in records.values():
 icon=record['icon']; exact=raw.get(icon)
 if exact and record['name'] in exact:covered+=1
 elif exact:
  if '\ufffd' in record['name']:encoding.append({**record,'mapped':exact,'reason':'cached HTML replacement character, do not overwrite mapping'})
  else:wrong.append({**record,'mapped':exact})
 else:
  basename=pathlib.PurePosixPath(icon).stem
  effective=set().union(*(keys[k] for k in (icon.lower(),norm(icon),basename.lower(),norm(basename))))
  missing.append({**record,'effectiveAliasNames':sorted(effective),'resolvedByAlias':record['name'] in effective})
report={'scope':'Cached detail and category pages; UniquePopup item name and its associated Icon/itemboximage; never og:image. DNT/UNUSED names and Strongbox world objects skipped. Metadata lookup separate.', 'mappingPaths':len(raw),'uniqueArtRecords':len(records),'exactCovered':covered,'missingExactPaths':missing,'wrongExactNames':wrong,'cachedEncodingIssues':encoding,'excludedWorldObjects':excluded,'loaderAliasCollisions':[{'key':key,'names':sorted(names)} for key,names in keys.items() if len(names)>1],'ambiguousPopupIcons':ambiguous,'noIconPopups':noicon,'records':list(records.values())}
ROOT.joinpath('unique-reaudit-report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:len(v) if isinstance(v,list) else v for k,v in report.items() if k!='records'},ensure_ascii=False,indent=2))
