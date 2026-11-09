"""Persistent MySQL gallery worker. Does not write input inventories."""
import sys,os,json,hashlib,time,struct,shutil,datetime,itertools,traceback,ctypes,re
import xml.etree.ElementTree as ET
from pathlib import Path
from collections import defaultdict
from concurrent.futures import ThreadPoolExecutor,as_completed
sys.path.insert(0,str(Path(__file__).parent/'python-deps'))
import pymysql
import FileManager as FM
import FileManagerWorker as FMW
from PIL import Image,ImageOps,ExifTags,__version__ as PILLOW
Image.MAX_IMAGE_PIXELS=150_000_000
BASE=Path('C:/downloads/gallery')
def configured_root():
 settings=ET.parse(BASE/'iis-site/web.config').getroot().find('appSettings')
 return Path(os.path.abspath(os.path.expandvars(next(item.get('value') for item in settings if item.get('key')=='WallpaperRoot'))))
ROOT=configured_root()
CONFIG=BASE/'iis-data/mysql.json';EXT={'.jpg','.jpeg','.png','.webp','.gif','.bmp','.tif','.tiff','.avif','.swf'}
def dumps(x):return json.dumps(x,ensure_ascii=False,sort_keys=True,separators=(',',':'))
def sha(x):return hashlib.sha256(x).digest()
def pathkey(x):return sha(x.replace('\\','/').casefold().encode())
def ticks(st):return st.st_mtime_ns//100+621355968000000000
def now():return datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None)

def require_root():
 if not ROOT.is_dir():raise OSError('Wallpaper storage is unavailable; database file presence was not reconciled.')

def walk_error(error):raise error
def decode(x):return json.loads(x) if isinstance(x,(str,bytes)) else x
def connect():
 c=json.loads(CONFIG.read_text());return pymysql.connect(host=c['host'],port=c.get('port',3306),user=c['user'],password=c['password'],database=c['database'],ssl_ca=c['ca'],ssl_verify_cert=True,ssl_verify_identity=True,autocommit=True,charset='utf8mb4',cursorclass=pymysql.cursors.DictCursor,connect_timeout=10,read_timeout=90,write_timeout=90)
def q(db,sql,args=()):
 with db.cursor() as c:c.execute(sql,args);return list(c.fetchall())
def run(db,sql,args=()):
 with db.cursor() as c:c.execute(sql,args);return c.lastrowid,c.rowcount
def state(db,key,value=None):
 if value is None:
  r=q(db,'SELECT value FROM gallery_state WHERE state_key=%s',(key,));return decode(r[0]['value']) if r else {}
 run(db,'INSERT INTO gallery_state(state_key,value) VALUES(%s,%s) ON DUPLICATE KEY UPDATE value=VALUES(value)',(key,dumps(value)))
def progress(db,message,processed=0,total=0,phase='scanning',**extra):state(db,'worker_status',dict(phase=phase,message=message,processed=processed,total=total,updated_utc=now().isoformat(),**extra))
def safe(relative):
 p=(ROOT/relative.replace('\\','/')).resolve();p.relative_to(ROOT)
 for parent in [p,*p.parents]:
  if parent==ROOT:break
  if parent.exists() and (parent.is_symlink() or getattr(parent.stat(),'st_file_attributes',0)&1024):raise ValueError('Linked file/folder is not allowed: '+str(parent))
 return p
def category(r):return str(r.get('category','')).lower() if str(r.get('category','')).lower() in ['sfw','sketchy','nsfw'] else 'not_provided'
def file_name(relative,owner):return relative if owner=='.' else relative[len(owner)+1:]
def collection(db,key):
 safe(key);run(db,'INSERT INTO collections(collection_key,root_path) VALUES(%s,%s) ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id)',(key,str(ROOT/key)));return q(db,'SELECT id FROM collections WHERE collection_key=%s',(key,))[0]['id']
def metadata_score(r):
 score=0
 for k,w in [('description',2),('image_title',2),('selftext',1),('tags',10),('source',10),('url',5),('resolution',10),('source_category',5),('purity',5),('age_assessment',5),('discovered_from',5)]:
  v=r.get(k)
  if v:score+=min(len(dumps(v)),400)*w if k in ['description','image_title','selftext'] else (len(v)*w if isinstance(v,list) else w)
 return min(score,2147483647)
def clean(row):
 r=decode(row['raw_record']).copy()
 for k in ['site','record_type','status','category','description','resolution']:
  if row.get(k) is not None:r[k]=row[k]
 for k,col in [('id','source_id'),('source','source_url'),('url','image_url')]:
  if row.get(col) is not None:r[k]=row[col]
 return r
def update_entry(db,eid,patch,action='worker_update',hidden=None,canonical=-1):
 row=q(db,'SELECT * FROM inventory_entries WHERE id=%s',(eid,))[0];r=clean(row)
 if callable(patch):patch(r)
 else:r.update(patch)
 raw=dumps(r);hide=row['is_hidden'] if hidden is None else hidden;canon=row['canonical_entry_id'] if canonical==-1 else canonical
 if raw==dumps(clean(row)) and hide==row['is_hidden'] and canon==row['canonical_entry_id'] and metadata_score(r)==row['metadata_score']:return True
 _,count=run(db,'UPDATE inventory_entries SET raw_record=%s,record_sha256=%s,status=%s,category=%s,description=%s,source_url=%s,image_url=%s,resolution=%s,is_hidden=%s,canonical_entry_id=%s,metadata_score=%s WHERE id=%s AND revision=%s',(raw,sha(raw.encode()),str(r.get('status','')),r.get('category'),r.get('description'),r.get('source'),r.get('url'),r.get('resolution'),hide,canon,metadata_score(r),eid,row['revision']))
 if count:run(db,'INSERT INTO inventory_entry_history(entry_id,revision,action,previous_record,new_record,actor) VALUES(%s,%s,%s,%s,%s,%s)',(eid,row['revision']+1,action,row['raw_record'],raw,'Database worker'))
 return count==1
def path_category(relative,owner):
 # Category directories may appear beneath site folders or other grouping paths.
 for part in reversed(Path(file_name(relative,owner)).parts[:-1]):
  if part.casefold() in {'sfw','sketchy','nsfw','not_provided'}:return part.casefold()
 return None

def discover_collection_folders():
 require_root()
 for folder,dirs,files in os.walk(ROOT,onerror=walk_error):
  dirs[:]=[d for d in dirs if not (Path(folder)/d).is_symlink() and not getattr((Path(folder)/d).stat(),'st_file_attributes',0)&1024]
  marker=next((d for d in dirs if d.casefold()=='_download-info'),None)
  if marker is None:continue
  dirs.remove(marker)
  yield Path(folder),Path(folder)/marker


def discover():
 # An inventory is an optional input, not the collection boundary.
 for folder,info in discover_collection_folders():
  p=info/'inventory.json'
  if not p.exists():
   choices=list(info.glob('*inventory.json'))
   if len(choices)!=1:continue
   p=choices[0]
  yield p

def register_collection_folders(db):
 # Database documents remain internal record containers; no JSON file is created.
 folders=list(discover_collection_folders())
 for folder,info in folders:
  key=folder.relative_to(ROOT).as_posix();cid=collection(db,key)
  if q(db,'SELECT id FROM inventory_documents WHERE collection_id=%s',(cid,)):continue
  relative=(info/'inventory.json').relative_to(ROOT).as_posix()
  run(db,"INSERT IGNORE INTO inventory_documents(collection_id,relative_path,path_key,original_content,import_state) VALUES(%s,%s,%s,'[]','ready')",(cid,relative,pathkey(relative)))

 state(db,'collection_markers',{'collections':sorted(folder.relative_to(ROOT).as_posix() for folder,info in folders)})
 FM.discover_markers(db,[folder.relative_to(ROOT).as_posix() for folder,info in folders])

def marker_keys(db):return set(state(db,'collection_markers').get('collections',[]))

def archive_document(db,cid,relative,data,st,kind):
 if kind in ('inventory_input','source_report'):return
 text=data.decode('utf-8-sig');obj=decode(text) if relative.endswith('.json') or kind=='inventory_input' else None
 run(db,'INSERT INTO collection_documents(collection_id,relative_path,path_key,document_kind,content_sha256,byte_size,modified_utc_ticks,json_content,text_content,imported_at) VALUES(%s,%s,%s,%s,%s,%s,%s,%s,%s,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE content_sha256=VALUES(content_sha256),byte_size=VALUES(byte_size),modified_utc_ticks=VALUES(modified_utc_ticks),json_content=VALUES(json_content),text_content=VALUES(text_content),imported_at=UTC_TIMESTAMP(6)',(cid,relative,pathkey(relative),kind,sha(data),len(data),ticks(st),dumps(obj) if obj is not None else None,text))
def closest_other_collection(source,collections):
 parts=source.split('/') if source!='.' else []
 if parts and parts[-1].casefold()=='other':return source
 candidates=['/'.join(parts[:level]+['other']) for level in range(max(0,len(parts)-1),-1,-1)]
 known={key.casefold():key for key in collections if key.split('/')[-1].casefold()=='other'}
 return next((known[key.casefold()] for key in candidates if key.casefold() in known),None)

