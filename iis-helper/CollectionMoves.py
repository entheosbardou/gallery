"""Journaled entry reassignment and verified physical file moves."""
from pathlib import Path
import json,hashlib,shutil
import FileManager as F
import FileManagerWorker as M

def move_file(W,db,job,item,target):
 category_move=job.get('target_category')
 f=F.rows(db,'SELECT f.*,c.collection_key owner FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id WHERE f.id=%s',(item['file_id'],))[0]
 if not f['is_present']:raise RuntimeError('A saved file is unavailable; reconnect storage and retry.')
 if not M.can_manage(db,f['relative_path'],f['id'])[0]:raise RuntimeError('The source downloader database must be ready before moving this file.')
 op=None
 if item['operation_id']:op=F.rows(db,'SELECT * FROM file_operations WHERE id=%s',(item['operation_id'],))[0]
 if op is None:
  if f['collection_id']==target['id'] and not category_move:
   F.execute(db,"UPDATE fm_collection_move_files SET state='completed' WHERE move_id=%s AND file_id=%s",(job['id'],f['id']));return
  if category_move and W.path_category(f['relative_path'],f['owner'])==category_move:
   F.execute(db,"UPDATE fm_collection_move_files SET state='completed' WHERE move_id=%s AND file_id=%s",(job['id'],f['id']));return
  category=category_move or str(W.path_category(f['relative_path'],f['owner']) or 'not_provided')
  name=Path(f['relative_path']).name;relative='/'.join(x for x in [target['collection_key'] if target['collection_key']!='.' else '',category,name] if x)
  if W.nearest_collection(relative,W.marker_keys(db))!=target['collection_key']:raise RuntimeError('The destination falls inside a nested collection; choose that collection instead.')
  allowed,why=M.can_relocate(db,f['id'],relative)
  if not allowed:raise RuntimeError(why)
  digest=W.filehash(W.safe(f['relative_path']))
  if W.safe(relative).exists() or F.rows(db,'SELECT id FROM fm_file_catalog WHERE path_key=%s',(W.pathkey(relative),)):
   name=Path(name).stem+'-move-'+str(job['id'])+'-'+str(f['id'])+Path(name).suffix
   relative='/'.join(x for x in [target['collection_key'] if target['collection_key']!='.' else '',category,name] if x)
   if W.safe(relative).exists() or F.rows(db,'SELECT id FROM fm_file_catalog WHERE path_key=%s',(W.pathkey(relative),)):raise RuntimeError('Destination already exists; no file was overwritten.')
  db.begin()
  try:
   oid,_=F.execute(db,"INSERT INTO file_operations(operation_kind,source_file_id,target_file_id,source_path,target_path,source_sha256,target_sha256) VALUES('organize',%s,%s,%s,%s,%s,%s)",(f['id'],f['id'],f['relative_path'],relative,digest,digest))
   F.execute(db,'UPDATE fm_collection_move_files SET operation_id=%s WHERE move_id=%s AND file_id=%s',(oid,job['id'],f['id']));db.commit()
  except Exception:db.rollback();raise
  op=F.rows(db,'SELECT * FROM file_operations WHERE id=%s',(oid,))[0]
 source=W.safe(op['source_path']);dest=W.safe(op['target_path']);digest=bytes(op['source_sha256'])
 if f['relative_path'] not in (op['source_path'],op['target_path']):raise RuntimeError('This file moved again; review its current location before retrying.')
 dest.parent.mkdir(parents=True,exist_ok=True)
 def link(source_stream=None,target_stream=None):
  current=F.rows(db,'SELECT f.*,c.collection_key owner FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id WHERE f.id=%s',(f['id'],))[0]
  new=dict(current,relative_path=op['target_path'],owner=target['collection_key'],collection_id=target['id'],file_sha256=digest)
  if current['relative_path']==op['source_path']:
   M.prepare_sync(db,current,new,W.ROOT,source_stream=source_stream,target_stream=target_stream)
   db.begin()
   try:
    locked=F.rows(db,'SELECT relative_path FROM fm_file_catalog WHERE id=%s FOR UPDATE',(f['id'],))[0]
    if locked['relative_path']!=op['source_path']:raise RuntimeError('File location changed during the move; original retained.')
    F.execute(db,'UPDATE files SET relative_path=%s,path_key=%s,file_name=%s WHERE id=%s',(op['target_path'],W.pathkey(op['target_path']),Path(op['target_path']).name,f['id']))
    M.synchronize_paths(db,current,new,W.ROOT)
    F.execute(db,'INSERT INTO fm_file_placement(file_id,collection_id) VALUES(%s,%s) ON DUPLICATE KEY UPDATE collection_id=VALUES(collection_id)',(f['id'],target['id']))
    F.execute(db,"UPDATE file_operations SET state='linked',error=NULL WHERE id=%s",(op['id'],));db.commit()
   except Exception:db.rollback();raise
  else:
   F.execute(db,'INSERT INTO fm_file_placement(file_id,collection_id) VALUES(%s,%s) ON DUPLICATE KEY UPDATE collection_id=VALUES(collection_id)',(f['id'],target['id']))
 if source.exists():
  with W.protected(source,True) as original:
   if W.hashstream(original)!=digest:raise RuntimeError('Source changed after the move was queued; original retained.')
   if not dest.exists():
    with dest.open('xb') as output:shutil.copyfileobj(original,output)
    shutil.copystat(source,dest)
   with W.protected(dest) as retained:
    if W.hashstream(retained)!=digest:raise RuntimeError('Destination checksum differs; original retained.')
    F.execute(db,"UPDATE file_operations SET state='copied' WHERE id=%s",(op['id'],));link(original,retained);W.delete_protected(original)
 else:
  with W.protected(dest) as retained:
   if W.hashstream(retained)!=digest:raise RuntimeError('Recovery destination checksum differs.')
   link(target_stream=retained)
 F.execute(db,"UPDATE file_operations SET state='complete',source_path='',target_path='',error=NULL,completed_at=UTC_TIMESTAMP(6) WHERE id=%s",(op['id'],))
 F.execute(db,"UPDATE fm_collection_move_files SET state='completed' WHERE move_id=%s AND file_id=%s",(job['id'],f['id']))

