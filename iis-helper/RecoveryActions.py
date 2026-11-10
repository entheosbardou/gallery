"""Administrator recovery decisions, executed under the existing worker lock/account."""
import json,traceback,shutil,os,hashlib,re
PREFIX='recovery_action:'

def resolve_reviewed_duplicate(W,db,request,key):
 import FileManager as F
 source_id=int(request['source_file_id']);target_id=int(request['target_file_id']);expected=bytes.fromhex(request['sha256'])
 if source_id==target_id or len(expected)!=32:raise RuntimeError('Choose two different exact-match files.')
 found=F.rows(db,'SELECT id,relative_path,byte_size,is_present,canonical_file_id FROM fm_file_catalog WHERE id IN (%s,%s)',(source_id,target_id))
 if len(found)!=2:raise RuntimeError('A reviewed file record is missing.')
 files={f['id']:f for f in found};source=files[source_id];target=files[target_id]
 if any(not f['is_present'] or f['canonical_file_id'] for f in (source,target)) or source['relative_path']!=request['source_path'] or target['relative_path']!=request['target_path'] or source['byte_size']!=target['byte_size']:raise RuntimeError('The reviewed files changed. Review them again.')
 with W.protected(W.safe(source['relative_path'])) as first,W.protected(W.safe(target['relative_path'])) as kept:
  if first.tell()!=0 or kept.tell()!=0 or first.seek(0,2)!=kept.seek(0,2):raise RuntimeError('The file lengths differ. No duplicate was merged.')
  if W.hashstream(first)!=expected or W.hashstream(kept)!=expected:raise RuntimeError('The file bytes changed. No duplicate was merged.')
  kept.seek(0);md5=hashlib.md5()
  for block in iter(lambda:kept.read(1024*1024),b''):md5.update(block)
  size=kept.tell()
  if size!=source['byte_size']:raise RuntimeError('The saved file size changed. No duplicate was merged.')
  db.begin()
  try:
   locked=F.rows(db,'SELECT id,relative_path,is_present,canonical_file_id FROM files WHERE id IN (%s,%s) ORDER BY id FOR UPDATE',(source_id,target_id))
   if len(locked)!=2 or any(not f['is_present'] or f['canonical_file_id'] or f['relative_path']!=files[f['id']]['relative_path'] for f in locked):raise RuntimeError('The file pointers changed. Review them again.')
   config=F.rows(db,"SELECT c.database_name,c.enabled,c.adapter_kind FROM fm_adapter_config c WHERE c.integration_key='booru'")
   if len(config)!=1 or not config[0]['enabled'] or config[0]['adapter_kind']!='booru-locations':raise RuntimeError('The Booru downloader integration is unavailable.')
   schema=config[0]['database_name']
   if not re.fullmatch('[A-Za-z0-9_-]+',schema):raise RuntimeError('Invalid downloader database name.')
   bindings=F.rows(db,"SELECT b.file_id,b.external_id,b.last_known_path FROM fm_external_bindings b JOIN fm_integrations i ON i.id=b.integration_id WHERE i.integration_key='booru' AND b.file_id IN (%s,%s) ORDER BY b.file_id FOR UPDATE",(source_id,target_id))
   if len(bindings)!=2 or {b['file_id'] for b in bindings}!={source_id,target_id}:raise RuntimeError('The downloader links changed. Review them again.')
   bound={b['file_id']:b for b in bindings}
   locations=F.rows(db,f'SELECT l.id,l.file_id,l.relative_path,l.status,f.md5,f.sha256,f.size_bytes FROM `{schema}`.file_locations l JOIN `{schema}`.files f ON f.id=l.file_id WHERE l.id IN (%s,%s) ORDER BY l.id FOR UPDATE',(int(bound[source_id]['external_id']),int(bound[target_id]['external_id'])))
   if len(locations)!=2:raise RuntimeError('A downloader location changed. Review it again.')
   byid={l['id']:l for l in locations};old=byid[int(bound[source_id]['external_id'])];owner=byid[int(bound[target_id]['external_id'])]
   if old['file_id']==owner['file_id'] or any(l['status']!='present' for l in (old,owner)) or any(l['relative_path']!=bound[fid]['last_known_path'] or l['relative_path']!=files[fid]['relative_path'] for fid,l in ((source_id,old),(target_id,owner))):raise RuntimeError('The downloader locations changed. Review them again.')
   if owner['sha256']!=expected.hex() or owner['md5']!=md5.hexdigest() or owner['size_bytes']!=size:raise RuntimeError('The retained downloader record does not match verified bytes.')
   remaining=F.rows(db,f"SELECT COUNT(*) n FROM `{schema}`.file_locations WHERE file_id=%s AND status='present'",(old['file_id'],))[0]['n']
   if remaining!=1:raise RuntimeError('The old downloader record has another present location. Review its links.')
   F.execute(db,f'INSERT IGNORE INTO `{schema}`.media_files(media_id,file_id) SELECT media_id,%s FROM `{schema}`.media_files WHERE file_id=%s',(owner['file_id'],old['file_id']))
   F.execute(db,f'UPDATE `{schema}`.file_locations SET file_id=%s,last_checked_at=UTC_TIMESTAMP(6) WHERE id=%s AND file_id=%s',(owner['file_id'],old['id'],old['file_id']))
   F.execute(db,f'DELETE FROM `{schema}`.media_files WHERE file_id=%s',(old['file_id'],))
   if F.rows(db,f'SELECT id FROM `{schema}`.file_locations WHERE file_id=%s LIMIT 1',(old['file_id'],)):raise RuntimeError('The old downloader record still has a location.')
   # All posts and locations now reference the retained identity. Remove the
   # obsolete downloader file row after verifying it has no remaining links.
   F.execute(db,f'DELETE FROM `{schema}`.files WHERE id=%s',(old['file_id'],))
   F.execute(db,"UPDATE fm_work_items SET state='pending',failure_count=0,available_at=UTC_TIMESTAMP(6),error=NULL WHERE file_id IN (%s,%s) AND task_kind='match_file' AND state='failed'",(source_id,target_id))
   request.update(status='done',message='Downloader posts now share one verified file record; physical duplicate removal queued.')
   F.execute(db,'UPDATE gallery_state SET value=%s WHERE state_key=%s',(W.dumps(request),key))
   F.execute(db,"INSERT INTO gallery_state(state_key,value) VALUES('worker_requested',%s) ON DUPLICATE KEY UPDATE value=VALUES(value)",(W.dumps({'requested':True}),))
   db.commit()
  except Exception:db.rollback();raise

