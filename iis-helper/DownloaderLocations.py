"""Current-location adapters for the migrated downloader schemas."""
import hashlib,json,posixpath,re,mimetypes
KINDS=('booru-locations','wallpaper-current')

def relative(config,path):
 path=str(path).replace('\\','/')
 if path.startswith('/') or ':' in path or '..' in path.split('/'):raise RuntimeError('Invalid root-relative file path')
 path=posixpath.normpath(path)
 prefix=(config.get('root_relative') or '.').strip('/')
 if prefix!='.':
  if not path.casefold().startswith(prefix.casefold()+'/'):raise RuntimeError('Destination is outside downloader storage')
  path=path[len(prefix)+1:]
 if path in ('','.'):raise RuntimeError('A file path is required')
 return path

def key(path):return hashlib.sha256(path.encode('utf-8')).digest()

def fingerprint(db,schema,kind):
 import FileManager as F
 if kind not in KINDS or not re.fullmatch(r'[a-zA-Z0-9_-]+',schema):raise RuntimeError('Invalid location adapter')
 required={'files':{'id','md5','sha256','size_bytes'},'storage_roots':{'id','configured_path','enabled'},'media_files':{'media_id','file_id'}}
 if kind=='booru-locations':required['file_locations']={'id','file_id','storage_root_id','relative_path','path_hash','status','downloaded_at','last_checked_at'}
 else:required['files']|={'storage_root_id','current_path','path_hash','status','downloaded_at','last_checked_at'}
 tables=list(required)
 meta=F.rows(db,'SELECT TABLE_NAME,COLUMN_NAME,COLUMN_TYPE,EXTRA,IS_NULLABLE,COLLATION_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=%s AND TABLE_NAME IN ('+','.join(['%s']*len(tables))+') ORDER BY TABLE_NAME,ORDINAL_POSITION',[schema,*tables])
 engines=F.rows(db,'SELECT TABLE_NAME,ENGINE FROM information_schema.TABLES WHERE TABLE_SCHEMA=%s AND TABLE_NAME IN ('+','.join(['%s']*len(tables))+') ORDER BY TABLE_NAME',[schema,*tables])
 if len(engines)!=len(tables) or any(r['ENGINE']!='InnoDB' for r in engines):raise RuntimeError('Downloader transactional tables are unavailable')
 for table,columns in required.items():
  if not columns<={r['COLUMN_NAME'] for r in meta if r['TABLE_NAME']==table}:raise RuntimeError('Downloader location schema is incompatible')
 return hashlib.sha256(json.dumps(meta,sort_keys=True,separators=(',',':')).encode()).digest()

def configs(db):
 import FileManager as F
 return F.rows(db,"SELECT c.*,i.id integration_id FROM fm_adapter_config c JOIN fm_integrations i ON i.integration_key=c.integration_key WHERE c.adapter_kind IN ('booru-locations','wallpaper-current') AND c.enabled=TRUE")

def lookup(db,c,path=None,md5=None):
 import FileManager as F
 schema=c['database_name'];root=c['storage_root_id']
 if c['adapter_kind']=='booru-locations':
  sql=f'SELECT l.id external_id,l.file_id,l.relative_path path,f.md5,f.sha256,f.size_bytes FROM `{schema}`.file_locations l JOIN `{schema}`.files f ON f.id=l.file_id WHERE l.storage_root_id=%s'
  return F.rows(db,sql+(' AND l.path_hash=%s' if path is not None else ' AND f.md5=%s'),(root,key(path) if path is not None else md5))
 sql=f'SELECT id external_id,current_path path,md5,sha256,size_bytes FROM `{schema}`.files WHERE storage_root_id=%s'
 return F.rows(db,sql+(' AND path_hash=%s' if path is not None else ' AND md5=%s'),(root,key(path) if path is not None else md5))

def bind(db,fid,path,proof=None,root=None):
 import FileManager as F
 checked=[]
 for c in configs(db):
  prefix={'booru':'booru/','wallpaper-download':'wallpaper-download/'}[c['integration_key']]
  if not path.casefold().startswith(prefix):continue
  rel=relative(c,path);matches=lookup(db,c,path=rel)
  if not matches and proof and root is not None:
   # Repair stale paths by content only when the other recorded copy is absent.
   matches=[r for r in lookup(db,c,md5=proof[0]) if r['path'] and not (root/r['path']).is_file()]
  for r in matches:
   F.execute(db,'INSERT INTO fm_external_bindings(integration_id,external_id,file_id,last_known_path,adapter_kind) VALUES(%s,%s,%s,%s,%s) ON DUPLICATE KEY UPDATE file_id=VALUES(file_id),last_known_path=VALUES(last_known_path),adapter_kind=VALUES(adapter_kind)',(c['integration_id'],str(r['external_id']),fid,r['path'],c['adapter_kind']))
  checked.append(c['integration_key'])
 return checked