def route_excluded_unrelated(db,commit=True):
 active=marker_keys(db);moved=0;checked={};destinations={}
 rows=q(db,"SELECT e.id,e.inventory_document_id,e.raw_record,c.collection_key FROM inventory_entries e JOIN inventory_documents d ON d.id=e.inventory_document_id JOIN collections c ON c.id=e.collection_id WHERE e.status='excluded_unrelated' AND e.deleted_at IS NULL")
 if not rows:return 0
 if commit:db.begin()
 try:
  for row in rows:
   source=row['collection_key']
   if source not in active:continue
   target=closest_other_collection(source,active)
   if not target or target==source:continue
   info=safe(target)/'_download-info'
   if target not in checked:checked[target]=info.is_dir() and any(child.name.casefold()=='other' and child.is_dir() for child in info.parent.parent.iterdir())
   if not checked[target]:continue
   if target not in destinations:
    cid=collection(db,target);documents=q(db,'SELECT id FROM inventory_documents WHERE collection_id=%s',(cid,))
    if documents:doc=documents[0]['id']
    else:
     relative=target+'/_download-info/inventory.json'
     doc=run(db,"INSERT INTO inventory_documents(collection_id,relative_path,path_key,original_content,import_state) VALUES(%s,%s,%s,'[]','ready')",(cid,relative,pathkey(relative)))[0]
    destinations[target]=doc
   doc=destinations[target]
   def patch(r):
    r['origin_collection']=r.get('origin_collection') or source;r['download_collection']=target
    for field,owner in [('file','file_collection'),('duplicate_of','duplicate_collection')]:
     if r.get(field) and not r.get(owner):r[owner]=source
    for field in ['related_image_references','saved_image_references']:
     for ref in r.get(field,[]) or []:
      if isinstance(ref,dict) and not ref.get('collection'):ref['collection']=source
    if r.get('related_image_files'):
     r.setdefault('related_image_references',[]).extend({'collection':source,'file':name} for name in r.pop('related_image_files') if isinstance(name,str))
   if not update_entry(db,row['id'],patch,'excluded_unrelated_routed'):raise RuntimeError('Entry changed during collection routing; retrying next pass.')
   ordinal=q(db,'SELECT COALESCE(MAX(source_ordinal),-1)+1 AS n FROM inventory_entries WHERE inventory_document_id=%s',(doc,))[0]['n']
   run(db,'UPDATE inventory_entries SET collection_id=(SELECT collection_id FROM inventory_documents WHERE id=%s) WHERE id=%s',(doc,row['id']))
   updated=q(db,'SELECT revision,raw_record FROM inventory_entries WHERE id=%s',(row['id'],))[0]
   run(db,"INSERT INTO inventory_entry_history(entry_id,revision,action,new_record,actor) VALUES(%s,%s,'collection_reassigned',%s,'Database worker')",(row['id'],updated['revision'],updated['raw_record']))
   active.add(target);moved+=1
  state(db,'collection_markers',{'collections':sorted(active)})
  if commit:db.commit()
 except Exception:
  if commit:db.rollback()
  raise
 return moved

def import_documents(db,allow_updates=False):
 imported=0;pending=[]
 invalid=[]
 for p in discover():
  known=[]
  try:
   before=p.stat();data=p.read_bytes();after=p.stat()
   if before.st_size!=after.st_size or ticks(before)!=ticks(after) or time.time()-after.st_mtime<3:continue
   relative=p.relative_to(ROOT).as_posix();key=p.parent.parent.relative_to(ROOT).as_posix();known=q(db,'SELECT * FROM inventory_documents WHERE path_key=%s',(pathkey(relative),))
   if not known:known=q(db,'SELECT d.* FROM inventory_documents d JOIN collections c ON c.id=d.collection_id WHERE c.collection_key=%s',(key,))
   if known and bytes(known[0]['source_sha256'] or b'')==sha(data):
    if known[0]['relative_path']!=relative:run(db,'UPDATE inventory_documents SET relative_path=%s,path_key=%s WHERE id=%s',(relative,pathkey(relative),known[0]['id']))
    if known[0]['import_error'] is not None:run(db,"UPDATE inventory_documents SET import_error=NULL,pending_source_sha256=NULL,import_state='ready' WHERE id=%s",(known[0]['id'],))
    if known[0]['original_content'] is not None:run(db,'UPDATE inventory_documents SET original_content=NULL WHERE id=%s',(known[0]['id'],))
    continue
   if known and known[0]['source_sha256'] is not None and not allow_updates:
    run(db,'UPDATE inventory_documents SET pending_source_sha256=%s,import_error=%s WHERE id=%s',(sha(data),'Input inventory changed; explicit import requested before applying updates.',known[0]['id']));pending.append(key);continue
   rows=json.loads(data.decode('utf-8-sig'))
   if not isinstance(rows,list) or not all(isinstance(r,dict) for r in rows):raise ValueError('Inventory must contain an array of objects: '+relative)
   progress(db,'Importing '+key,0,len(rows));db.begin()
   try:
    cid=collection(db,key)
    if known:doc=known[0]['id']
    else:doc=run(db,'INSERT INTO inventory_documents(collection_id,relative_path,path_key,import_state) VALUES(%s,%s,%s,%s)',(cid,relative,pathkey(relative),'importing'))[0]
    existing=q(db,'SELECT * FROM inventory_entries WHERE inventory_document_id=%s ORDER BY source_ordinal',(doc,));identities=defaultdict(list)
    def identity(r):return (str(r.get('site','')),str(r.get('id','')),str(r.get('record_type','')),str(r.get('url','')),str(r.get('variant','')))
    for e in existing:identities[identity(clean(e))].append(e)
    ordinal=max([e['source_ordinal'] for e in existing]+[-1])+1
    for routed in q(db,"SELECT * FROM inventory_entries WHERE JSON_UNQUOTE(JSON_EXTRACT(raw_record,'$.origin_collection'))=%s AND inventory_document_id<>%s",(key,doc)):
     identities[identity(clean(routed))].append(routed)
    for i,r in enumerate(rows):
     raw=dumps(r);matches=identities.get(identity(r),[])
     if matches:
      e=matches.pop(0)
      if e['deleted_at'] is not None:continue
      def merge(old):
       for k,v in r.items():
        if k in ['status','file','file_collection','duplicate_of','duplicate_collection','related_image_references','related_image_files','saved_image_references','duplicate_record_of','duplicate_record_site','duplicate_record_collection','sha256','file_size','bytes','download_collection','origin_collection'] and old.get(k):continue
        if v is not None:
         if k=='category':old.pop('duplicate_source_category',None)
         old[k]=v
      update_entry(db,e['id'],merge,'inventory_update')
     else:
      eid=run(db,'INSERT INTO inventory_entries(inventory_document_id,source_ordinal,site,source_id,record_type,status,category,description,source_url,image_url,resolution,raw_record,record_sha256,is_hidden,metadata_score) VALUES(%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)',(doc,ordinal if known else i,str(r.get('site','')),str(r.get('id','')),str(r.get('record_type','')),str(r.get('status','')),r.get('category'),r.get('description'),r.get('source'),r.get('url'),r.get('resolution'),raw,sha(raw.encode()),r.get('status')=='duplicate',metadata_score(r)))[0]
      run(db,'INSERT INTO inventory_entry_history(entry_id,revision,action,new_record,actor) VALUES(%s,1,%s,%s,%s)',(eid,'import',raw,'Database worker'));ordinal+=1
    run(db,"UPDATE inventory_documents SET relative_path=%s,path_key=%s,source_sha256=%s,source_byte_size=%s,source_modified_utc_ticks=%s,original_content=NULL,imported_at=UTC_TIMESTAMP(6),import_state='organizing',import_error=NULL,pending_source_sha256=NULL WHERE id=%s",(relative,pathkey(relative),sha(data),len(data),ticks(after),doc))
    archive_document(db,cid,relative+'#'+sha(data).hex(),data,after,'inventory_input');db.commit();imported+=1
   except Exception:db.rollback();raise
  except Exception as error:
   db.rollback();relative=p.relative_to(ROOT).as_posix();key=p.parent.parent.relative_to(ROOT).as_posix();message=str(error)
   try:
    cid=collection(db,key)
    if not known:known=q(db,'SELECT * FROM inventory_documents WHERE collection_id=%s',(cid,))
    if known:run(db,"UPDATE inventory_documents SET import_state='error',import_error=%s WHERE id=%s",(message,known[0]['id']))
    else:run(db,"INSERT INTO inventory_documents(collection_id,relative_path,path_key,import_state,import_error) VALUES(%s,%s,%s,'error',%s)",(cid,relative,pathkey(relative),message))
   except Exception:pass
   invalid.append({'inventory':relative,'error':message})
 state(db,'invalid_documents',{'errors':invalid})
 return imported,pending
def all_entries(db):return q(db,'SELECT e.*,c.collection_key FROM inventory_entries e JOIN inventory_documents d ON d.id=e.inventory_document_id JOIN collections c ON c.id=e.collection_id ORDER BY e.id')
def record_refs(r,owner):
 refs=[]
 for field,kind,cf in [('file','primary','file_collection'),('duplicate_of','duplicate','duplicate_collection')]:
  if isinstance(r.get(field),str) and r[field]:refs.append((kind,r.get(cf) or owner,r[field]))
 for field,kind in [('related_image_references','related'),('saved_image_references','saved')]:
  for ref in r.get(field,[]) or []:
   if isinstance(ref,dict) and ref.get('file'):refs.append((kind,ref.get('collection') or owner,ref['file']))
 for name in r.get('related_image_files',[]) or []:
  if isinstance(name,str):refs.append(('related',owner,name))
 return list(dict.fromkeys(refs))
def filehash(path):
 h=hashlib.sha256()
 with path.open('rb') as f:
  for part in iter(lambda:f.read(1024*1024),b''):h.update(part)
 return h.digest()
def image_file_stats():
 require_root()
 pending=[ROOT]
 while pending:
  with os.scandir(pending.pop()) as entries:
   for entry in entries:
    if entry.is_symlink():continue
    st=entry.stat(follow_symlinks=False)
    if getattr(st,'st_file_attributes',0)&1024:continue
    path=Path(entry.path)
    if entry.is_dir(follow_symlinks=False):
     if entry.name!='_download-info':pending.append(path)
    elif entry.is_file(follow_symlinks=False) and path.suffix.lower() in EXT:yield path,st

def image_files():
 for path,st in image_file_stats():yield path
