"""Incremental file manager: bounded database work and indexed duplicate candidates."""
import os,time,json,hashlib,uuid,traceback,datetime,threading,random
from collections import deque
from concurrent.futures import ThreadPoolExecutor,wait,FIRST_COMPLETED
from contextlib import contextmanager
from pathlib import Path
import FileManager as F

VISUAL_WORKERS=10
MAX_VISUAL_WORKERS=16
MUTATION_WORKERS=4
INDEX_WORKERS=4
INDEX_BATCH_SIZE=1
MUTATION_CLAIM_PER_WORKER=10
CLAIM_BATCH_SIZE=MUTATION_WORKERS*MUTATION_CLAIM_PER_WORKER
MAX_PIPELINE_JOBS=CLAIM_BATCH_SIZE*2
PIPELINE_SECONDS=300
BACKLOG_SCAN_INTERVAL=1800
_mutation_stripes=[threading.Lock() for _ in range(4096)]
_activity_guard=threading.Lock()
_activity={'visual':0,'mutation':0}
_average_seconds={'visual':None,'mutation':None}
_completed={'visual':0,'mutation':0}
_index_guard=threading.Lock()
_index_active=0
_indexed_total=0

def index_counts():
 with _index_guard:return _index_active,_indexed_total

@contextmanager
def active_stage(name):
 started=time.monotonic()
 with _activity_guard:_activity[name]+=1
 try:yield
 finally:
  elapsed=time.monotonic()-started
  with _activity_guard:
   _activity[name]-=1;_completed[name]+=1
   old=_average_seconds[name];_average_seconds[name]=elapsed if old is None else old*.85+elapsed*.15

def active_counts():
 with _activity_guard:return dict(active=_activity.copy(),average_seconds=_average_seconds.copy(),completed=_completed.copy())

def reset_activity_counts():
 with _activity_guard:
  for name in _activity:_average_seconds[name]=None;_completed[name]=0

def recommended_visual_limit(limit,metrics,mutation_backlog,visual_backlog):
 visual_seconds=metrics['average_seconds']['visual'];mutation_seconds=metrics['average_seconds']['mutation']
 if (limit>=MAX_VISUAL_WORKERS or not visual_seconds or not mutation_seconds
     or metrics['completed']['visual']<20 or metrics['completed']['mutation']<8
     or mutation_backlog>=MUTATION_WORKERS or not visual_backlog):return limit
 visual_rate=limit/visual_seconds;mutation_rate=MUTATION_WORKERS/mutation_seconds
 return min(MAX_VISUAL_WORKERS,limit+2) if mutation_rate>visual_rate*1.15 else limit

@contextmanager
def mutation_locks(keys):
 # Every worker acquires the same sorted key order, so overlapping groups wait
 # without deadlocking; disjoint groups can modify files concurrently.
 locks=[_mutation_stripes[index] for index in sorted({hash(key)%len(_mutation_stripes) for key in keys})]
 for lock in locks:lock.acquire()
 try:yield
 finally:
  for lock in reversed(locks):lock.release()

def prepare_sync(db,source,target,root,source_stream=None,target_stream=None):
 import DownloaderLocations as D
 bound=F.rows(db,'SELECT integration_id FROM fm_external_bindings WHERE file_id=%s LIMIT 1',(source['id'],))
 intake=any((source.get('relative_path') or '').casefold().startswith(p) for p in ('booru/','wallpaper-download/'))
 if not bound and not intake:return
 def proof(path,stream=None):
  md5=hashlib.md5();sha=hashlib.sha256();size=0
  if stream is None:
   with path.open('rb') as handle:
    for block in iter(lambda:handle.read(1024*1024),b''):md5.update(block);sha.update(block);size+=len(block)
  else:
   position=stream.tell()
   try:
    stream.seek(0)
    for block in iter(lambda:stream.read(1024*1024),b''):md5.update(block);sha.update(block);size+=len(block)
   finally:stream.seek(position)
  return md5.hexdigest(),sha.hexdigest(),size
 target['_sync_hashes']=proof(root/target['relative_path'],target_stream)
 source['_sync_hashes']=target['_sync_hashes'] if source['id']==target['id'] else proof(root/source['relative_path'],source_stream) if source.get('relative_path') else None
 source['_external_checked']=D.bind(db,source['id'],source.get('relative_path') or '',source['_sync_hashes'],root)