def match_discovered(db,config,gallery_path,filesystem_path,expected_sha256):
 """Verify a discovered file and attach matching media inside its downloader DB."""
 import FileManager as F
 rel=relative(config,gallery_path)
 md5=hashlib.md5();sha=hashlib.sha256();size=0
 with filesystem_path.open('rb') as stream:
  for block in iter(lambda:stream.read(1024*1024),b''):
   md5.update(block);sha.update(block);size+=len(block)
 proof=(md5.hexdigest(),sha.hexdigest(),size)
 if proof[1]!=expected_sha256:raise RuntimeError('Discovered file changed since gallery hashing')
 schema=config['database_name'];kind=config['adapter_kind'];root_id=config['storage_root_id']
 found=lookup(db,config,path=rel)
 if found:
  if len(found)!=1:raise RuntimeError('Downloader has conflicting records for this path')
  verify(found[0],proof)
  external_file_id=found[0]['file_id'] if kind=='booru-locations' else found[0]['external_id']
 else:
  same=F.rows(db,f'SELECT id,md5,sha256,size_bytes FROM `{schema}`.files WHERE sha256=%s',(proof[1],))
  if same:
   verify(same[0],proof);external_file_id=same[0]['id']
  else:external_file_id=None
 # A remote MD5 is evidence for the original media bytes. It must also agree
 # with a supplied media size; URLs, filenames and post IDs are not proof.
 candidates=[]
 if kind=='booru-locations':
  candidates=F.rows(db,f"SELECT id FROM `{schema}`.post_media WHERE remote_md5=%s AND (file_size_bytes IS NULL OR file_size_bytes=%s) LIMIT 101",(proof[0],size))
  if len(candidates)>100:raise RuntimeError('Too many downloader posts share this checksum; manual review required')
 if external_file_id is None and not candidates:return 0
 db.begin()
 try:
  if external_file_id is None:
   external_file_id=F.execute(db,f'INSERT INTO `{schema}`.files(md5,sha256,size_bytes,mime_type,verified_at) VALUES(%s,%s,%s,%s,UTC_TIMESTAMP(6))',(proof[0],proof[1],size,mimetypes.guess_type(rel)[0]))[0]
  if kind=='booru-locations':
   if found:
    F.execute(db,f"UPDATE `{schema}`.file_locations SET status='present',last_checked_at=UTC_TIMESTAMP(6) WHERE id=%s",(found[0]['external_id'],))
   else:
    F.execute(db,f"INSERT INTO `{schema}`.file_locations(file_id,storage_root_id,relative_path,path_hash,status,downloaded_at,last_checked_at) VALUES(%s,%s,%s,%s,'present',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))",(external_file_id,root_id,rel,key(rel)))
  elif found:
   F.execute(db,f"UPDATE `{schema}`.files SET status='present',last_checked_at=UTC_TIMESTAMP(6) WHERE id=%s",(external_file_id,))
  elif not found:
   # Wallpaper Desk's file row owns the current path.
   F.execute(db,f"UPDATE `{schema}`.files SET storage_root_id=%s,current_path=%s,path_hash=%s,status='present',last_checked_at=UTC_TIMESTAMP(6) WHERE id=%s AND (current_path='' OR current_path IS NULL)",(root_id,rel,key(rel),external_file_id))
  for media in candidates:
   F.execute(db,f'INSERT IGNORE INTO `{schema}`.media_files(media_id,file_id) VALUES(%s,%s)',(media['id'],external_file_id))
  post_count=F.rows(db,f'SELECT COUNT(DISTINCT pm.post_id) n FROM `{schema}`.media_files mf JOIN `{schema}`.post_media pm ON pm.id=mf.media_id WHERE mf.file_id=%s',(external_file_id,))[0]['n']
  db.commit()
 except Exception:db.rollback();raise
 return post_count

def verify(row,proof):
 if not proof:raise RuntimeError('Downloader source-file proof is unavailable')
 md5,sha,size=proof
 if not row.get('md5') and not row.get('sha256'):raise RuntimeError('Downloader file has no content hash')
 if row.get('md5') and row['md5'].lower()!=md5:raise RuntimeError('Downloader MD5 differs from the actual file; original retained')
 if row.get('sha256') and row['sha256'].lower()!=sha:raise RuntimeError('Downloader SHA256 differs from the actual file; original retained')
 if row.get('size_bytes') is not None and row['size_bytes']!=size:raise RuntimeError('Downloader file size differs; original retained')