def same_plan(a,b):
 return all(a.get(k)==b.get(k) for k in ('source_file_id','target_file_id','source_path','target_path'))

def verify_originals(W,db,op,request):
 files=[]
 for side in ('source','target'):
  rows=W.q(db,'SELECT * FROM files WHERE id=%s',(op[side+'_file_id'],))
  if not rows:raise RuntimeError('A file record is missing. Retry discovery before choosing a copy.')
  f=rows[0]
  if f['canonical_file_id'] or f['relative_path']!=op[side+'_path']:
   raise RuntimeError('These copies have already been moved or merged. Use the saved-copy resolution or retry cleanup to reconcile their current locations.')
  path=W.safe(op[side+'_path'])
  reviewed=request.get('reviewed_'+side+'_sha256','').lower()
  if not re.fullmatch('[0-9a-f]{64}',reviewed):raise RuntimeError('Review both current files before applying this decision.')
  with W.protected(path) as stream:
   if W.hashstream(stream).hex()!=reviewed:raise RuntimeError('The '+side+' copy changed since your review. Review it again.')
  files.append(f)
 return files

def finish_reviewed_move(W,db,op,request):
 import CollectionMoves as C
 import FileManager as F
 with F.asset_write_lock:
  linked=W.q(db,'SELECT move_id,file_id,state FROM fm_collection_move_files WHERE operation_id=%s',(op['id'],))
  if len(linked)!=1 or linked[0]['state']!='pending':raise RuntimeError('This move changed. Review it again.')
  found=W.q(db,'SELECT * FROM fm_collection_moves WHERE id=%s',(linked[0]['move_id'],))
  if len(found)!=1 or found[0]['state']!='failed':raise RuntimeError('This move is no longer failed. Refresh it.')
  job=found[0]
  setting=W.q(db,'SELECT value FROM gallery_state WHERE state_key=%s',('category_move:'+str(job['id']),))
  if setting:job['target_category']=W.decode(setting[0]['value'])['category']
  target=W.q(db,'SELECT id,collection_key FROM collections WHERE id=%s',(job['target_collection_id'],))
  if len(target)!=1:raise RuntimeError('The target collection is unavailable.')
  source=W.safe(op['source_path']);dest=W.safe(op['target_path'])
  expected=request.get('reviewed_source_sha256','').lower()
  if not re.fullmatch('[0-9a-f]{64}',expected):raise RuntimeError('Review the original file before finishing this move.')
  with W.protected(source,True) as original:
   if W.hashstream(original).hex()!=expected:raise RuntimeError('The original changed since your review. Review it again.')
   if dest.exists():
    reviewed_target=request.get('reviewed_target_sha256','').lower()
    if not re.fullmatch('[0-9a-f]{64}',reviewed_target):raise RuntimeError('Review the destination copy before replacing it.')
    with W.protected(dest,True) as previous:
     if W.hashstream(previous).hex()!=reviewed_target:raise RuntimeError('The destination changed since your review. Review it again.')
     W.delete_protected(previous)
   elif request.get('reviewed_target_sha256'):raise RuntimeError('The destination changed since your review. Review it again.')
  W.run(db,'UPDATE file_operations SET source_sha256=%s,target_sha256=%s WHERE id=%s',(bytes.fromhex(expected),bytes.fromhex(expected),op['id']))
  C.move_file(W,db,job,linked[0],target[0])
  if not W.q(db,"SELECT file_id FROM fm_collection_move_files WHERE move_id=%s AND state='pending' LIMIT 1",(job['id'],)):C.finish(W,db,job,target[0])
  else:W.run(db,"UPDATE fm_collection_moves SET state='pending',error=NULL WHERE id=%s",(job['id'],))
  return 'Your reviewed file was moved and its category updated.'