def synchronize_paths(db,source,target,root):
 source_asset,_=F.execute(db,'INSERT INTO fm_assets(legacy_canonical_file_id) VALUES(%s) ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id)',(source['id'],))
 target_asset,_=F.execute(db,'INSERT INTO fm_assets(legacy_canonical_file_id) VALUES(%s) ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id)',(target['id'],))
 F.execute(db,'UPDATE fm_assets SET revision=revision+1,canonical_asset_id=%s WHERE id=%s',(None if source['id']==target['id'] else target_asset,source_asset))
 if source_asset!=target_asset:F.execute(db,'UPDATE fm_assets SET canonical_asset_id=%s WHERE canonical_asset_id=%s',(target_asset,source_asset))
 revision=F.rows(db,'SELECT revision FROM fm_assets WHERE id=%s',(source_asset,))[0]['revision']
 kind='asset_moved' if source['id']==target['id'] else 'asset_merged'
 if source['id']==target['id']:
  F.execute(db,'UPDATE fm_asset_locations SET relative_path=%s,path_key=%s,collection_id=%s WHERE legacy_file_id=%s',(target['relative_path'],hashlib.sha256(target['relative_path'].replace('\\','/').casefold().encode()).digest(),target['collection_id'],source['id']))
 else:F.execute(db,'UPDATE fm_asset_locations SET is_present=FALSE WHERE legacy_file_id=%s',(source['id'],))
 event,_=F.execute(db,'INSERT INTO fm_change_events(event_kind,asset_id,asset_revision,replacement_asset_id) VALUES(%s,%s,%s,%s)',(kind,source_asset,revision,None if source['id']==target['id'] else target_asset))
 bindings=F.rows(db,'SELECT b.*,i.integration_key,c.database_name,c.enabled,c.external_root,c.root_relative,c.schema_sha256,c.storage_root_id FROM fm_external_bindings b JOIN fm_integrations i ON i.id=b.integration_id JOIN fm_adapter_config c ON c.integration_key=i.integration_key WHERE b.file_id=%s FOR UPDATE',(source['id'],))
 md5,sha256,byte_size=target.get('_sync_hashes',(None,None,None))
 if bindings and md5 is None:raise RuntimeError('File proof was not prepared before starting the transaction')
 for b in bindings:
  if not b['enabled']:raise RuntimeError('External adapter is not enabled; original retained')
  if F.adapter_fingerprint(db,b['database_name'],b['adapter_kind'])!=bytes(b['schema_sha256'] or b''):raise RuntimeError('External schema changed; original retained')
  schema=b['database_name'];old=b['last_known_path']
  new=external_path(b,target['relative_path'])
  identity=hashlib.sha256(f"{event}|{b['integration_id']}|{b['external_id']}".encode()).digest()
  sync,_=F.execute(db,"INSERT INTO fm_database_sync(integration_id,event_id,external_record_id,operation,asset_id,canonical_asset_id,expected_old_path,new_path,idempotency_key) VALUES(%s,%s,%s,%s,%s,%s,%s,%s,%s)",(b['integration_id'],event,b['external_id'],'path_changed' if kind=='asset_moved' else 'duplicate_linked',source_asset,target_asset,old,new,identity))
  replacement=b['external_id']
  if b['adapter_kind'] in ('booru-locations','wallpaper-current'):
   import DownloaderLocations as D
   replacement=D.sync(db,b,b,new,source.get('_sync_hashes') or target['_sync_hashes'],target['_sync_hashes'])
  elif b['adapter_kind']=='booru-downloads':F.sync_download_path(db,schema,int(b['external_id'].split(':')[0]),old,new,md5)
  elif b['adapter_kind']=='wallpaper-files':F.sync_wallpaper_path(db,schema,int(b['external_id']),old,new,sha256,byte_size)
  else:raise RuntimeError('Unknown external adapter')
  if replacement!=b['external_id']:F.execute(db,'DELETE FROM fm_external_bindings WHERE integration_id=%s AND external_id=%s',(b['integration_id'],b['external_id']))
  F.execute(db,'INSERT INTO fm_external_bindings(integration_id,external_id,file_id,last_known_path,adapter_kind) VALUES(%s,%s,%s,%s,%s) ON DUPLICATE KEY UPDATE file_id=VALUES(file_id),last_known_path=VALUES(last_known_path),adapter_kind=VALUES(adapter_kind)',(b['integration_id'],replacement,target['id'],new,b['adapter_kind']))
  F.execute(db,"UPDATE fm_database_sync SET state='completed',expected_old_path=NULL,new_path=NULL,completed_at=UTC_TIMESTAMP(6) WHERE id=%s",(sync,))
 # A managed intake file must have registered every external owner before removal.
 for integration,root_name in [('booru','booru'),('wallpaper-download','wallpaper-download')]:
  if (source.get('relative_path') or '').casefold().startswith(root_name+'/') and not any(b['integration_key']==integration for b in bindings):
   previous_redirect=source.get('canonical_file_id')==target['id'] and F.rows(db,'SELECT b.file_id FROM fm_external_bindings b JOIN fm_integrations i ON i.id=b.integration_id WHERE i.integration_key=%s AND b.file_id=%s LIMIT 1',(integration,target['id']))
   if integration in source.get('_external_checked',[]):continue
   if not previous_redirect:raise RuntimeError('Downloader file has not been registered; original retained')

def external_path(config,relative):
 if config.get('adapter_kind') in ('booru-locations','wallpaper-current'):
  import DownloaderLocations as D
  return D.relative(config,relative)
 prefix=(config.get('root_relative') or '.').replace('\\','/').strip('/')
 if prefix!='.':
  if not relative.casefold().startswith(prefix.casefold()+'/'):raise RuntimeError('Retained file is outside the downloader storage mount')
  relative=relative[len(prefix)+1:]
 base=config.get('external_root')
 if not base:raise RuntimeError('Downloader storage mapping is not configured')
 separator='\\' if base.startswith('\\\\') or re_drive(base) else '/'
 return base.rstrip('/\\')+separator+relative.replace('/',separator)

def re_drive(value):return len(value)>2 and value[1]==':'

def can_relocate(db,file_id,relative):
 configs=F.rows(db,'SELECT c.* FROM fm_external_bindings b JOIN fm_integrations i ON i.id=b.integration_id JOIN fm_adapter_config c ON c.integration_key=i.integration_key WHERE b.file_id=%s',(file_id,))
 try:
  for config in configs:external_path(config,relative)
  return True,None
 except RuntimeError as error:return False,str(error)

