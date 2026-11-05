"""Administrator recovery decisions, executed under the existing worker lock/account."""
import json,traceback,shutil,os
PREFIX='recovery_action:'

def same_plan(a,b):
 return all(a.get(k)==b.get(k) for k in ('source_file_id','target_file_id','source_path','target_path'))

def verify_originals(W,db,op):
 files=[]
 for side in ('source','target'):
  rows=W.q(db,'SELECT * FROM files WHERE id=%s',(op[side+'_file_id'],))
  if not rows:raise RuntimeError('A file record is missing. Retry discovery before choosing a copy.')
  f=rows[0]
  if f['canonical_file_id'] or f['relative_path']!=op[side+'_path']:
   raise RuntimeError('These copies have already been moved or merged. Use the saved-copy resolution or retry cleanup to reconcile their current locations.')
  path=W.safe(op[side+'_path'])
  with W.protected(path) as stream:
   if W.hashstream(stream)!=bytes(op[side+'_sha256']):raise RuntimeError('The '+side+' copy changed since this cleanup was planned. No decision was applied.')
  files.append(f)
 return files

def apply(W,db,op,request):
 choice=request['choice']
 if choice=='retry':
  W.run(db,"UPDATE file_operations SET error=NULL,state=IF(state='error','planned',state) WHERE id=%s",(op['id'],))
  W.recover(db,operation_id=op['id'])
 elif choice in ('first','second','both','cancel_move'):
  is_move=op['operation_kind']=='organize'
  if choice=='cancel_move':
   if not is_move:raise RuntimeError('Keeping the original location applies to a move only.')
   path=W.safe(op['source_path'])
   with W.protected(path) as stream:
    if W.hashstream(stream)!=bytes(op['source_sha256']):raise RuntimeError('The original file changed; the move was not cancelled.')
   current=W.q(db,'SELECT * FROM files WHERE id=%s',(op['source_file_id'],))[0]
   if current['relative_path']!=op['source_path'] or current['canonical_file_id']:raise RuntimeError('The move already updated its links. Finish the move instead.')
   if W.safe(op['target_path']).exists():raise RuntimeError('A destination copy already exists. Finish the move to verify and reconcile both locations.')
   W.run(db,"UPDATE file_operations SET state='cancelled',completed_at=UTC_TIMESTAMP(6) WHERE id=%s",(op['id'],))
   return 'Resolved. The verified original stays in its original location.'
  if is_move:raise RuntimeError('This is a file move. Choose Finish move or Keep original location.')
  verify_originals(W,db,op)
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
  if choice=='first':
   # Values come from the verified snapshot, avoiding MySQL assignment-order swaps.
   W.run(db,"UPDATE file_operations SET source_file_id=%s,target_file_id=%s,source_path=%s,target_path=%s,source_sha256=%s,target_sha256=%s,state='planned',error=NULL WHERE id=%s",(op['target_file_id'],op['source_file_id'],op['target_path'],op['source_path'],op['target_sha256'],op['source_sha256'],op['id']))
  else:W.run(db,"UPDATE file_operations SET state='planned',error=NULL WHERE id=%s",(op['id'],))
  W.recover(db,operation_id=op['id'])
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
   source=next((p for p in candidates if p.is_file() and W.filehash(p)==bytes(op[side+'_sha256'])),candidates[0])
   if not source.is_file():raise RuntimeError('No backup of this copy exists for this issue. Locate an existing copy or restore it from your NAS backup, then retry.')
  with W.protected(source) as stream:
   if W.hashstream(stream)!=bytes(op[side+'_sha256']):raise RuntimeError('This copy does not match the missing image. It was not restored.')
   stream.seek(0);dest.parent.mkdir(parents=True,exist_ok=True)
   # Exclusive creation prevents replacing any file created since the check.
   try:
    with dest.open('xb') as out:
     shutil.copyfileobj(stream,out);out.flush();os.fsync(out.fileno())
   except Exception:
    # Retain even a partial file for explicit review; never overwrite or delete it.
    raise
  if W.filehash(dest)!=bytes(op[side+'_sha256']):raise RuntimeError('Restored copy failed verification. Review it before continuing.')
  return 'Copy restored and verified. Now choose which copy to keep, or retry cleanup.'
 else:raise RuntimeError('Choose a supported resolution.')
 result=W.q(db,'SELECT state,error FROM file_operations WHERE id=%s',(op['id'],))[0]
 if result['state'] in ('complete','cancelled'):return 'Resolved. Cleanup finished, linked records were updated, and the removed copy was backed up.'
 message=result['error'] or 'Cleanup is blocked by a downloader connection or collection move. Check the integration connection, then retry.'
 W.run(db,"UPDATE file_operations SET state='error',error=%s WHERE id=%s",(message,op['id']))
 raise RuntimeError(message)

def drain(W,db):
 # Called only while the normal worker owns wallpapers_database_worker.
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
 if 'Downloader MD5 differs' in message or 'Downloader SHA256 differs' in message:return 'The downloader record does not match the actual image. Keep both copies, restore the original copy from backup, or repair the downloader record before merging.'
 return message