def apply(W,db,op,request):
 choice=request['choice']
 if choice=='retry':
  W.run(db,"UPDATE file_operations SET error=NULL,state=IF(state='error','planned',state) WHERE id=%s",(op['id'],))
  W.recover(db,operation_id=op['id'])
 elif choice=='finish_move_manual':
  if op['operation_kind']!='organize':raise RuntimeError('This decision applies to file moves only.')
  return finish_reviewed_move(W,db,op,request)
 elif choice in ('first','second','both','cancel_move'):
  is_move=op['operation_kind']=='organize'
  if choice=='cancel_move':
   if not is_move:raise RuntimeError('Keeping the original location applies to a move only.')
   path=W.safe(op['source_path'])
   reviewed=request.get('reviewed_source_sha256','').lower()
   if not re.fullmatch('[0-9a-f]{64}',reviewed):raise RuntimeError('Review the original file before cancelling this move.')
   with W.protected(path) as stream:
    if W.hashstream(stream).hex()!=reviewed:raise RuntimeError('The original file changed since your review. Review it again.')
   current=W.q(db,'SELECT * FROM files WHERE id=%s',(op['source_file_id'],))[0]
   if current['relative_path']!=op['source_path'] or current['canonical_file_id']:raise RuntimeError('The move already updated its links. Finish the move instead.')
   if W.safe(op['target_path']).exists():raise RuntimeError('A destination copy already exists. Finish the move to verify and reconcile both locations.')
   W.run(db,"UPDATE file_operations SET state='cancelled',completed_at=UTC_TIMESTAMP(6) WHERE id=%s",(op['id'],))
   return 'Resolved. The verified original stays in its original location.'
  if is_move:raise RuntimeError('This is a file move. Choose Finish move or Keep original location.')
  originals=verify_originals(W,db,op,request)
  if W.q(db,'SELECT move_id FROM fm_collection_move_files WHERE operation_id=%s LIMIT 1',(op['id'],)):
   raise RuntimeError('This file belongs to a collection move. Retry the move instead of merging its files.')
  if choice=='both':
   db.begin()
   try:
    W.run(db,"UPDATE duplicate_matches SET review_state='rejected' WHERE file_id_low=%s AND file_id_high=%s",(min(op['source_file_id'],op['target_file_id']),max(op['source_file_id'],op['target_file_id'])))
    W.run(db,"UPDATE file_operations SET state='cancelled',completed_at=UTC_TIMESTAMP(6) WHERE id=%s",(op['id'],))
    db.commit()
   except Exception:db.rollback();raise
   return 'Resolved. Both verified files are kept, with their existing records.'
  discard,keeper=(originals[1],originals[0]) if choice=='first' else (originals[0],originals[1])
  discard_path=W.safe(discard['relative_path']);keeper_path=W.safe(keeper['relative_path'])
  with W.protected(keeper_path) as kept,W.protected(discard_path,True) as removed:
   if W.hashstream(kept).hex()!=request['reviewed_'+('source' if choice=='first' else 'target')+'_sha256'].lower() or W.hashstream(removed).hex()!=request['reviewed_'+('target' if choice=='first' else 'source')+'_sha256'].lower():raise RuntimeError('A copy changed since your review. Review it again.')
   W.rewrite_path(db,discard,keeper,source_stream=removed,target_stream=kept)
   W.run(db,"UPDATE file_operations SET source_file_id=%s,target_file_id=%s,source_path=%s,target_path=%s,source_sha256=%s,target_sha256=%s,state='linked',error=NULL WHERE id=%s",(discard['id'],keeper['id'],discard['relative_path'],keeper['relative_path'],bytes.fromhex(request['reviewed_'+('target' if choice=='first' else 'source')+'_sha256']),bytes.fromhex(request['reviewed_'+('source' if choice=='first' else 'target')+'_sha256']),op['id']))
   W.delete_protected(removed)
  W.run(db,"UPDATE file_operations SET state='complete',source_path='',target_path='',error=NULL,completed_at=UTC_TIMESTAMP(6) WHERE id=%s",(op['id'],))
 elif choice in ('restore','locate'):
  side=request.get('side','source')
  if side not in ('source','target'):raise RuntimeError('Choose the first or second copy to restore.')
  dest=W.safe(op[side+'_path'])
  if dest.exists():raise RuntimeError('That copy already exists. Choose which copy to keep or retry cleanup.')
  if choice=='locate':
   relative=request.get('path','').strip()
   if not relative:raise RuntimeError('Enter the copy location relative to wallpaper storage.')
   source=W.safe(relative)
  else:
   # The existing per-operation backup store is outside the library.
   folder=W.backup_folder(op['id'],0)
   candidates=[folder/dest.name,folder/(str(op[side+'_file_id'])+'-'+dest.name)]
   source=next((p for p in candidates if p.is_file()),candidates[0])
   if not source.is_file():raise RuntimeError('No backup of this copy exists for this issue. Locate an existing copy or restore it from your NAS backup, then retry.')
  with W.protected(source) as stream:
   reviewed_hash=W.hashstream(stream)
   stream.seek(0);dest.parent.mkdir(parents=True,exist_ok=True)
   # Exclusive creation prevents replacing any file created since the check.
   try:
    with dest.open('xb') as out:
     shutil.copyfileobj(stream,out);out.flush();os.fsync(out.fileno())
   except Exception:
    # Retain even a partial file for explicit review; never overwrite or delete it.
    raise
  if W.filehash(dest)!=reviewed_hash:raise RuntimeError('Restored copy changed during transfer. Review it before continuing.')
  return 'Selected copy restored and verified against its source. Now choose which copy to keep.'
 else:raise RuntimeError('Choose a supported resolution.')
 result=W.q(db,'SELECT state,error FROM file_operations WHERE id=%s',(op['id'],))[0]
 if result['state'] in ('complete','cancelled'):return 'Resolved. Your reviewed choice was applied and the redundant physical copy was removed.'
 message=result['error'] or 'Cleanup is blocked by a downloader connection or collection move. Check the integration connection, then retry.'
 W.run(db,"UPDATE file_operations SET state='error',error=%s WHERE id=%s",(message,op['id']))
 raise RuntimeError(message)