def can_manage(db,relative,file_id=None):
 if file_id is not None:
  import DownloaderLocations as D
  D.bind(db,file_id,relative)
 if file_id is not None:
  bindings=F.rows(db,'SELECT c.*,b.adapter_kind FROM fm_external_bindings b JOIN fm_integrations i ON i.id=b.integration_id JOIN fm_adapter_config c ON c.integration_key=i.integration_key WHERE b.file_id=%s',(file_id,))
  for binding in bindings:
   if not binding['enabled']:return False,binding['status']
   try:
    if F.adapter_fingerprint(db,binding['database_name'],binding['adapter_kind'])!=bytes(binding['schema_sha256'] or b''):return False,'External database schema changed'
   except Exception:return False,'External database is not available'
 for key,root in [('booru','booru'),('wallpaper-download','wallpaper-download')]:
  if relative.casefold().startswith(root+'/'):
   cfg=F.rows(db,'SELECT enabled,status,adapter_kind FROM fm_adapter_config WHERE integration_key=%s',(key,))
   if not cfg or not cfg[0]['enabled']:return False,(cfg[0]['status'] if cfg else 'Database adapter is not configured')
   if cfg[0].get('adapter_kind') in ('booru-locations','wallpaper-current'):continue
   if file_id is not None and not F.rows(db,'SELECT b.file_id FROM fm_external_bindings b JOIN fm_integrations i ON i.id=b.integration_id WHERE i.integration_key=%s AND b.file_id=%s LIMIT 1',(key,file_id)):return False,'Downloader file owner has not been registered'
 return True,None

def reconcile(W,db):
 W.require_root();W.register_collection_folders(db)
 active=W.marker_keys(db);owners={r['collection_key']:r['id'] for r in F.rows(db,'SELECT id,collection_key FROM collections')}
 F.execute(db,'DROP TEMPORARY TABLE IF EXISTS fm_seen_paths')
 F.execute(db,'CREATE TEMPORARY TABLE fm_seen_paths(path_key BINARY(32) PRIMARY KEY) ENGINE=InnoDB')
 pending=[];count=0;order=sorted(active,key=len,reverse=True)
 def flush():
  if not pending:return
  db.begin()
  try:
   with db.cursor() as c:
    c.executemany('INSERT IGNORE INTO fm_seen_paths VALUES(%s)',[(r[1],) for r in pending])
    c.executemany('INSERT INTO files(relative_path,path_key,byte_size,modified_utc_ticks,is_present,last_seen_at,file_name) VALUES(%s,%s,%s,%s,TRUE,UTC_TIMESTAMP(6),%s) ON DUPLICATE KEY UPDATE byte_size=VALUES(byte_size),modified_utc_ticks=VALUES(modified_utc_ticks),is_present=TRUE,canonical_file_id=NULL,file_name=VALUES(file_name)',pending)
   db.commit();pending.clear()
  except Exception:db.rollback();raise
 for path,st in W.image_file_stats():
  relative=path.relative_to(W.ROOT).as_posix();owner=next((k for k in order if k=='.' or relative.casefold().startswith(k.casefold()+'/')),None)
  if owner is None or time.time()-st.st_mtime<3:continue
  pending.append((relative,W.pathkey(relative),st.st_size,W.ticks(st),path.name));count+=1
  if len(pending)>=500:flush()
 flush()
 # Executed only after every accessible directory was successfully traversed.
 if active:
  ids=[owners[k] for k in active]
  F.execute(db,'UPDATE files f JOIN fm_file_catalog catalog ON catalog.id=f.id LEFT JOIN fm_seen_paths seen ON seen.path_key=f.path_key SET f.is_present=FALSE WHERE f.is_present=TRUE AND f.canonical_file_id IS NULL AND catalog.collection_id IN ('+','.join(['%s']*len(ids))+') AND seen.path_key IS NULL',ids)
 return count

def claim(db,limit=CLAIM_BATCH_SIZE,kind='match_file'):
 token=uuid.uuid4().bytes
 db.begin()
 try:
  F.execute(db,"UPDATE fm_work_items SET state='pending',lease_owner=NULL,lease_token=NULL,lease_expires_at=NULL WHERE state='leased' AND lease_expires_at<UTC_TIMESTAMP(6)")
  filter_sql=" AND task_kind='match_file'" if kind=='match_file' else " AND task_kind<>'match_file'"
  jobs=F.rows(db,"SELECT * FROM fm_work_items WHERE state='pending' AND available_at<=UTC_TIMESTAMP(6)"+filter_sql+" ORDER BY priority,available_at,id LIMIT %s FOR UPDATE SKIP LOCKED",(limit,))
  for j in jobs:F.execute(db,"UPDATE fm_work_items SET state='leased',lease_owner=%s,lease_token=%s,lease_expires_at=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 10 MINUTE),attempts=attempts+1 WHERE id=%s",(str(os.getpid()),token,j['id']))
  db.commit();return token,jobs
 except Exception:db.rollback();raise

def enriched(W,db,fid):
 found=F.rows(db,'SELECT f.id,f.collection_id,f.relative_path,f.byte_size,f.modified_utc_ticks,f.width,f.height,f.image_format,f.frame_count,f.is_present,f.canonical_file_id,f.metadata_score,f.icc_profile_sha256,f.color_signature,f.animation_signature,f.animation_loop,f.fm_metadata_ticks,c.collection_key owner FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id WHERE f.id=%s AND f.is_present=TRUE AND f.canonical_file_id IS NULL',(fid,))
 if not found:return None
 f=found[0];h=F.rows(db,"SELECT h.hash_kind,h.hash_value,p.display_width,p.display_height FROM file_hashes h LEFT JOIN fm_hash_settings p ON p.legacy_hash_id=h.id WHERE h.file_id=%s AND h.algorithm_version='1' AND h.source_byte_size=%s AND h.source_modified_utc_ticks=%s",(fid,f['byte_size'],f['modified_utc_ticks']));h={x['hash_kind']:x for x in h}
 if 'file_sha256' not in h:return None
 f.update(bytes=f['byte_size'],ticks=f['modified_utc_ticks'],file_sha256=bytes(h['file_sha256']['hash_value']))
 for kind,field in [('pixel_sha256','pixel_sha256'),('perceptual','visual_hash')]:f[field]=bytes(h[kind]['hash_value']) if kind in h else None
 params=h.get('pixel_sha256',{})
 if f['fm_metadata_ticks']!=f['modified_utc_ticks'] or f['color_signature'] is None:f['pixel_sha256']=None
 f.update(display_width=params.get('display_width') or f['width'] or 1,display_height=params.get('display_height') or f['height'] or 1)
 return f