def finish(W,db,job,target):
 db.begin()
 try:
  row=F.rows(db,'SELECT e.*,c.collection_key owner FROM inventory_entries e JOIN inventory_documents d ON d.id=e.inventory_document_id JOIN collections c ON c.id=e.collection_id WHERE e.id=%s AND e.deleted_at IS NULL FOR UPDATE',(job['entry_id'],))
  if not row:raise RuntimeError('The entry was deleted while the move was pending.')
  e=row[0]
  if e['collection_id'] not in (job['source_collection_id'],target['id']):raise RuntimeError('The entry was reassigned while this move was pending; review before retrying.')
  def patch(r):
   import CurrentLocations
   cleaned=CurrentLocations.clean(r);r.clear();r.update(cleaned)
   if job.get('target_category'):r['category']=job['target_category']
   else:r['collection_override']=target['collection_key'];r['download_collection']=target['collection_key']
  if not W.update_entry(db,e['id'],patch,'collection_manually_assigned'):raise RuntimeError('The entry changed; retry the move.')
  if not job.get('target_category'):F.execute(db,'UPDATE inventory_entries SET collection_id=%s WHERE id=%s',(target['id'],e['id']))
  # A parent move also reassigns its individual children; provenance stays intact.
  if not job.get('target_category'):F.execute(db,"UPDATE inventory_entries child JOIN inventory_entry_relations r ON r.related_entry_id=child.id SET child.collection_id=%s WHERE r.entry_id=%s AND r.relation_kind='parent_of'",(target['id'],e['id']))
  F.execute(db,"UPDATE fm_collection_moves SET state='completed',source_collection_id=NULL,error=NULL,completed_at=UTC_TIMESTAMP(6) WHERE id=%s",(job['id'],));db.commit()
 except Exception:db.rollback();raise

def process(W,db):
 jobs=F.rows(db,"SELECT * FROM fm_collection_moves WHERE state IN ('pending','running') ORDER BY id LIMIT 1")
 if not jobs:return False
 job=jobs[0]
 try:
  setting=F.rows(db,'SELECT value FROM gallery_state WHERE state_key=%s',('category_move:'+str(job['id']),))
  if setting:job['target_category']=json.loads(setting[0]['value'])['category']
  W.require_root();target=F.rows(db,'SELECT c.id,c.collection_key FROM collections c JOIN fm_collection_discovery m ON m.collection_id=c.id WHERE c.id=%s AND m.is_marked=TRUE',(job['target_collection_id'],))
  if not target or not (W.ROOT/target[0]['collection_key']/'_download-info').is_dir():raise RuntimeError('Destination collection marker is unavailable.')
  target=target[0]
  entry=F.rows(db,'SELECT collection_id FROM inventory_entries WHERE id=%s AND deleted_at IS NULL',(job['entry_id'],))
  if not entry or entry[0]['collection_id'] not in (job['source_collection_id'],target['id']):raise RuntimeError('The entry was reassigned while the move was pending; review before retrying.')
  F.execute(db,"UPDATE fm_collection_moves SET state='running',error=NULL WHERE id=%s",(job['id'],))
  files=F.rows(db,"SELECT * FROM fm_collection_move_files WHERE move_id=%s AND state='pending' ORDER BY file_id LIMIT 10",(job['id'],))
  # Validate all source-owner gates before starting this batch.
  for item in files:
   f=F.rows(db,'SELECT relative_path FROM fm_file_catalog WHERE id=%s',(item['file_id'],))[0]
   if not M.can_manage(db,f['relative_path'],item['file_id'])[0]:raise RuntimeError('A downloader integration is not ready; files retained. Retry after its migration.')
  for item in files:move_file(W,db,job,item,target)
  if not F.rows(db,"SELECT file_id FROM fm_collection_move_files WHERE move_id=%s AND state='pending' LIMIT 1",(job['id'],)):finish(W,db,job,target)
  return True
 except Exception as error:
  db.rollback();F.execute(db,"UPDATE fm_collection_moves SET state='failed',error=%s WHERE id=%s",(str(error),job['id']));return True