def index_files(db):
 require_root()
 active=marker_keys(db);owners=[c for c in q(db,'SELECT c.id,c.collection_key FROM collections c JOIN inventory_documents d ON d.collection_id=c.id') if c['collection_key'] in active];owners.sort(key=lambda c:len(c['collection_key']),reverse=True)
 known={bytes(r['path_key']):r for r in q(db,'SELECT f.id,f.path_key,f.canonical_file_id,f.byte_size,f.modified_utc_ticks,f.is_present,f.collection_id,c.collection_key AS owner FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id')};found=[]
 for path,st in image_file_stats():
  if not st.st_size or time.time()-st.st_mtime<3:continue
  relative=path.relative_to(ROOT).as_posix();owner=next((c for c in owners if (c['collection_key']=='.' or relative.startswith(c['collection_key']+'/'))),None)
  if not owner:
   # Unclaimed images outside a marked collection are left untouched and unimported.
   found.append(relative);continue
  key=pathkey(relative)
  if key in known and known[key]['canonical_file_id']:
   # A user can put a removed duplicate back, or replace that pathname with new content.
   # Re-index the actual bytes; historical duplicate decisions must not hide a live file.
   run(db,'UPDATE files SET canonical_file_id=NULL,is_present=TRUE,byte_size=%s,modified_utc_ticks=%s,last_seen_at=UTC_TIMESTAMP(6) WHERE id=%s',(st.st_size,ticks(st),known[key]['id']))
  if key in known and known[key]['collection_id']!=owner['id']:
   previous=known[key];source={'id':previous['id'],'owner':previous['owner'],'relative_path':relative};target={'id':previous['id'],'owner':owner['collection_key'],'relative_path':relative,'byte_size':st.st_size}
   rewrite_path(db,source,target)
  if key not in known or not known[key]['is_present'] or known[key]['byte_size']!=st.st_size or known[key]['modified_utc_ticks']!=ticks(st):run(db,'INSERT INTO files(relative_path,path_key,byte_size,modified_utc_ticks,is_present,last_seen_at) VALUES(%s,%s,%s,%s,TRUE,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE byte_size=VALUES(byte_size),modified_utc_ticks=VALUES(modified_utc_ticks),is_present=TRUE,last_seen_at=UTC_TIMESTAMP(6)',(relative,key,st.st_size,ticks(st)))
  found.append(relative)
 # A missing physical path stays catalogued, preserving its history and references.
 require_root()
 present={pathkey(name) for name in found}
 for f in q(db,'SELECT id,path_key FROM fm_file_catalog WHERE is_present=TRUE AND canonical_file_id IS NULL'):
  if bytes(f['path_key']) not in present:run(db,'UPDATE files SET is_present=FALSE WHERE id=%s',(f['id'],))
 return found
def normalize_links(db):
 for e in all_entries(db):
  if not e['deleted_at']:FM.index_entry(db,e)
 return all_entries(db)

def rewrite_path(db,source,target,source_stream=None,target_stream=None):
 if not FMW.can_manage(db,source['relative_path'])[0] or not FMW.can_manage(db,target['relative_path'])[0]:raise RuntimeError('External file database is not ready; original retained')
 FMW.prepare_sync(db,source,target,ROOT,source_stream=source_stream,target_stream=target_stream)
 with FM.asset_write_lock:
  db.begin()
  try:
   # All asset/index writers enter this transaction in the same order.
   if source['id']!=target['id']:run(db,'UPDATE inventory_entries SET file_id=%s WHERE file_id=%s',(target['id'],source['id']))
   if source['id']==target['id']:
    run(db,'UPDATE files SET relative_path=%s,path_key=%s,file_name=%s WHERE id=%s',(target['relative_path'],pathkey(target['relative_path']),Path(target['relative_path']).name,source['id']))
   else:
    run(db,'UPDATE files SET canonical_file_id=%s WHERE canonical_file_id=%s',(target['id'],source['id']))
    run(db,'UPDATE files SET canonical_file_id=%s,is_present=FALSE WHERE id=%s',(target['id'],source['id']))
   FMW.synchronize_paths(db,source,target,ROOT)
   db.commit()
  except Exception:db.rollback();raise

def protected(path,delete_access=False):
 import msvcrt
 kernel=ctypes.WinDLL('kernel32',use_last_error=True);kernel.CreateFileW.restype=ctypes.c_void_p;kernel.CreateFileW.argtypes=[ctypes.c_wchar_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p]
 handle=kernel.CreateFileW(str(path),0x80000000|(0x10000 if delete_access else 0),1,None,3,0,None)
 if handle==ctypes.c_void_p(-1).value:raise OSError(ctypes.get_last_error(),'Image is in use: '+str(path))
 return os.fdopen(msvcrt.open_osfhandle(handle,os.O_RDONLY|os.O_BINARY),'rb')
def hashstream(stream):
 stream.seek(0);h=hashlib.sha256()
 for part in iter(lambda:stream.read(1024*1024),b''):h.update(part)
 stream.seek(0);return h.digest()
def delete_protected(stream):
 import msvcrt
 kernel=ctypes.WinDLL('kernel32',use_last_error=True);flag=ctypes.c_int(1)
 if not kernel.SetFileInformationByHandle(ctypes.c_void_p(msvcrt.get_osfhandle(stream.fileno())),4,ctypes.byref(flag),ctypes.sizeof(flag)):raise OSError(ctypes.get_last_error(),'Unable to remove protected duplicate')

def backup_folder(operation_id,byte_size):
 settings=ET.parse(BASE/'iis-site/web.config').getroot().find('appSettings')
 configured=next((item.get('value') for item in settings if item.get('key')=='GalleryBackupRoot'),None)
 root=Path(configured) if configured else BASE/'change-backups/mysql-dedupe'
 try:root.resolve().relative_to(ROOT.resolve())
 except ValueError:pass
 else:raise ValueError('Backup storage must be outside the wallpaper library')
 root.mkdir(parents=True,exist_ok=True)
 if shutil.disk_usage(root).free<byte_size+1024*1024*1024:raise OSError('Backup storage is nearly full; original file retained')
 folder=root/str(operation_id);folder.mkdir(parents=True,exist_ok=True);return folder

def retire_duplicate_backups(db,source_id,target_id,source_digest,target_digest):
 """Remove old duplicate backups only after one verified library copy and all links survive."""
 ops=q(db,"SELECT id,state,source_path,target_path,source_sha256,target_sha256 FROM file_operations WHERE operation_kind='deduplicate' AND source_file_id=%s AND target_file_id=%s",(source_id,target_id))
 if not ops or any(o['state'] not in ('complete','cancelled') or bytes(o['source_sha256'])!=source_digest or bytes(o['target_sha256'])!=target_digest for o in ops):return
 source=q(db,'SELECT canonical_file_id,is_present FROM files WHERE id=%s',(source_id,))
 target=q(db,'SELECT relative_path,is_present,canonical_file_id FROM files WHERE id=%s',(target_id,))
 if not source or source[0]['canonical_file_id']!=target_id or source[0]['is_present']:return
 if not target or not target[0]['is_present'] or target[0]['canonical_file_id'] or not target[0]['relative_path']:return
 if q(db,'SELECT id FROM inventory_entries WHERE file_id=%s LIMIT 1',(source_id,)) or q(db,'SELECT file_id FROM fm_external_bindings WHERE file_id=%s LIMIT 1',(source_id,)):return
 source_paths={o['source_path'] for o in ops if o['source_path']}
 target_paths={o['target_path'] for o in ops if o['target_path']}
 if any(safe(path).exists() for path in source_paths):return
 target_paths.add(target[0]['relative_path'])
 if not any(safe(path).is_file() and filehash(safe(path))==target_digest for path in target_paths):return
 settings=ET.parse(BASE/'iis-site/web.config').getroot().find('appSettings')
 configured=next((item.get('value') for item in settings if item.get('key')=='GalleryBackupRoot'),None)
 root=Path(configured) if configured else BASE/'change-backups/mysql-dedupe'
 if not root.is_dir() or root.is_symlink():return
 for op in ops:
  folder=root/str(op['id'])
  if not folder.is_dir() or folder.is_symlink():continue
  files=list(folder.iterdir())
  if any(not f.is_file() or f.is_symlink() or filehash(f)!=source_digest for f in files):continue
  for f in files:f.unlink()
  folder.rmdir()
def nearest_collection(relative,active):
 path=relative.replace('\\','/').casefold()
 return next((key for key in sorted(active,key=len,reverse=True) if key=='.' or path.startswith(key.casefold()+'/')),None)

def collection_assignment(row,refs,active):
 r=decode(row['raw_record'])
 # Explicit routing takes precedence over physical location.
 if r.get('download_collection') or r.get('collection_override') or row.get('status')=='excluded_unrelated':return None
 primary=[f for f in refs if f['reference_kind']=='primary']
 candidates=primary or [f for f in refs if f['reference_kind'] in {'contains','related','saved'}]
 owners={nearest_collection(f['relative_path'],active) for f in candidates}
 owners.discard(None)
 return next(iter(owners)) if len(owners)==1 else None

def refresh_manual_categories(db):
 active=marker_keys(db);links=defaultdict(list);moved=0
 for f in q(db,"SELECT l.entry_id,l.reference_kind,f.relative_path FROM fm_entry_file_projection l JOIN fm_file_catalog f ON f.id=l.file_id WHERE f.is_present=TRUE AND f.canonical_file_id IS NULL"):
  links[f['entry_id']].append(f)
 docs={r['collection_key']:r['id'] for r in q(db,'SELECT d.id,c.collection_key FROM inventory_documents d JOIN collections c ON c.id=d.collection_id ORDER BY d.id DESC') if r['collection_key'] in active}
 for row in all_entries(db):
  if row['deleted_at']:continue
  refs=links[row['id']];target=collection_assignment(row,refs,active)
  if not target or target not in docs:continue
  source=row['collection_key'];r=clean(row)
  if r.get('manual_import'):
   primary=next((f for f in refs if f['reference_kind']=='primary'),None)
   category=path_category(primary['relative_path'],target) if primary else None
   if category and category!=row['category']:update_entry(db,row['id'],{'category':category,'duplicate_source_category':category},'manual_folder_category')
  if source==target:continue
  db.begin()
  try:
   current=q(db,'SELECT inventory_document_id,raw_record,status FROM inventory_entries WHERE id=%s FOR UPDATE',(row['id'],))[0]
   if current['inventory_document_id']!=row['inventory_document_id'] or collection_assignment(current,refs,active)!=target:raise RuntimeError('Entry changed during collection assignment; retry next pass.')
   def patch(record):
    record['origin_collection']=record.get('origin_collection') or source
    for field,owner in [('file','file_collection'),('duplicate_of','duplicate_collection')]:
     if record.get(field) and not record.get(owner):record[owner]=source
    for field in ['related_image_references','saved_image_references']:
     for ref in record.get(field,[]) or []:
      if isinstance(ref,dict) and not ref.get('collection'):ref['collection']=source
    if record.get('related_image_files'):
     record.setdefault('related_image_references',[]).extend({'collection':source,'file':name} for name in record.pop('related_image_files') if isinstance(name,str))
   if not update_entry(db,row['id'],patch,'nearest_collection_assigned'):raise RuntimeError('Entry changed during collection assignment.')
   doc=docs[target];ordinal=q(db,'SELECT COALESCE(MAX(source_ordinal),-1)+1 AS n FROM inventory_entries WHERE inventory_document_id=%s',(doc,))[0]['n']
   run(db,'UPDATE inventory_entries SET collection_id=(SELECT collection_id FROM inventory_documents WHERE id=%s) WHERE id=%s',(doc,row['id']))
   updated=q(db,'SELECT revision,raw_record FROM inventory_entries WHERE id=%s',(row['id'],))[0]
   run(db,"INSERT INTO inventory_entry_history(entry_id,revision,action,new_record,actor) VALUES(%s,%s,'collection_reassigned',%s,'Database worker')",(row['id'],updated['revision'],updated['raw_record']))
   db.commit();moved+=1
  except Exception:db.rollback();raise
 return moved