def render_signature(f):return (f['pixel_sha256'],f.get('color_signature'),f.get('animation_signature'),str(f.get('animation_loop',0)),f.get('frame_count'))

def save_match(W,db,a,b,kind,distance=None):
 low,high=(a,b) if a['id']<b['id'] else (b,a)
 algorithm={'exact_file':'sha256','exact_pixels':'pillow-rgba-frames-sha256','visual_similarity':'dhash64'}[kind]
 F.execute(db,"INSERT INTO duplicate_matches(file_id_low,file_id_high,match_kind,algorithm,algorithm_version,distance,review_state,low_source_byte_size,low_source_modified_utc_ticks,high_source_byte_size,high_source_modified_utc_ticks,detected_at,notes) VALUES(%s,%s,%s,%s,'1',%s,%s,%s,%s,%s,%s,UTC_TIMESTAMP(6),%s) ON DUPLICATE KEY UPDATE distance=VALUES(distance),detected_at=VALUES(detected_at)",(low['id'],high['id'],kind,algorithm,distance,'pending' if kind=='visual_similarity' else 'confirmed',low['bytes'],low['ticks'],high['bytes'],high['ticks'],'Human review required' if kind=='visual_similarity' else 'Indexed exact comparison'))

def compare(W,db,fid,work_id=None,phase='both'):
 a=enriched(W,db,fid)
 collecting=phase=='visual_collect';visual_matches=[]
 if not a:return (True,visual_matches,0) if collecting else True
 # Hash lookups replace the former all-files Cartesian comparison.
 for kind,value,algorithm in ([] if collecting else [('file_sha256',a['file_sha256'],'sha256'),('pixel_sha256',a['pixel_sha256'],'pillow-rgba-frames-sha256')]):
  if value is None:continue
  last=0
  while True:
   candidates=F.rows(db,'SELECT h.file_id FROM file_hashes h JOIN fm_file_catalog f ON f.id=h.file_id WHERE h.hash_kind=%s AND h.algorithm=%s AND h.algorithm_version=\'1\' AND h.hash_value=%s AND h.file_id>%s AND h.file_id<>%s AND h.source_byte_size=f.byte_size AND h.source_modified_utc_ticks=f.modified_utc_ticks AND f.is_present=TRUE AND f.canonical_file_id IS NULL ORDER BY h.file_id LIMIT 100',(kind,algorithm,value,last,fid))
   if not candidates:break
   for candidate in candidates:
    last=candidate['file_id'];b=enriched(W,db,last)
    if not b:continue
    exact=a['file_sha256']==b['file_sha256'];pixels=a['pixel_sha256'] is not None and b['pixel_sha256']==a['pixel_sha256'] and render_signature(a)==render_signature(b)
    if not exact and not pixels:continue
    save_match(W,db,a,b,'exact_file' if exact else 'exact_pixels')
    allowed,reason=can_manage(db,a['relative_path'],a['id']);other,why=can_manage(db,b['relative_path'],b['id'])
    if allowed and other:
     W.consolidate(db,[[a,b]])
     a=enriched(W,db,fid)
     if not a:return True
     continue
    F.execute(db,'INSERT INTO fm_deferred_files(file_id,reason) VALUES(%s,%s) ON DUPLICATE KEY UPDATE reason=VALUES(reason)',(fid,reason or why))
    return True
   if len(candidates)<100:break
 if phase=='exact' or a['visual_hash'] is None:return (True,visual_matches,0) if collecting else True
 progress=F.rows(db,'SELECT last_candidate_id FROM fm_match_progress WHERE work_id=%s',(work_id,)) if work_id else []
 bands=list(F.visual_bands(a['visual_hash']));last=progress[0]['last_candidate_id'] if progress else 0;examined=0
 predicates=' OR '.join('(band=%s AND band_value=%s)' for _ in bands);params=[v for pair in bands for v in pair]
 while True:
  candidates=F.rows(db,'SELECT DISTINCT file_id FROM fm_visual_bands WHERE ('+predicates+') AND file_id>%s AND file_id<>%s ORDER BY file_id LIMIT 250',[*params,last,fid])
  if not candidates:break
  for candidate in candidates:
   last=candidate['file_id'];b=enriched(W,db,last)
   if not b or b['visual_hash'] is None:continue
   if a['pixel_sha256'] is not None and a['pixel_sha256']==b['pixel_sha256'] and render_signature(a)==render_signature(b):continue
   if abs((a['display_width']/a['display_height'])/(b['display_width']/b['display_height'])-1)>.02:continue
   distance=(int.from_bytes(a['visual_hash'],'big')^int.from_bytes(b['visual_hash'],'big')).bit_count()
   if distance<=5:
    if collecting:visual_matches.append((b['id'],distance))
    else:save_match(W,db,a,b,'visual_similarity',distance)
  examined+=len(candidates)
  if work_id and not collecting:
   F.execute(db,'INSERT INTO fm_match_progress VALUES(%s,%s) ON DUPLICATE KEY UPDATE last_candidate_id=VALUES(last_candidate_id)',(work_id,last))
  if work_id and examined>=1000 and len(candidates)==250:return (False,visual_matches,last) if collecting else False
  if len(candidates)<250:break
 if work_id and not collecting:F.execute(db,'DELETE FROM fm_match_progress WHERE work_id=%s',(work_id,))
 return (True,visual_matches,last) if collecting else True