def relink(db,schema,old,new):
 import FileManager as F
 if old==new:return
 F.execute(db,f'INSERT IGNORE INTO `{schema}`.media_files(media_id,file_id) SELECT media_id,%s FROM `{schema}`.media_files WHERE file_id=%s',(new,old))
 F.execute(db,f'DELETE FROM `{schema}`.media_files WHERE file_id=%s',(old,))

def sync(db,c,b,new,old_proof,new_proof):
 import FileManager as F
 schema=c['database_name'];root=c['storage_root_id'];eid=int(b['external_id']);md5,sha,size=new_proof
 if c['adapter_kind']=='booru-locations':
  found=F.rows(db,f'SELECT l.*,l.relative_path path,f.md5,f.sha256,f.size_bytes FROM `{schema}`.file_locations l JOIN `{schema}`.files f ON f.id=l.file_id WHERE l.id=%s FOR UPDATE',(eid,))
 else:found=F.rows(db,f'SELECT *,current_path path FROM `{schema}`.files WHERE id=%s FOR UPDATE',(eid,))
 if not found:raise RuntimeError('Downloader location disappeared; original retained')
 old=found[0]
 if old['storage_root_id']!=root:raise RuntimeError('Downloader storage root changed; original retained')
 if old['path']==new:
  verify(old,new_proof);return str(eid)
 if old['path']!=b['last_known_path']:raise RuntimeError('Downloader path changed concurrently; original retained')
 verify(old,old_proof)
 same=old_proof[0]==md5 and old_proof[1]==sha
 if c['adapter_kind']=='booru-locations':
  dest=F.rows(db,f'SELECT l.*,f.md5,f.sha256,f.size_bytes FROM `{schema}`.file_locations l JOIN `{schema}`.files f ON f.id=l.file_id WHERE l.storage_root_id=%s AND l.path_hash=%s FOR UPDATE',(root,key(new)))
  if dest:
   verify(dest[0],new_proof);target=dest[0]['file_id'];relink(db,schema,old['file_id'],target)
   F.execute(db,f'DELETE FROM `{schema}`.file_locations WHERE id=%s',(eid,));return str(dest[0]['id'])
  target=old['file_id']
  if not same:
   identities=F.rows(db,f'SELECT id FROM `{schema}`.files WHERE sha256=%s FOR UPDATE',(sha,))
   target=identities[0]['id'] if identities else F.execute(db,f'INSERT INTO `{schema}`.files(md5,sha256,size_bytes,mime_type,verified_at) VALUES(%s,%s,%s,%s,UTC_TIMESTAMP(6))',(md5,sha,size,mimetypes.guess_type(new)[0]))[0]
   relink(db,schema,old['file_id'],target)
  F.execute(db,f"UPDATE `{schema}`.file_locations SET file_id=%s,relative_path=%s,path_hash=%s,status='present',last_checked_at=UTC_TIMESTAMP(6) WHERE id=%s",(target,new,key(new),eid))
  return str(eid)
 dest=F.rows(db,f'SELECT * FROM `{schema}`.files WHERE storage_root_id=%s AND path_hash=%s FOR UPDATE',(root,key(new)))
 if dest:verify(dest[0],new_proof)
 identities=F.rows(db,f'SELECT * FROM `{schema}`.files WHERE sha256=%s FOR UPDATE',(sha,))
 target=eid if same and not dest else identities[0]['id'] if identities else dest[0]['id'] if dest else None
 retire={eid,*[r['id'] for r in dest]}-{target}
 for old_id in retire:F.execute(db,f"UPDATE `{schema}`.files SET current_path='',path_hash=UNHEX(SHA2(CONCAT('retired-file:',id),256)),status='missing',last_checked_at=UTC_TIMESTAMP(6) WHERE id=%s",(old_id,))
 if target is None:target=F.execute(db,f"INSERT INTO `{schema}`.files(md5,sha256,size_bytes,mime_type,verified_at,storage_root_id,current_path,path_hash,status,downloaded_at) VALUES(%s,%s,%s,%s,UTC_TIMESTAMP(6),%s,%s,%s,'present',%s)",(md5,sha,size,mimetypes.guess_type(new)[0],root,new,key(new),old['downloaded_at']))[0]
 else:F.execute(db,f"UPDATE `{schema}`.files SET storage_root_id=%s,current_path=%s,path_hash=%s,status='present',last_checked_at=UTC_TIMESTAMP(6) WHERE id=%s",(root,new,key(new),target))
 for old_id in retire:relink(db,schema,old_id,target)
 return str(target)