def organize(db,selected_ids=None):
 active=marker_keys(db)
 files=q(db,'SELECT f.*,c.collection_key AS owner FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id WHERE f.is_present=TRUE AND f.canonical_file_id IS NULL'+(' AND f.id IN ('+','.join(['%s']*len(selected_ids))+')' if selected_ids else ''),selected_ids or ())
 for f in files:
  if Path(f['relative_path']).suffix.casefold()=='.swf':continue
  if f['owner'] not in active or path_category(f['relative_path'],f['owner']) is not None:continue
  if not FMW.can_manage(db,f['relative_path'],f['id'])[0]:continue
  cats=[str(x['category'] or '').lower() for x in q(db,'SELECT DISTINCT e.category FROM fm_entry_file_projection l JOIN inventory_entries e ON e.id=l.entry_id WHERE l.file_id=%s AND e.deleted_at IS NULL',(f['id'],))]
  chosen=next((c for c in ['nsfw','sketchy','sfw'] if c in cats),'not_provided')
  if not cats:
   previous=Path(file_name(f['relative_path'],f['owner'])).parts[0].lower()
   if previous in ['sfw','sketchy','nsfw']:chosen=previous
  name=Path(f['relative_path']).name;relative=Path(f['owner'],chosen,name).as_posix()
  if relative==f['relative_path']:continue
  allowed,reason=FMW.can_relocate(db,f['id'],relative)
  if not allowed:
   run(db,'INSERT INTO fm_deferred_files(file_id,reason) VALUES(%s,%s) ON DUPLICATE KEY UPDATE reason=VALUES(reason)',(f['id'],reason));continue
  source=safe(f['relative_path']);target=safe(relative)
  if not source.exists():continue
  digest=filehash(source)
  if target.exists() or q(db,'SELECT id FROM fm_file_catalog WHERE path_key=%s',(pathkey(relative),)):
   relative=Path(f['owner'],chosen,Path(name).stem+'-'+digest.hex()[:12]+Path(name).suffix).as_posix();target=safe(relative)
   if target.exists():raise RuntimeError('Organization destination requires review: '+relative)
  op=run(db,"INSERT INTO file_operations(operation_kind,source_file_id,target_file_id,source_path,target_path,source_sha256,target_sha256) VALUES('organize',%s,%s,%s,%s,%s,%s)",(f['id'],f['id'],f['relative_path'],relative,digest,digest))[0]
  try:
   target.parent.mkdir(parents=True,exist_ok=True)
   with protected(source,True) as original:
    if hashstream(original)!=digest:raise RuntimeError('Source changed during organization')
    with target.open('xb') as dest:shutil.copyfileobj(original,dest)
    shutil.copystat(source,target)
    with protected(target) as retained:
     if hashstream(retained)!=digest:raise RuntimeError('Move verification failed')
     run(db,"UPDATE file_operations SET state='copied' WHERE id=%s",(op,));new=dict(f,relative_path=relative)
     rewrite_path(db,f,new,source_stream=original,target_stream=retained)
     run(db,'UPDATE files SET relative_path=%s,path_key=%s WHERE id=%s',(relative,pathkey(relative),f['id']))
     run(db,"UPDATE file_operations SET state='linked' WHERE id=%s",(op,));delete_protected(original)
   run(db,"UPDATE file_operations SET state='complete',completed_at=UTC_TIMESTAMP(6) WHERE id=%s",(op,))
  except Exception as err:run(db,"UPDATE file_operations SET state='error',error=%s WHERE id=%s",(str(err),op));raise
def flash_info(path):
 before=path.stat()
 with path.open('rb') as stream:header=stream.read(8)
 if len(header)<8 or header[:3] not in (b'FWS',b'CWS',b'ZWS') or int.from_bytes(header[4:8],'little')<8:raise ValueError('Invalid SWF header')
 digest=filehash(path);after=path.stat()
 if ticks(before)!=ticks(after) or before.st_size!=after.st_size:raise RuntimeError('Flash file changed during indexing')
 return {'digest':digest,'bytes':after.st_size,'ticks':ticks(after),'version':header[3],'compression':header[:3].decode('ascii')}

def index_flash_files(db):
 active=marker_keys(db);errors=[]
 for f in q(db,"SELECT f.*,c.collection_key AS owner FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id WHERE f.is_present=TRUE AND f.canonical_file_id IS NULL"):
  if f['owner'] not in active or Path(f['relative_path']).suffix.casefold()!='.swf':continue
  try:
   info=flash_info(safe(f['relative_path']));meta={'media_type':'flash','swf_version':info['version'],'compression':info['compression']}
   run(db,"UPDATE files SET width=0,height=0,image_format='SWF',frame_count=1,metadata_score=0,embedded_metadata=%s WHERE id=%s",(dumps(meta),f['id']))
   run(db,"INSERT INTO file_hashes(file_id,hash_kind,algorithm,algorithm_version,hash_value,hash_bits,source_byte_size,source_modified_utc_ticks,parameters,computed_at) VALUES(%s,'file_sha256','sha256','1',%s,256,%s,%s,%s,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE hash_value=VALUES(hash_value),source_byte_size=VALUES(source_byte_size),source_modified_utc_ticks=VALUES(source_modified_utc_ticks),parameters=VALUES(parameters)",(f['id'],info['digest'],info['bytes'],info['ticks'],dumps({'media_type':'flash','visual_comparison':False})))
  except Exception as error:errors.append({'file':f['relative_path'],'error':str(error)})
 return errors

def compute(path):
 before=path.stat();result={'bytes':before.st_size,'ticks':ticks(before),'file_sha256':filehash(path)}
 with Image.open(path) as image:
  result.update(width=image.width,height=image.height,format=image.format,frames=getattr(image,'n_frames',1));meta=extract_metadata(image)
  result['metadata']=meta;result['metadata_score']=embedded_score(meta)
  if result['frames']>1:meta['animation_durations_ms']=[];meta['animation_loop']=image.info.get('loop',0)
  pixel=hashlib.sha256(b'wallpapers-rgba-frames-v1\0');pixel.update(struct.pack('>I',result['frames']))
  for frame in range(result['frames']):
   image.seek(frame)
   if result['frames']>1:meta['animation_durations_ms'].append(image.info.get('duration',0))
   oriented=ImageOps.exif_transpose(image);rgba=oriented.convert('RGBA');pixel.update(struct.pack('>II',rgba.width,rgba.height))
   for top in range(0,rgba.height,128):pixel.update(rgba.crop((0,top,rgba.width,min(top+128,rgba.height))).tobytes())
   if frame==0:
    small=rgba.convert('L').resize((9,8),Image.Resampling.LANCZOS);samples=list(small.get_flattened_data());dh=0
    for y in range(8):
     for x in range(8):dh=(dh<<1)|int(samples[y*9+x]>samples[y*9+x+1])
    result.update(visual_hash=dh.to_bytes(8,'big'),display_width=rgba.width,display_height=rgba.height)
   rgba.close();oriented.close()
  result['pixel_sha256']=pixel.digest()
 after=path.stat()
 if ticks(after)!=result['ticks'] or after.st_size!=result['bytes']:raise RuntimeError('Image changed during decoding')
 return result
def extract_metadata(image):
 meta={'_gallery_extractor':2}
 for tag,value in image.getexif().items():
  if value is not None and not isinstance(value,bytes):meta[ExifTags.TAGS.get(tag,str(tag))]=str(value)[:32768]
 for field,value in image.info.items():
  if value is None or field in ['exif','icc_profile']:continue
  if isinstance(value,bytes):
   try:meta[field]=value.decode('utf-8')[:32768]
   except UnicodeDecodeError:meta[field]={'sha256':sha(value).hex(),'bytes':len(value)}
  elif isinstance(value,(str,int,float,list,tuple)):meta[field]=str(value)[:32768]
 icc=image.info.get('icc_profile')
 if icc:meta['icc_profile_sha256']=sha(icc).hex();meta['icc_profile_bytes']=len(icc)
 return meta
def embedded_score(meta):
 ignored={'ExifOffset','Orientation','ResolutionUnit','XResolution','YResolution','icc_profile_bytes','dpi','jfif','jfif_version','jfif_unit','jfif_density','progressive','progression','interlace'}
 return sum(min(len(str(v)),1000) for k,v in meta.items() if k not in ignored and not k.startswith('_'))
def header_metadata(path):
 with Image.open(path) as image:return extract_metadata(image)