def manual_entry(W,db,fid):
 found=F.rows(db,'SELECT f.*,c.collection_key,d.id doc FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id JOIN inventory_documents d ON d.collection_id=c.id JOIN fm_collection_discovery m ON m.collection_id=c.id AND m.is_marked=TRUE WHERE f.id=%s AND f.is_present=TRUE AND f.canonical_file_id IS NULL',(fid,))
 if not found:return
 f=found[0]
 if F.rows(db,'SELECT id FROM fm_entry_file_projection WHERE file_id=%s LIMIT 1',(fid,)):return
 stable='local-'+bytes(f['path_key']).hex()[:16]
 previous=F.rows(db,"SELECT id,deleted_at FROM inventory_entries WHERE site='Local' AND source_id=%s LIMIT 1",(stable,))
 if previous:
  if previous[0]['deleted_at']:return
  stable+='-'+hashlib.sha256(f"{f['byte_size']}|{f['modified_utc_ticks']}".encode()).hexdigest()[:12]
  if F.rows(db,"SELECT id FROM inventory_entries WHERE site='Local' AND source_id=%s LIMIT 1",(stable,)):return
 category=W.path_category(f['relative_path'],f['collection_key']) or 'not_provided';name=W.file_name(f['relative_path'],f['collection_key'])
 record={'site':'Local','id':stable,'record_type':'image','status':'saved','file':name,'file_collection':f['collection_key'],'file_size':f['byte_size'],'manual_import':True,'category':category}
 db.begin()
 try:
  # Lock the document while allocating its unique ordinal.
  F.rows(db,'SELECT id FROM inventory_documents WHERE id=%s FOR UPDATE',(f['doc'],))
  ordinal=F.rows(db,'SELECT COALESCE(MAX(source_ordinal),-1)+1 n FROM inventory_entries WHERE inventory_document_id=%s',(f['doc'],))[0]['n'];raw=W.dumps(record)
  eid,_=F.execute(db,"INSERT INTO inventory_entries(inventory_document_id,source_ordinal,site,source_id,record_type,status,category,raw_record,record_sha256) VALUES(%s,%s,'Local',%s,'image','saved',%s,%s,%s)",(f['doc'],ordinal,stable,category,raw,W.sha(raw.encode())))
  F.execute(db,'UPDATE inventory_entries SET file_id=%s WHERE id=%s',(fid,eid))
  F.execute(db,"INSERT INTO inventory_entry_history(entry_id,revision,action,new_record,actor) SELECT id,revision,'manual_image_found',raw_record,'File manager' FROM inventory_entries WHERE id=%s",(eid,))
  db.commit()
 except Exception:db.rollback();raise

def verify_downloader_post(W,db,fid):
 import DownloaderLocations as D
 found=F.rows(db,"SELECT f.relative_path,f.byte_size,f.modified_utc_ticks,h.hash_value FROM fm_file_catalog f JOIN file_hashes h ON h.file_id=f.id AND h.hash_kind='file_sha256' AND h.algorithm='sha256' AND h.algorithm_version='1' AND h.source_byte_size=f.byte_size AND h.source_modified_utc_ticks=f.modified_utc_ticks WHERE f.id=%s AND f.is_present=TRUE AND f.canonical_file_id IS NULL",(fid,))
 if not found:return 0
 f=found[0];path=f['relative_path']
 config=next((c for c in D.configs(db) if path.casefold().startswith({'booru':'booru/','wallpaper-download':'wallpaper-download/'}[c['integration_key']])),None)
 if config is None:return 0
 if F.adapter_fingerprint(db,config['database_name'],config['adapter_kind'])!=bytes(config['schema_sha256'] or b''):raise RuntimeError('Downloader schema changed; post matching paused')
 matched=D.match_discovered(db,config,path,W.safe(path),bytes(f['hash_value']).hex())
 D.bind(db,fid,path)
 return matched

def process(W,db,token,job):
 current=F.rows(db,'SELECT * FROM fm_file_catalog WHERE id=%s',(job['file_id'],))
 state='completed'
 if current and job['task_kind']=='reconcile_file' and current[0]['canonical_file_id']:
  source=current[0];target=enriched(W,db,source['canonical_file_id'])
  if not target:raise RuntimeError('The canonical file is unavailable')
  allowed,reason=can_relocate(db,source['id'],target['relative_path'])
  if not allowed:raise RuntimeError(reason)
  with W.protected(W.safe(target['relative_path'])) as retained:
   if W.hashstream(retained)!=target['file_sha256']:raise RuntimeError('The retained file changed')
   prepare_sync(db,source,target,W.ROOT,target_stream=retained);db.begin()
   try:synchronize_paths(db,source,target,W.ROOT);db.commit()
   except Exception:db.rollback();raise
 elif not current or not current[0]['is_present'] or current[0]['canonical_file_id'] or current[0]['byte_size']!=job['expected_size'] or current[0]['modified_utc_ticks']!=job['expected_ticks']:state='cancelled'
 else:
  fid=job['file_id']
  if job['task_kind']=='hash_file':
   if current[0]['relative_path'].lower().endswith('.swf'):
    info=W.flash_info(W.safe(current[0]['relative_path']))
    F.execute(db,"INSERT INTO file_hashes(file_id,hash_kind,algorithm,algorithm_version,hash_value,hash_bits,source_byte_size,source_modified_utc_ticks,computed_at) VALUES(%s,'file_sha256','sha256','1',%s,256,%s,%s,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE hash_value=VALUES(hash_value),source_byte_size=VALUES(source_byte_size),source_modified_utc_ticks=VALUES(source_modified_utc_ticks)",(fid,info['digest'],info['bytes'],info['ticks']))
    errors=[]
   else:result,hits,errors=W.hash_files(db,None,[fid])
   if errors:raise RuntimeError(errors[0]['error'])
   manual_entry(W,db,fid)
   F.execute(db,'INSERT INTO fm_file_dirty(file_id) VALUES(%s) ON DUPLICATE KEY UPDATE generation=generation+1',(fid,))
  elif job['task_kind']=='match_file':
   manual_entry(W,db,fid);verify_downloader_post(W,db,fid);complete=compare(W,db,fid,job['id'])
   if not complete:
    F.execute(db,"UPDATE fm_work_items SET state='pending',available_at=UTC_TIMESTAMP(6),lease_owner=NULL,lease_token=NULL,lease_expires_at=NULL WHERE id=%s AND lease_token=%s",(job['id'],token));return
   allowed,reason=can_manage(db,current[0]['relative_path'],fid)
   if allowed:W.organize(db,[fid])
   else:F.execute(db,'INSERT INTO fm_deferred_files(file_id,reason) VALUES(%s,%s) ON DUPLICATE KEY UPDATE reason=VALUES(reason)',(fid,reason))
 F.execute(db,'UPDATE fm_work_items SET state=%s,lease_owner=NULL,lease_token=NULL,lease_expires_at=NULL,error=NULL WHERE id=%s AND lease_token=%s',(state,job['id'],token))