def drain(W,db):
 # Called only while the normal worker owns wallpapers_database_worker.
 manual=W.q(db,"SELECT state_key,value FROM gallery_state WHERE state_key LIKE 'manual_duplicate_request:%%' AND JSON_UNQUOTE(JSON_EXTRACT(value,'$.status')) IN ('pending','working') ORDER BY state_key LIMIT 10")
 for row in manual:
  request=W.decode(row['value']);request['status']='working';W.state(db,row['state_key'],request)
  try:resolve_reviewed_duplicate(W,db,request,row['state_key'])
  except Exception as error:
   db.rollback();request.update(status='failed',message=str(error));W.state(db,row['state_key'],request);print('Manual duplicate review: '+traceback.format_exc(),flush=True)
 requests=W.q(db,"SELECT state_key,value FROM gallery_state WHERE state_key LIKE 'recovery_action:%%' AND JSON_UNQUOTE(JSON_EXTRACT(value,'$.status')) IN ('pending','working') ORDER BY state_key LIMIT 10")
 for row in requests:
  request=W.decode(row['value'])
  if request.get('status') not in ('pending','working'):continue
  request['status']='working';W.state(db,row['state_key'],request)
  try:
   W.require_root()
   found=W.q(db,'SELECT * FROM file_operations WHERE id=%s',(request['id'],))
   if not found:raise RuntimeError('This cleanup record no longer exists.')
   op=found[0]
   if op['state'] in ('complete','cancelled'):message='Already resolved. Refreshing removed the old error.'
   else:
    if not same_plan(op,request['plan']):raise RuntimeError('The worker changed this cleanup since you opened it. Refresh the issue before choosing again.')
    moves=W.q(db,'SELECT move_id FROM fm_collection_move_files WHERE operation_id=%s LIMIT 1',(op['id'],))
    if moves and request['choice']=='retry':
     W.run(db,"UPDATE fm_collection_moves SET state='pending',error=NULL WHERE id=%s AND state='failed'",(moves[0]['move_id'],))
     import CollectionMoves
     CollectionMoves.process(W,db)
     current=W.q(db,'SELECT state,error FROM file_operations WHERE id=%s',(op['id'],))[0]
     if current['state'] not in ('complete','cancelled'):raise RuntimeError(current['error'] or 'Collection move resumed. Its next batch will continue automatically.')
     message='Resolved. The collection move completed this file.'
    else:message=apply(W,db,op,request)
   request.update(status='done',message=message)
  except Exception as error:
   db.rollback();request.update(status='failed',message=friendly(error));print(traceback.format_exc(),flush=True)
  request.pop('plan',None);request.pop('path',None)
  W.state(db,row['state_key'],request)

def friendly(error):
 if isinstance(error,PermissionError):return 'Storage denied the worker access to this image. Close any app locking it and check NAS access for the Gallery worker account, then retry this cleanup.'
 if isinstance(error,FileNotFoundError):return 'This copy is missing. Restore its backup or locate an existing copy below, then retry.'
 message=str(error)
 if 'Downloader MD5 differs' in message or 'Downloader SHA256 differs' in message:return 'The downloader record does not match the actual image. Review the failed matching job to correct or merge its verified downloader record, then retry keeping one copy.'
 return message