def record_decode_error(db,f,error,errors):
 if isinstance(error,pymysql.MySQLError):raise error
 errors.append({'file':f['relative_path'],'error':str(error)})
 try:
  path=safe(f['relative_path']);before=path.stat();digest=filehash(path);after=path.stat()
  if before.st_size==after.st_size==f['byte_size'] and ticks(before)==ticks(after)==f['modified_utc_ticks']:
   run(db,"INSERT INTO file_hashes(file_id,hash_kind,algorithm,algorithm_version,hash_value,hash_bits,source_byte_size,source_modified_utc_ticks,parameters,computed_at) VALUES(%s,'file_sha256','sha256','1',%s,256,%s,%s,%s,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE hash_value=VALUES(hash_value),source_byte_size=VALUES(source_byte_size),source_modified_utc_ticks=VALUES(source_modified_utc_ticks),parameters=VALUES(parameters),computed_at=UTC_TIMESTAMP(6)",(f['id'],digest,after.st_size,ticks(after),dumps({'decode_error':str(error),'compared_automatically':False})))
 except OSError:pass

def hash_files(db,scan,selected_ids=None):
 files=q(db,'SELECT f.*,c.collection_key AS owner FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id WHERE f.is_present=TRUE AND f.canonical_file_id IS NULL'+(' AND f.id IN ('+','.join(['%s']*len(selected_ids))+')' if selected_ids else '')+' ORDER BY f.id',selected_ids or ());active=marker_keys(db);files=[f for f in files if f['owner'] in active and Path(f['relative_path']).suffix.casefold()!='.swf'];results=[];hits=0;errors=[];pending=[]
 allhashes=defaultdict(dict)
 for h in q(db,"SELECT * FROM file_hashes WHERE "+("file_id IN ("+",".join(str(int(i)) for i in selected_ids)+") AND " if selected_ids else "")+"algorithm_version='1' AND ((hash_kind='file_sha256' AND algorithm='sha256') OR (hash_kind='pixel_sha256' AND algorithm='pillow-rgba-frames-sha256') OR (hash_kind='perceptual' AND algorithm='dhash64'))"):allhashes[h['file_id']][h['hash_kind']]=h
 def flush():
  if not pending:return
  db.begin()
  try:
   metadata=[];hashes=[];ids=[]
   for f,result,is_new in pending:
    ids.append(f['id'])
    if 'metadata' in result:
     metadata.append((result.get('width',f['width']),result.get('height',f['height']),result.get('format',f['image_format']),result.get('frames',f['frame_count']),result['metadata_score'],dumps(result['metadata']),f['id']))
    if is_new:
     for kind,algorithm,field,bits in [('file_sha256','sha256','file_sha256',256),('pixel_sha256','pillow-rgba-frames-sha256','pixel_sha256',256),('perceptual','dhash64','visual_hash',64)]:
      params={'decoder':'Pillow '+PILLOW,'display_width':result['display_width'],'display_height':result['display_height'],'orientation':'EXIF transpose','frames':'all; first frame only for visual hash','color':'RGBA8, ICC not transformed','animation_timing':'compared separately','embedded_metadata_sha256':sha(dumps(result['metadata']).encode()).hex()}
      hashes.append((f['id'],kind,algorithm,'1',result[field],bits,result['bytes'],result['ticks'],dumps(params),now()))
   with db.cursor() as cursor:
    if metadata:cursor.executemany('UPDATE files SET width=%s,height=%s,image_format=%s,frame_count=%s,metadata_score=%s,embedded_metadata=%s WHERE id=%s',metadata)
    if hashes:cursor.executemany('INSERT INTO file_hashes(file_id,hash_kind,algorithm,algorithm_version,hash_value,hash_bits,source_byte_size,source_modified_utc_ticks,parameters,computed_at) VALUES(%s,%s,%s,%s,%s,%s,%s,%s,%s,%s) ON DUPLICATE KEY UPDATE hash_value=VALUES(hash_value),source_byte_size=VALUES(source_byte_size),source_modified_utc_ticks=VALUES(source_modified_utc_ticks),parameters=VALUES(parameters),computed_at=VALUES(computed_at)',hashes)
    cursor.execute('UPDATE files SET last_scan_id=%s WHERE id IN ('+','.join(['%s']*len(ids))+')',[scan,*ids])
   db.commit();pending.clear()
  except Exception:db.rollback();raise
  run(db,'UPDATE scan_runs SET files_seen=%s,files_processed=%s,cache_hits=%s,errors=%s WHERE id=%s',(len(files),len(results),hits,len(errors),scan));progress(db,'Hashing images',len(results)+len(errors),len(files),scan_id=scan)
 def accept(f,result,is_new):
  f.update(result);results.append(f);pending.append((f,result,is_new))
  if len(pending)>=25:flush()
 with ThreadPoolExecutor(max_workers=2) as pool:
  futures={};cached=[]
  for f in files:
   h={k:v for k,v in allhashes[f['id']].items() if v['source_byte_size']==f['byte_size'] and v['source_modified_utc_ticks']==f['modified_utc_ticks']}
   if len(h)==3:cached.append((f,h))
   else:futures[pool.submit(compute,safe(f['relative_path']))]=f
  for f,h in cached:
   try:
    params=decode(h['pixel_sha256']['parameters']);result=dict(bytes=f['byte_size'],ticks=f['modified_utc_ticks'],file_sha256=bytes(h['file_sha256']['hash_value']),pixel_sha256=bytes(h['pixel_sha256']['hash_value']),visual_hash=bytes(h['perceptual']['hash_value']),display_width=int(params.get('display_width') or f['width']),display_height=int(params.get('display_height') or f['height']))
    if f['embedded_metadata'] is None or decode(f['embedded_metadata']).get('_gallery_extractor')!=2:
     meta=header_metadata(safe(f['relative_path']));result.update(metadata=meta,metadata_score=embedded_score(meta))
    hits+=1;accept(f,result,False)
   except Exception as error:record_decode_error(db,f,error,errors)
  for future in as_completed(futures):
   f=futures[future]
   try:accept(f,future.result(),True)
   except Exception as error:record_decode_error(db,f,error,errors)
 flush();return results,hits,errors
def manual_images(db):
 active=marker_keys(db)
 entries=all_entries(db);claimed={r['file_id'] for r in q(db,'SELECT DISTINCT file_id FROM fm_entry_file_projection WHERE file_id IS NOT NULL')};archived=set()
 for p in (BASE/'iis-data/deleted-entries').glob('*.json'):
  try:
   item=json.loads(p.read_text(encoding='utf-8-sig'))
   for _,owner,name in record_refs(item.get('record',{}),item.get('collection','')):archived.add(pathkey(owner+'/'+name))
  except Exception:pass
 # Preserve pre-migration deletions even after category moves changed their paths.
 for operation in q(db,'SELECT source_file_id,source_path FROM file_operations'):
  if pathkey(operation['source_path']) in archived:claimed.add(operation['source_file_id'])
 for f in q(db,'SELECT id,path_key,canonical_file_id FROM fm_file_catalog'):
  if bytes(f['path_key']) in archived or f['id'] in claimed:
   claimed.add(f['canonical_file_id'] or f['id'])
 for f in q(db,"SELECT f.*,c.collection_key,d.id AS doc FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id JOIN inventory_documents d ON d.collection_id=c.id WHERE f.is_present=TRUE AND f.canonical_file_id IS NULL AND f.width IS NOT NULL"):
  if f['collection_key'] not in active or f['id'] in claimed or bytes(f['path_key']) in archived:continue
  stable_id='local-'+bytes(f['path_key']).hex()[:16]
  previous=next((e for e in entries if e['collection_key']==f['collection_key'] and e['source_id'].endswith(stable_id) and clean(e).get('manual_import')),None)
  if previous and previous['deleted_at']:continue
  if previous:
   relative=file_name(f['relative_path'],f['collection_key'])
   def rediscover(r):
    r.update(file=relative,file_collection=f['collection_key'],file_size=f['byte_size'],status='saved',category=path_category(f['relative_path'],f['collection_key']) or 'not_provided')
    for field in ['duplicate_of','duplicate_collection','duplicate_record_of','duplicate_record_site','duplicate_record_collection','duplicate_detected_by','error','reason']:r.pop(field,None)
   update_entry(db,previous['id'],rediscover,'manual_image_rediscovered',hidden=False,canonical=None)
   continue
  name=Path(f['relative_path']).name;stem=Path(name).stem;matches=[]
  for e in entries:
   if e['deleted_at'] or e['collection_key']!=f['collection_key']:continue
   r=clean(e);urls=[r.get('url',''),*r.get('original_image_urls',[])];urlmatch=any(Path(str(url).split('?')[0]).name==name for url in urls if url)
   if urlmatch or (e['site'] and e['source_id'] and re.search(r'(?:^|\W)'+re.escape(e['site'])+r'[ _-]+'+re.escape(e['source_id'])+r'(?:$|\W)',stem,re.I)):matches.append(e)
  images=[e for e in matches if e['record_type']=='image'];matches=images or matches
  relative=file_name(f['relative_path'],f['collection_key'])
  if len(matches)==1 and matches[0]['record_type']=='image':update_entry(db,matches[0]['id'],{'file':relative,'file_collection':f['collection_key'],'status':'saved','file_size':f['byte_size']},'manual_image_found')
  else:
   original=matches[0] if len(matches)==1 else None;r={'site':original['site'] if original else 'Local','id':(original['source_id']+'-' if original else '')+'local-'+bytes(f['path_key']).hex()[:16],'record_type':'image','status':'saved','file':relative,'file_collection':f['collection_key'],'file_size':f['byte_size'],'manual_import':True,'category':path_category(f['relative_path'],f['collection_key']) or 'not_provided'}
   if original:r.update(source=original['source_url'],description=original['description'])
   ordinal=q(db,'SELECT COALESCE(MAX(source_ordinal),-1)+1 AS n FROM inventory_entries WHERE inventory_document_id=%s',(f['doc'],))[0]['n'];raw=dumps(r)
   eid=run(db,'INSERT INTO inventory_entries(inventory_document_id,source_ordinal,site,source_id,record_type,status,category,description,source_url,raw_record,record_sha256) VALUES(%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)',(f['doc'],ordinal,r['site'],r['id'],'image','saved',r['category'],r.get('description'),r.get('source'),raw,sha(raw.encode())))[0];run(db,"INSERT INTO inventory_entry_history(entry_id,revision,action,new_record,actor) VALUES(%s,1,'manual_image_found',%s,'Database worker')",(eid,raw))