def match_ready(db,job):
 current=F.rows(db,'SELECT * FROM fm_file_catalog WHERE id=%s',(job['file_id'],))
 return bool(current and current[0]['is_present'] and not current[0]['canonical_file_id'] and current[0]['byte_size']==job['expected_size'] and current[0]['modified_utc_ticks']==job['expected_ticks'])

def match_visual(W,job):
 # A consistent read snapshot lets this stage overlap file changes safely.
 with active_stage('visual'):
  with W.connect() as db:
   db.begin()
   try:return compare(W,db,job['file_id'],job['id'],phase='visual_collect')
   finally:db.rollback()

def apply_visual_matches(W,db,fid,matches):
 a=enriched(W,db,fid)
 if not a or a['visual_hash'] is None:return
 for candidate_id,_ in matches:
  b=enriched(W,db,candidate_id)
  if not b or b['visual_hash'] is None:continue
  if a['pixel_sha256'] is not None and a['pixel_sha256']==b['pixel_sha256'] and render_signature(a)==render_signature(b):continue
  if abs((a['display_width']/a['display_height'])/(b['display_width']/b['display_height'])-1)>.02:continue
  distance=(int.from_bytes(a['visual_hash'],'big')^int.from_bytes(b['visual_hash'],'big')).bit_count()
  if distance<=5:save_match(W,db,a,b,'visual_similarity',distance)

def mutation_keys(db,fid):
 a=enriched(None,db,fid)
 if not a:return [('file',fid)]
 # Serialize shared files and destination paths, not an entire collection.
 keys={('file',fid),('path',a['relative_path'].casefold())}
 cats=[str(row['category'] or '').lower() for row in F.rows(db,'SELECT DISTINCT e.category FROM fm_entry_file_projection l JOIN inventory_entries e ON e.id=l.entry_id WHERE l.file_id=%s AND e.deleted_at IS NULL',(fid,))]
 chosen=next((category for category in ('nsfw','sketchy','sfw') if category in cats),'not_provided')
 target=Path(a['owner'],chosen,Path(a['relative_path']).name).as_posix()
 keys.add(('path',target.casefold()))
 for kind,value,algorithm in [('file_sha256',a['file_sha256'],'sha256'),('pixel_sha256',a['pixel_sha256'],'pillow-rgba-frames-sha256')]:
  if value is None:continue
  candidates=F.rows(db,"SELECT h.file_id,f.relative_path FROM file_hashes h JOIN fm_file_catalog f ON f.id=h.file_id WHERE h.hash_kind=%s AND h.algorithm=%s AND h.algorithm_version='1' AND h.hash_value=%s AND h.source_byte_size=f.byte_size AND h.source_modified_utc_ticks=f.modified_utc_ticks AND f.is_present=TRUE AND f.canonical_file_id IS NULL",(kind,algorithm,value))
  keys.update(('file',row['file_id']) for row in candidates)
  keys.update(('path',row['relative_path'].casefold()) for row in candidates)
 return keys

def mutate_match(W,token,job,visual_result):
 with active_stage('mutation'):
  try:
   with W.connect() as db:
    try:
     for attempt in range(3):
      keys=set(mutation_keys(db,job['file_id']))
      with mutation_locks(keys):
       if not set(mutation_keys(db,job['file_id'])).issubset(keys):continue
       if not match_ready(db,job):state='cancelled'
       else:
        fid=job['file_id'];manual_entry(W,db,fid);verify_downloader_post(W,db,fid)
        compare(W,db,fid,job['id'],phase='exact')
        if not match_ready(db,job):state='cancelled'
        elif isinstance(visual_result,Exception):raise visual_result
        else:
         visual_complete,visual_matches,visual_last=visual_result
         apply_visual_matches(W,db,fid,visual_matches)
         if not visual_complete:
          F.execute(db,'INSERT INTO fm_match_progress VALUES(%s,%s) ON DUPLICATE KEY UPDATE last_candidate_id=VALUES(last_candidate_id)',(job['id'],visual_last))
          F.execute(db,"UPDATE fm_work_items SET state='pending',available_at=UTC_TIMESTAMP(6),lease_owner=NULL,lease_token=NULL,lease_expires_at=NULL WHERE id=%s AND lease_token=%s",(job['id'],token));return
         F.execute(db,'DELETE FROM fm_match_progress WHERE work_id=%s',(job['id'],))
         current=F.rows(db,'SELECT relative_path FROM fm_file_catalog WHERE id=%s',(fid,))[0]
         allowed,reason=can_manage(db,current['relative_path'],fid)
         if allowed:W.organize(db,[fid])
         else:F.execute(db,'INSERT INTO fm_deferred_files(file_id,reason) VALUES(%s,%s) ON DUPLICATE KEY UPDATE reason=VALUES(reason)',(fid,reason))
         state='completed'
       F.execute(db,'UPDATE fm_work_items SET state=%s,lease_owner=NULL,lease_token=NULL,lease_expires_at=NULL,error=NULL WHERE id=%s AND lease_token=%s',(state,job['id'],token))
       return
     raise RuntimeError('Exact-match files changed while acquiring mutation locks; retrying this job later.')
    except Exception as error:
     if getattr(error,'args',()) and error.args[0] in (1205,1213):
      db.rollback()
      F.execute(db,"UPDATE fm_work_items SET state='pending',available_at=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 60 SECOND),lease_owner=NULL,lease_token=NULL,lease_expires_at=NULL,error='Temporary database lock conflict; retrying' WHERE id=%s AND lease_token=%s",(job['id'],token))
     else:record_failure(db,token,job,error)
  except Exception as error:
   # Connection failure before a job can be updated is retried when its lease expires.
   print('Mutation work item '+str(job['id'])+': '+''.join(traceback.format_exception(error)),flush=True)

def process_pipeline(W,db,once=False):
 # The coordinator is the only claimer. Completed visual scans feed mutation
 # workers immediately, while visual workers continue into the next claim.
 deadline=time.monotonic()+(30 if once else PIPELINE_SECONDS)
 waiting=deque();visual={};mutations=set();claimed=0;last_report=time.monotonic();last_tune=last_report;empty=False;visual_limit=VISUAL_WORKERS
 last_visual=last_mutation=0;last_index=index_counts()[1]
 reset_activity_counts()
 with ThreadPoolExecutor(max_workers=MAX_VISUAL_WORKERS,thread_name_prefix='gallery-visual') as visual_pool,ThreadPoolExecutor(max_workers=MUTATION_WORKERS,thread_name_prefix='gallery-mutate') as mutation_pool:
  while True:
   capacity=MAX_PIPELINE_JOBS-len(waiting)-len(visual)-len(mutations)
   if capacity>0 and (capacity>=CLAIM_BATCH_SIZE or (not waiting and len(visual)<visual_limit)) and not empty and time.monotonic()<deadline:
    token,jobs=claim(db,min(CLAIM_BATCH_SIZE,capacity))
    if jobs:
     claimed+=len(jobs)
     waiting.extend((token,job) for job in jobs)
    else:empty=True
   while waiting and len(visual)<visual_limit:
    token,job=waiting.popleft();visual[visual_pool.submit(match_visual,W,job)]=(token,job)
   if not waiting and not visual and not mutations:break
   done,_=wait(set(visual)|mutations,timeout=5,return_when=FIRST_COMPLETED)
   for future in done:
    if future in visual:
     token,job=visual.pop(future)
     try:result=future.result()
     except Exception as error:result=error
     mutations.add(mutation_pool.submit(mutate_match,W,token,job,result))
    else:
     mutations.remove(future);future.result()
   now=time.monotonic();metrics=active_counts()
   if now-last_tune>=60:
    visual_limit=recommended_visual_limit(visual_limit,metrics,len(mutations),bool(waiting or visual))
    last_tune=now
   if now-last_report>=30:
    pending=F.rows(db,"SELECT COUNT(*) n FROM fm_work_items WHERE state IN ('pending','leased')")[0]['n']
    dirty=F.rows(db,'SELECT (SELECT COUNT(*) FROM fm_entry_dirty)+(SELECT COUNT(*) FROM fm_file_dirty) n')[0]['n']
    failed=F.rows(db,"SELECT COUNT(*) n FROM fm_work_items WHERE state='failed'")[0]['n']
    index_active,indexed_total=index_counts()
    minutes=(now-last_report)/60
    visual_rate=round((metrics['completed']['visual']-last_visual)/minutes,1)
    mutation_rate=round((metrics['completed']['mutation']-last_mutation)/minutes,1)
    index_rate=round((indexed_total-last_index)/minutes,1)
    W.progress(db,'Matching images through visual and file-change stages',phase='scanning',pending_work=pending,pending_index=dirty,failed_count=failed,visual_active=metrics['active']['visual'],visual_waiting=len(waiting),file_changes_active=metrics['active']['mutation'],file_changes_waiting=max(0,len(mutations)-metrics['active']['mutation']),visual_worker_limit=visual_limit,file_change_worker_limit=MUTATION_WORKERS,index_worker_limit=INDEX_WORKERS,index_active=index_active,indexed_total=indexed_total,visual_per_minute=visual_rate,file_changes_per_minute=mutation_rate,indexed_per_minute=index_rate,claimed_this_pass=claimed)
    last_visual=metrics['completed']['visual'];last_mutation=metrics['completed']['mutation'];last_index=indexed_total
    last_report=now
 return claimed

def record_failure(db,token,job,error):
 db.rollback()
 F.execute(db,"UPDATE fm_work_items SET state=IF(failure_count>=4,'failed','pending'),failure_count=failure_count+1,available_at=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 60 SECOND),error=%s,lease_token=NULL,lease_owner=NULL,lease_expires_at=NULL WHERE id=%s AND lease_token=%s",(str(error),job['id'],token))
 print('Work item '+str(job['id'])+': '+''.join(traceback.format_exception(error)),flush=True)