def render_key(f):
 meta=f.get('metadata') or decode(f.get('embedded_metadata')) or {}
 return (f['pixel_sha256'],meta.get('icc_profile_sha256',''),dumps(meta.get('animation_durations_ms',[])),str(meta.get('animation_loop',0)))
def matches(db,files):
 groups=defaultdict(list);counts=defaultdict(int);inserts=[];lookup={f['id']:f for f in files};parent={f['id']:f['id'] for f in files}
 def find(a):
  while parent[a]!=a:parent[a]=parent[parent[a]];a=parent[a]
  return a
 for f in files:f['_render_key']=render_key(f);groups[f['_render_key']].append(f)
 for a,b in itertools.combinations(files,2):
  kinds=[]
  if a['file_sha256']==b['file_sha256']:kinds.append(('exact_file','sha256',None,'confirmed'))
  if a['_render_key']==b['_render_key']:
   kinds.append(('exact_pixels','pillow-rgba-frames-sha256',None,'confirmed'));parent[find(b['id'])]=find(a['id'])
  elif abs((a['display_width']/a['display_height'])/(b['display_width']/b['display_height'])-1)<=.02:
   distance=(int.from_bytes(a['visual_hash'],'big')^int.from_bytes(b['visual_hash'],'big')).bit_count()
   if distance<=5:kinds.append(('visual_similarity','dhash64',distance,'pending'))
  for kind,algo,distance,review in kinds:
   low,high=(a,b) if a['id']<b['id'] else (b,a);counts[kind]+=1
   inserts.append((low['id'],high['id'],kind,algo,'1',distance,review,low['bytes'],low['ticks'],high['bytes'],high['ticks'],now(),'Human review required' if review=='pending' else 'Verified exact contents or decoded pixels'))
 if inserts:
  with db.cursor() as cursor:cursor.executemany('INSERT INTO duplicate_matches(file_id_low,file_id_high,match_kind,algorithm,algorithm_version,distance,review_state,low_source_byte_size,low_source_modified_utc_ticks,high_source_byte_size,high_source_modified_utc_ticks,detected_at,notes) VALUES(%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s) ON DUPLICATE KEY UPDATE detected_at=VALUES(detected_at),distance=VALUES(distance)',inserts)
 return [g for g in groups.values() if len(g)>1],dict(counts)
def consolidate(db,groups):
 removed=0
 for group in groups:
  if any(not FMW.can_manage(db,f['relative_path'],f['id'])[0] for f in group):continue
  pins=q(db,'SELECT file_id,collection_id FROM fm_file_placement WHERE file_id IN ('+','.join(['%s']*len(group))+')',[f['id'] for f in group]);pinned={p['file_id'] for p in pins}
  if len({p['collection_id'] for p in pins})>1:continue
  members={f['id'] for f in group};slots=','.join(['%s']*len(members));decisions=q(db,"SELECT * FROM duplicate_matches WHERE file_id_low IN ("+slots+") AND file_id_high IN ("+slots+") AND (review_state='rejected' OR (review_state='confirmed' AND reviewed_at IS NOT NULL AND retained_file_id IS NOT NULL))",[*members,*members])
  if any(d['review_state']=='rejected' and d['file_id_low'] in members and d['file_id_high'] in members for d in decisions):continue
  overrides={d['retained_file_id'] for d in decisions if d['review_state']=='confirmed' and d['file_id_low'] in members and d['file_id_high'] in members and d['retained_file_id'] in members}
  if len(overrides)>1 or (overrides and pinned and not (overrides & pinned)):continue
  scores={}
  for f in group:
   rows=q(db,'SELECT e.metadata_score FROM fm_entry_file_projection l JOIN inventory_entries e ON e.id=l.entry_id WHERE l.file_id=%s AND e.deleted_at IS NULL',(f['id'],));scores[f['id']]=f.get('metadata_score',0)+max([e['metadata_score'] for e in rows]+[0])
  keeper=max(group,key=lambda f:(f['id'] in pinned,f['id'] in overrides,scores[f['id']],-f['id']));kp=safe(keeper['relative_path'])
  for f in group:
   if f is keeper:continue
   # A failed cleanup already owns this exact pair. Recovery will resume it;
   # do not create another identical error each time matching runs.
   if q(db,"SELECT id FROM file_operations WHERE operation_kind='deduplicate' AND source_file_id=%s AND target_file_id=%s AND source_sha256=%s AND target_sha256=%s AND state IN ('planned','copied','linked','error') LIMIT 1",(f['id'],keeper['id'],f['file_sha256'],keeper['file_sha256'])):continue
   allowed,reason=FMW.can_relocate(db,f['id'],keeper['relative_path'])
   if not allowed:
    run(db,'INSERT INTO fm_deferred_files(file_id,reason) VALUES(%s,%s) ON DUPLICATE KEY UPDATE reason=VALUES(reason)',(f['id'],reason));continue
   op=run(db,"INSERT INTO file_operations(operation_kind,source_file_id,target_file_id,source_path,target_path,source_sha256,target_sha256) VALUES('deduplicate',%s,%s,%s,%s,%s,%s)",(f['id'],keeper['id'],f['relative_path'],keeper['relative_path'],f['file_sha256'],keeper['file_sha256']))[0]
   try:
    with protected(kp) as kept,protected(safe(f['relative_path']),True) as duplicate:
     if hashstream(kept)!=keeper['file_sha256'] or hashstream(duplicate)!=f['file_sha256']:raise RuntimeError('File changed after hash comparison')
     # The DB and every raw reference are committed before removing the old file.
     rewrite_path(db,f,keeper,source_stream=duplicate,target_stream=kept)
     run(db,'UPDATE files SET canonical_file_id=%s WHERE canonical_file_id=%s',(keeper['id'],f['id']));run(db,'UPDATE files SET canonical_file_id=%s,is_present=FALSE WHERE id=%s',(keeper['id'],f['id']))
     run(db,'UPDATE duplicate_matches SET retained_file_id=%s WHERE file_id_low IN (%s,%s) AND file_id_high IN (%s,%s) AND match_kind IN (%s,%s)',(keeper['id'],f['id'],keeper['id'],f['id'],keeper['id'],'exact_file','exact_pixels'))
     run(db,"UPDATE file_operations SET state='linked' WHERE id=%s",(op,));delete_protected(duplicate)
    run(db,"UPDATE file_operations SET state='complete',completed_at=UTC_TIMESTAMP(6) WHERE id=%s",(op,));removed+=1
   except Exception as e:run(db,"UPDATE file_operations SET state='error',error=%s WHERE id=%s",(str(e),op));raise
 return removed
def aliases(db):
 byfile=defaultdict(list);byentry=defaultdict(set)
 for link in q(db,'SELECT entry_id,file_id FROM fm_entry_file_projection WHERE file_id IS NOT NULL'):byentry[link['entry_id']].add(link['file_id'])
 for row in q(db,'SELECT DISTINCT l.file_id,e.* FROM fm_entry_file_projection l JOIN inventory_entries e ON e.id=l.entry_id WHERE e.deleted_at IS NULL AND l.file_id IS NOT NULL'):byfile[row['file_id']].append(row)
 stored={r['state_key']:decode(r['value']) for r in q(db,"SELECT state_key,value FROM gallery_state WHERE state_key LIKE 'aliases:%%' OR state_key LIKE 'album_aliases:%%'")}
 owners={r['id']:r['collection_key'] for r in q(db,'SELECT id,collection_key FROM collections')}
 docowners={r['id']:r['collection_id'] for r in q(db,'SELECT id,collection_id FROM inventory_documents')}
 for f in q(db,"SELECT f.*,h.hash_value AS actual_sha256 FROM fm_file_catalog f JOIN file_hashes h ON h.file_id=f.id AND h.hash_kind='file_sha256' AND h.algorithm='sha256' AND h.algorithm_version='1' AND h.source_byte_size=f.byte_size AND h.source_modified_utc_ticks=f.modified_utc_ticks WHERE f.is_present=TRUE AND f.canonical_file_id IS NULL AND f.width IS NOT NULL"):
  eligible=[e for e in byfile[f['id']] if len(byentry[e['id']])==1]
  if not eligible:continue
  signature=sha(dumps({'version':3,'entries':sorted((e['id'],e['metadata_score']) for e in eligible)}).encode()).hex()
  if stored.get('aliases:'+str(f['id']),{}).get('signature')==signature:continue
  primary=max(eligible,key=lambda e:(e['metadata_score'],docowners.get(e['inventory_document_id'])==f['collection_id'],e['record_type']=='image',-e['id']));owner=owners[f['collection_id']];name=file_name(f['relative_path'],owner)
  for e in eligible:
   isprimary=e['id']==primary['id'];patch={'status':'saved' if isprimary else 'duplicate','file':name,'file_collection':owner,'file_size':f['byte_size'],'sha256':bytes(f['actual_sha256']).hex()}
   if not isprimary:patch.update(duplicate_of=name,duplicate_collection=owner,duplicate_record_of=primary['source_id'],duplicate_record_site=primary['site'],duplicate_detected_by='database_exact_file_or_pixels')
   if isprimary:
    def apply_primary(r):
     r.update(patch)
     for field in ['duplicate_of','duplicate_collection','duplicate_record_of','duplicate_record_site','duplicate_record_collection','duplicate_detected_by']:r.pop(field,None)
    update_entry(db,e['id'],apply_primary,'canonical_entry_selected',hidden=False,canonical=None)
   else:update_entry(db,e['id'],patch,'canonical_entry_selected',hidden=True,canonical=primary['id'])
  state(db,'aliases:'+str(f['id']),{'signature':signature,'canonical_entry_id':primary['id']})
 # Albums/posts that share the same complete set of physical images are aliases too.
 entry_rows={e['id']:e for e in all_entries(db) if not e['deleted_at']}
 live_files={f['id']:f for f in q(db,'SELECT id,collection_id FROM fm_file_catalog WHERE is_present=TRUE AND canonical_file_id IS NULL')}
 album_groups=defaultdict(list)
 for eid,ids in byentry.items():
  e=entry_rows.get(eid)
  if not e or len(ids)<2 or e['record_type'] not in ['post','album','post_or_album'] or not ids.issubset(live_files):continue
  if clean(e).get('duplicate_detected_by')=='database_exact_file_or_pixels' and e['canonical_entry_id'] is not None:
   def detach(r):
    r['status']='saved'
    for field in ['duplicate_of','duplicate_collection','duplicate_record_of','duplicate_record_site','duplicate_record_collection','duplicate_detected_by']:r.pop(field,None)
   update_entry(db,eid,detach,'album_gained_images',hidden=False,canonical=None)
   e=q(db,'SELECT * FROM inventory_entries WHERE id=%s',(eid,))[0]
  album_groups[tuple(sorted(ids))].append(e)
 for ids,rows in album_groups.items():
  if len(rows)<2 and clean(rows[0]).get('duplicate_detected_by')!='database_exact_image_set':continue
  signature=sha(dumps({'version':3,'entries':sorted((e['id'],e['metadata_score']) for e in rows)}).encode()).hex();key='album_aliases:'+sha(dumps(ids).encode()).hex()
  if stored.get(key,{}).get('signature')==signature:continue
  def rank(e):return (e['metadata_score'],sum(live_files[fid]['collection_id']==docowners[e['inventory_document_id']] for fid in ids),-e['id'])
  primary=max(rows,key=rank)
  for e in rows:
   first=e['id']==primary['id'];patch={'status':'saved' if first else 'duplicate'}
   if not first:patch.update(duplicate_record_of=primary['source_id'],duplicate_record_site=primary['site'],duplicate_detected_by='database_exact_image_set')
   if first:
    def apply_primary(r):
     r.update(patch)
     for field in ['duplicate_of','duplicate_collection','duplicate_record_of','duplicate_record_site','duplicate_record_collection','duplicate_detected_by']:r.pop(field,None)
    update_entry(db,e['id'],apply_primary,'canonical_album_selected',hidden=False,canonical=None)
   else:update_entry(db,e['id'],patch,'canonical_album_selected',hidden=True,canonical=primary['id'])
  state(db,key,{'signature':signature,'canonical_entry_id':primary['id']})