def index_loop(W,kind):
 # Each thread owns a connection. drain() locks and skips rows claimed by peers.
 global _index_active,_indexed_total
 while True:
  db=None
  try:
   db=W.connect()
   while True:
    db.ping(reconnect=True)
    with _index_guard:_index_active+=1
    try:
     try:indexed=F.drain(db,batches=1,size=INDEX_BATCH_SIZE,kind=kind)
     except Exception as error:
      if getattr(error,'args',()) and error.args[0] in (1205,1213):
       # Shared assets can make unrelated entries contend. Retry the row.
       time.sleep(random.uniform(.05,.2));continue
      raise
    finally:
     with _index_guard:_index_active-=1
    with _index_guard:_indexed_total+=indexed
    if not indexed:time.sleep(1)
  except Exception:
   print('Index worker: '+traceback.format_exc(),flush=True)
   time.sleep(2)
  finally:
   if db:
    try:db.close()
    except Exception:pass

def tag_loop(W):
 import TagSync
 while True:
  db=None
  try:
   db=W.connect()
   while True:
    db.ping(reconnect=True)
    synced=TagSync.sync_batch(db,20)
    time.sleep(5 if synced else 30)
  except Exception:
   print('Tag sync worker: '+traceback.format_exc(),flush=True)
   time.sleep(2)
  finally:
   if db:
    try:db.close()
    except Exception:pass

def main(W,once=False):
 if not once:
  for index in range(INDEX_WORKERS):
   kind='entry' if index==0 else 'file'
   threading.Thread(target=index_loop,args=(W,kind),name='gallery-index-'+kind+'-'+str(index+1),daemon=True).start()
  threading.Thread(target=tag_loop,args=(W,),name='gallery-tag-sync',daemon=True).start()
 last_scan=0;last_status=None;last_metrics=0;pending=dirty=failed=0;issues=[]
 while True:
  db=None;scanned=False
  try:
   db=W.connect()
   if not F.rows(db,"SELECT GET_LOCK('wallpapers_database_worker',0) acquired")[0]['acquired']:
    db.close();db=None
    if once:return
    time.sleep(5);continue
   W.require_root()
   import RecoveryActions
   RecoveryActions.drain(W,db)
   if once:
    import TagSync
    TagSync.sync_batch(db,100)
   import CollectionMoves
   moved=CollectionMoves.process(W,db)
   requested=W.state(db,'worker_requested').get('requested',False);imports=W.state(db,'import_updates').get('requested',False)
   initial_scan_due=False
   if last_scan==0:
    W.progress(db,'Recovering file operations before matching images')
    W.recover(db)
    initial_scan_due=not F.rows(db,"SELECT id FROM fm_work_items WHERE task_kind='match_file' AND state='pending' LIMIT 1")
    last_scan=time.monotonic()
   scan_interval=BACKLOG_SCAN_INTERVAL if pending else 300
   if not moved and (initial_scan_due or time.monotonic()-last_scan>scan_interval or requested or imports):
    W.progress(db,'Discovering collections and checking file changes')
    if requested:W.recover(db)
    count=reconcile(W,db);last_scan=time.monotonic()
    scanned=True
    W.import_documents(db,imports)
    if imports:W.state(db,'import_updates',{'requested':False})
    if requested:F.execute(db,"UPDATE fm_work_items SET state='pending',failure_count=0,available_at=UTC_TIMESTAMP(6) WHERE state='failed'")
    W.state(db,'worker_requested',{'requested':False})
   import CurrentLocations
   if scanned:CurrentLocations.housekeeping(db)
   indexed=F.drain(db,batches=2) if once else 0
   token,jobs=claim(db,limit=20,kind='other')
   for job in jobs:
    try:process(W,db,token,job)
    except Exception as error:record_failure(db,token,job,error)
   matched=process_pipeline(W,db,once=once)
   CollectionMoves.process(W,db)
   if scanned or time.monotonic()-last_metrics>=30:
    pending=F.rows(db,"SELECT COUNT(*) n FROM fm_work_items WHERE state IN ('pending','leased')")[0]['n'];dirty=F.rows(db,'SELECT COUNT(*) n FROM fm_entry_dirty')[0]['n']+F.rows(db,'SELECT COUNT(*) n FROM fm_file_dirty')[0]['n'];failed=F.rows(db,"SELECT COUNT(*) n FROM fm_work_items WHERE state='failed'")[0]['n']
    issues=F.rows(db,"SELECT f.relative_path file,w.error FROM fm_work_items w JOIN fm_file_catalog f ON f.id=w.file_id WHERE w.state='failed' ORDER BY w.id LIMIT 10");last_metrics=time.monotonic()
   if (pending,dirty)!=last_status or jobs or matched or indexed or scanned:
    index_active,indexed_total=index_counts()
    W.progress(db,'File manager processing changes' if pending or dirty else 'Some files need attention' if failed else 'Collections are up to date',0,pending+dirty,phase='scanning' if pending or dirty else 'complete',pending_work=pending,pending_index=dirty,updated_files=indexed,failed_count=failed,issues=issues,index_worker_limit=INDEX_WORKERS,index_active=index_active,indexed_total=indexed_total)
    last_status=(pending,dirty)
  except Exception as error:
   print(traceback.format_exc(),flush=True)
   if db:
    try:W.progress(db,str(error),phase='error')
    except Exception:pass
   if once:raise
  finally:
   if db:
    try:F.rows(db,"SELECT RELEASE_LOCK('wallpapers_database_worker')")
    except Exception:pass  # A disconnected session has already released its lock.
    finally:db.close()
  if once:return
  time.sleep(1 if pending or dirty else 5)