def recovery_file(db,file_id,recorded_path,expected):
 rows=q(db,'SELECT f.*,c.collection_key AS owner FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id WHERE f.id=%s',(file_id,))
 if not rows:return None
 recorded=safe(recorded_path)
 if recorded.exists():
  if filehash(recorded)!=bytes(expected):raise RuntimeError('Recovery file changed: '+recorded_path)
  return dict(rows[0],relative_path=recorded_path),recorded
 seen=set()
 while rows and rows[0]['id'] not in seen:
  f=rows[0];seen.add(f['id']);path=safe(f['relative_path'])
  if path.exists() and filehash(path)==bytes(expected):return f,path
  if not f['canonical_file_id']:break
  rows=q(db,'SELECT f.*,c.collection_key AS owner FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id WHERE f.id=%s',(f['canonical_file_id'],))
 return None

def close_superseded_duplicate(db,op):
 """Close an old dedupe plan after both IDs already resolve to one verified copy."""
 if op['operation_kind']!='deduplicate' or bytes(op['source_sha256'])!=bytes(op['target_sha256']):return False
 files=q(db,'SELECT id,canonical_file_id FROM files WHERE id IN (%s,%s)',(op['source_file_id'],op['target_file_id']))
 if len(files)!=2 or not files[0]['canonical_file_id'] or files[0]['canonical_file_id']!=files[1]['canonical_file_id']:return False
 survivor=q(db,'SELECT relative_path,is_present,canonical_file_id FROM files WHERE id=%s',(files[0]['canonical_file_id'],))
 if len(survivor)!=1 or not survivor[0]['is_present'] or survivor[0]['canonical_file_id']:return False
 if q(db,'SELECT id FROM inventory_entries WHERE file_id IN (%s,%s) LIMIT 1',(op['source_file_id'],op['target_file_id'])):return False
 if q(db,'SELECT file_id FROM fm_external_bindings WHERE file_id IN (%s,%s) LIMIT 1',(op['source_file_id'],op['target_file_id'])):return False
 for old in (op['source_path'],op['target_path']):
  if safe(old).exists():return False
 saved=safe(survivor[0]['relative_path'])
 if not saved.is_file() or filehash(saved)!=bytes(op['source_sha256']):return False
 run(db,"UPDATE file_operations SET state='cancelled',error='Superseded by verified surviving duplicate',completed_at=UTC_TIMESTAMP(6) WHERE id=%s AND state IN ('planned','copied','linked','error')",(op['id'],))
 return True

def recover(db,operation_id=None):
 require_root()
 ops=q(db,"SELECT * FROM file_operations WHERE state IN ('planned','copied','linked','error')"+(" AND id=%s" if operation_id is not None else "")+" ORDER BY id",(operation_id,) if operation_id is not None else ())
 total=len(ops);review=q(db,"SELECT COUNT(*) n FROM file_operations WHERE state='error' AND (error='Missing source has different bytes; recovery needs review' OR error='Downloader MD5 differs from the actual file; original retained')")[0]['n']
 review_issues=[{'file':r['source_path'],'error':'Operation '+str(r['id'])+' needs review: source missing and retained file has different bytes'} for r in q(db,"SELECT id,source_path FROM file_operations WHERE state='error' AND error='Missing source has different bytes; recovery needs review' ORDER BY id LIMIT 10")]
 for index,op in enumerate(ops):
  if operation_id is None and index%10==0:
   import RecoveryActions
   RecoveryActions.drain(sys.modules[__name__],db)
  fresh=q(db,'SELECT * FROM file_operations WHERE id=%s',(op['id'],))
  if not fresh or fresh[0]['state'] in ('complete','cancelled'):continue
  op=fresh[0]
  if index%10==0:progress(db,'Recovering file operations',index,total,recovery_processed=index,recovery_total=total,recovery_review=review,issues=review_issues)
  if op['state']=='error' and (op['error'] or '').startswith('On hold: '):continue
  try:
   if q(db,'SELECT move_id FROM fm_collection_move_files WHERE operation_id=%s LIMIT 1',(op['id'],)):continue
   if close_superseded_duplicate(db,op):continue
   if not FMW.can_manage(db,op['source_path'])[0]:continue
   if not FMW.can_relocate(db,op['source_file_id'],op['target_path'])[0]:continue
   if op['operation_kind']=='organize':
    current=q(db,'SELECT f.*,c.collection_key AS owner FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id WHERE f.id=%s',(op['source_file_id'],))[0]
    if current['relative_path'] not in [op['source_path'],op['target_path']]:
     current_path=safe(current['relative_path'])
     if current_path.exists() and filehash(current_path)==bytes(op['source_sha256']):
      run(db,"UPDATE file_operations SET state='cancelled',error=%s,completed_at=UTC_TIMESTAMP(6) WHERE id=%s",('Superseded by a later move; verified original retained at '+current['relative_path'],op['id']));continue
   source_found=recovery_file(db,op['source_file_id'],op['source_path'],op['source_sha256'])
   target_found=recovery_file(db,op['target_file_id'],op['target_path'],op['target_sha256'])
   if not target_found:
    if not source_found:raise RuntimeError('Both paths absent for file operation '+str(op['id']))
    f,source=source_found
    with protected(source) as original:
     if hashstream(original)!=bytes(op['source_sha256']):raise RuntimeError('Recovery original changed')
     # An unstarted plan must not keep blocking after organization moves its original.
     note='Obsolete plan cancelled; verified original retained at '+f['relative_path']
     if op['operation_kind']=='deduplicate' and bytes(op['source_sha256'])==bytes(op['target_sha256']):
      rejected=q(db,"SELECT id FROM duplicate_matches WHERE file_id_low=%s AND file_id_high=%s AND review_state='rejected'",(min(op['source_file_id'],op['target_file_id']),max(op['source_file_id'],op['target_file_id'])))
      missing=q(db,'SELECT * FROM files WHERE id=%s',(op['target_file_id'],))[0]
      if not rejected and missing['id']!=f['id']:
       keeper=dict(f,file_sha256=bytes(op['source_sha256']))
       rewrite_path(db,missing,keeper,source_stream=original,target_stream=original)
       run(db,'UPDATE files SET canonical_file_id=NULL,is_present=TRUE WHERE id=%s',(f['id'],))
       run(db,'UPDATE files SET canonical_file_id=%s WHERE canonical_file_id=%s',(f['id'],missing['id']))
       run(db,'UPDATE files SET canonical_file_id=%s,is_present=FALSE WHERE id=%s',(f['id'],missing['id']))
       note+='; missing identical target references linked to surviving original'
    run(db,"UPDATE file_operations SET state='cancelled',error=%s,completed_at=UTC_TIMESTAMP(6) WHERE id=%s",(note,op['id']));continue
   keeper,target=target_found;keeper=dict(keeper,file_sha256=bytes(op['target_sha256']))
   # A completed deduplication may have retired the old path; its file ID still exists.
   source_row=q(db,'SELECT * FROM files WHERE id=%s',(op['source_file_id'],))[0]
   source=source_found[1] if source_found else None
   if source==target:
    state_name='complete' if keeper['relative_path']==op['target_path'] else 'cancelled'
    run(db,"UPDATE file_operations SET state=%s,error=%s,completed_at=UTC_TIMESTAMP(6) WHERE id=%s",(state_name,'Verified file is already at its current location; no file deleted',op['id']));continue
   def link(source_stream=None,target_stream=None):
    f=dict(source_row,relative_path=source.relative_to(ROOT).as_posix() if source else op['source_path'])
    rewrite_path(db,f,keeper,source_stream=source_stream,target_stream=target_stream)
    if op['operation_kind']=='organize':run(db,'UPDATE files SET relative_path=%s,path_key=%s WHERE id=%s',(keeper['relative_path'],pathkey(keeper['relative_path']),f['id']))
    else:
     run(db,'UPDATE files SET canonical_file_id=%s WHERE canonical_file_id=%s',(keeper['id'],f['id']))
     run(db,'UPDATE files SET canonical_file_id=%s,is_present=FALSE WHERE id=%s',(keeper['id'],f['id']))
    run(db,"UPDATE file_operations SET state='linked' WHERE id=%s",(op['id'],))
   with protected(target) as kept:
    if hashstream(kept)!=bytes(op['target_sha256']):raise RuntimeError('Recovery target changed')
    if source:
     with protected(source,True) as original:
      if hashstream(original)!=bytes(op['source_sha256']):raise RuntimeError('Recovery original changed')
      try:link(original,kept)
      except RuntimeError as error:
       if 'Downloader MD5 differs from the actual file' not in str(error):raise
       run(db,"UPDATE file_operations SET state='error',error=%s WHERE id=%s",(str(error),op['id']))
       if op['error']!=str(error):review+=1
       if len(review_issues)<10:review_issues.append({'file':op['source_path'],'error':str(error)})
       continue
      delete_protected(original)
    else:
     if bytes(op['source_sha256'])!=bytes(op['target_sha256']):
      # A prior pass may already have linked the source and removed its old path.
      # Verify the retained bytes above and all current pointers before closing it.
      linked=source_row['canonical_file_id']==keeper['id'] and not source_row['is_present'] and source_row['relative_path']==''
      linked=linked and not q(db,'SELECT id FROM inventory_entries WHERE file_id=%s LIMIT 1',(source_row['id'],)) and not q(db,'SELECT file_id FROM fm_external_bindings WHERE file_id=%s LIMIT 1',(source_row['id'],))
      old_path=op['source_path']
      integration='booru' if old_path.startswith('booru/') else 'wallpaper-download' if old_path.startswith('wallpaper-download/') else None
      if linked and integration:
       import DownloaderLocations as D
       configs=q(db,"SELECT * FROM fm_adapter_config WHERE integration_key=%s AND enabled=TRUE AND adapter_kind IN ('booru-locations','wallpaper-current')",(integration,))
       linked=bool(configs) and not D.lookup(db,configs[0],path=D.relative(configs[0],old_path))
      if linked:
       run(db,"UPDATE file_operations SET state='complete',source_path='',target_path='',error=NULL,completed_at=UTC_TIMESTAMP(6) WHERE id=%s",(op['id'],))
       if op['error']=='Missing source has different bytes; recovery needs review':
        review=max(0,review-1);review_issues=[issue for issue in review_issues if issue['file']!=old_path]
       continue
      run(db,"UPDATE file_operations SET state='error',error='Missing source has different bytes; recovery needs review' WHERE id=%s",(op['id'],))
      if op['error']!='Missing source has different bytes; recovery needs review':review+=1
      if len(review_issues)<10 and not any(issue['file']==op['source_path'] for issue in review_issues):review_issues.append({'file':op['source_path'],'error':'Operation '+str(op['id'])+' needs review: source missing and retained file has different bytes'})
      continue
     try:link(source_stream=kept,target_stream=kept)
     except RuntimeError as error:
      if 'Downloader MD5 differs from the actual file' not in str(error):raise
      run(db,"UPDATE file_operations SET state='error',error=%s WHERE id=%s",(str(error),op['id']))
      if op['error']!=str(error):review+=1
      if len(review_issues)<10:review_issues.append({'file':op['source_path'],'error':str(error)})
      continue
   run(db,"UPDATE file_operations SET state='complete',error=NULL,completed_at=UTC_TIMESTAMP(6) WHERE id=%s",(op['id'],))
  except Exception as error:
   db.rollback()
   message=str(error)
   run(db,"UPDATE file_operations SET state='error',error=%s WHERE id=%s",(message,op['id']))
   print('Recovery operation '+str(op['id'])+': '+traceback.format_exc(),flush=True)
   if op['error']!=message:review+=1
   if len(review_issues)<10:review_issues.append({'file':op['source_path'],'error':'Operation '+str(op['id'])+': '+message})
 for source_id,target_id,source_digest,target_digest in {(op['source_file_id'],op['target_file_id'],bytes(op['source_sha256']),bytes(op['target_sha256'])) for op in ops if op['operation_kind']=='deduplicate'}:
  try:retire_duplicate_backups(db,source_id,target_id,source_digest,target_digest)
  except Exception as error:print('Duplicate backup cleanup '+str(source_id)+' -> '+str(target_id)+': '+str(error),flush=True)
 if total:progress(db,'File recovery pass complete',total,total,recovery_processed=total,recovery_total=total,recovery_review=review,issues=review_issues)

def cycle(db):
 require_root()
 progress(db,'Discovering collection folders');recover(db);register_collection_folders(db)
 requested=state(db,'import_updates').get('requested',False)
 imported,pending=import_documents(db,requested)
 progress(db,'Assigning unrelated entries to other collections');route_excluded_unrelated(db)
 if requested:state(db,'import_updates',{'requested':False})
 index_files(db);normalize_links(db);refresh_manual_categories(db)
 progress(db,'Organizing category folders');organize(db);index_files(db);normalize_links(db)
 scan=run(db,"INSERT INTO scan_runs(scan_type,state,started_at,details) VALUES('full','running',UTC_TIMESTAMP(6),%s)",(dumps({'scope':'all collections','worker':'DatabaseWorker'}),))[0]
 try:
  flash_errors=index_flash_files(db);files,hits,errors=hash_files(db,scan);errors.extend(flash_errors);errors.extend(state(db,'invalid_documents').get('errors',[]));manual_images(db);normalize_links(db);progress(db,'Comparing exact and visual hashes',len(files),len(files));groups,counts=matches(db,files)
  progress(db,'Consolidating exact duplicates');removed=consolidate(db,groups);aliases(db);normalize_links(db)
  run(db,"UPDATE scan_runs SET state='completed',finished_at=UTC_TIMESTAMP(6),files_seen=%s,files_processed=%s,cache_hits=%s,errors=%s,details=%s WHERE id=%s",(len(files)+len(errors),len(files),hits,len(errors),dumps({'matches':counts,'removed_files':removed,'errors':errors,'pending_inventory_updates':pending}),scan))
  run(db,"UPDATE inventory_documents SET import_state='ready' WHERE import_state IN ('organizing','hashing','importing')")
  progress(db,'Collections updated from MySQL',len(files),len(files),phase='complete',scan_id=scan,cache_hits=hits,imported=imported,removed=removed,matches=counts,issues=errors,pending_updates=pending)
 except Exception as e:
  run(db,"UPDATE scan_runs SET state='failed',finished_at=UTC_TIMESTAMP(6),errors=errors+1,details=%s WHERE id=%s",(dumps({'error':str(e)}),scan));raise
def input_fingerprint():
 # Metadata checks only: unchanged images are neither decoded nor compared.
 h=hashlib.sha256()
 rows=[]
 for folder,info in discover_collection_folders():rows.append([info.relative_to(ROOT).as_posix(),'collection_marker'])
 for path in discover():
  try:
   st=path.stat()
   if time.time()-st.st_mtime<3:continue
   rows.append([path.relative_to(ROOT).as_posix(),st.st_size,st.st_mtime_ns])
  except FileNotFoundError:continue
 # DirEntry supplies cached size/time attributes on Windows SMB listings.
 for path,st in image_file_stats():
  if time.time()-st.st_mtime>=3:rows.append([path.relative_to(ROOT).as_posix(),st.st_size,st.st_mtime_ns])
 for row in sorted(rows):h.update(dumps(row).encode())
 return h.digest()

def processing_needed(previous,current,requested=False):
 return previous is None or previous!=current or requested

def main():
 previous_inputs=None
 once='--once' in sys.argv
 while True:
  db=None
  try:
   db=connect()
   if not q(db,"SELECT GET_LOCK('wallpapers_database_worker',0) AS acquired")[0]['acquired']:
    if once:return
    time.sleep(10);continue
   current_inputs=input_fingerprint()
   requested=state(db,'worker_requested').get('requested',False) or state(db,'import_updates').get('requested',False)
   if not once and not processing_needed(previous_inputs,current_inputs,requested):
    q(db,"SELECT RELEASE_LOCK('wallpapers_database_worker')");db.close();db=None
    for _ in range(12):
     time.sleep(5)
     with connect() as check:
      if state(check,'worker_requested').get('requested') or state(check,'import_updates').get('requested'):break
    continue
   run(db,"UPDATE scan_runs SET state='cancelled',finished_at=UTC_TIMESTAMP(6),details=JSON_OBJECT('worker','DatabaseWorker','reason','Worker restarted; completed hashes remain cached') WHERE state='running' AND JSON_UNQUOTE(JSON_EXTRACT(details,'$.worker'))='DatabaseWorker'");state(db,'worker_requested',{'requested':False});
   if '--import-only' in sys.argv:register_collection_folders(db);import_documents(db,False);route_excluded_unrelated(db);index_files(db);normalize_links(db);refresh_manual_categories(db)
   else:cycle(db)
   previous_inputs=current_inputs
  except Exception as err:
   print(now().isoformat(),traceback.format_exc(),flush=True)
   if db:
    try:progress(db,str(err),phase='error')
    except Exception:pass
   if once:raise
  finally:
   if db:
    try:q(db,"SELECT RELEASE_LOCK('wallpapers_database_worker')")
    finally:db.close()
  if once:return
  # Poll requests without keeping a transaction or server lock open.
  for _ in range(12):
   time.sleep(5)
   try:
    with connect() as check:
     if state(check,'worker_requested').get('requested'):break
   except Exception:break
if __name__=='__main__':
 if '--once' not in sys.argv:
  log=(BASE/'iis-data/database-worker.log').open('a',encoding='utf-8',buffering=1);sys.stdout=log;sys.stderr=log
 settings=ET.parse(BASE/'iis-site/web.config').getroot().find('appSettings')
 if any(item.get('key')=='GalleryPagedInventory' and item.get('value')=='true' for item in settings):FMW.main(sys.modules[__name__],once='--once' in sys.argv)
 else:main()


