"""Configure a schema-pinned adapter after its owning app migration completes.

No credentials are printed. --enable and --grant-runtime are explicit DBA actions.
--bind-id plus --bind-file registers an existing external record with the gallery.
"""
import argparse,configparser,hashlib,pathlib
import FileManager as F
from FileManagerWorker import external_path

def main():
 p=argparse.ArgumentParser();p.add_argument('--defaults-file',required=True);p.add_argument('--gallery-database',default='wallpapers');p.add_argument('--integration',choices=['booru','wallpaper-download'],required=True);p.add_argument('--database',required=True);p.add_argument('--kind',choices=['booru-downloads','wallpaper-files'],required=True);p.add_argument('--external-root',required=True);p.add_argument('--root-relative',default='.');p.add_argument('--enable',action='store_true');p.add_argument('--grant-runtime',action='store_true');p.add_argument('--bind-id',type=int);p.add_argument('--bind-file');a=p.parse_args()
 if bool(a.bind_id is not None)!=bool(a.bind_file):p.error('--bind-id and --bind-file must be used together')
 if a.gallery_database!='wallpapers' and not a.gallery_database.startswith('wallpapers_fm_'):p.error('Unexpected gallery database')
 import pymysql
 c=configparser.ConfigParser(interpolation=None);c.read(a.defaults_file);v=c['client']
 db=pymysql.connect(host=v.get('host','localhost'),user=v['user'],password=v['password'],database=a.gallery_database,charset='utf8mb4',autocommit=True,cursorclass=pymysql.cursors.DictCursor)
 fingerprint=F.adapter_fingerprint(db,a.database,a.kind)
 F.execute(db,'UPDATE fm_adapter_config SET database_name=%s,enabled=FALSE,status=%s,schema_sha256=%s,external_root=%s,root_relative=%s WHERE integration_key=%s',(a.database,'Applying adapter configuration',fingerprint,a.external_root,a.root_relative,a.integration))
 if a.grant_runtime:
  tables=['downloads'] if a.kind=='booru-downloads' else ['files','posts']
  for table in tables:
   grants='SELECT,UPDATE,INSERT,DELETE' if table=='downloads' else 'SELECT,UPDATE'
   F.execute(db,f"GRANT {grants} ON `{a.database}`.`{table}` TO 'wallpapers_gallery'@'%%'")
  if a.kind=='wallpaper-files':
   for table in ['providers','post_tags','tag_terms']:F.execute(db,f"GRANT SELECT ON `{a.database}`.`{table}` TO 'wallpapers_gallery'@'%%'")
 if a.bind_file:
  relative=a.bind_file.replace('\\','/').strip('/')
  if any(part in ('.','..','') for part in relative.split('/')):p.error('Use a root-relative file path without dot segments')
  current=F.rows(db,'SELECT id,byte_size,modified_utc_ticks,is_present,canonical_file_id FROM fm_file_catalog WHERE path_key=%s',(hashlib.sha256(relative.casefold().encode()).digest(),))
  if not current:raise RuntimeError('The file must be discovered by the gallery first')
  file=current[0];remote=external_path({'external_root':a.external_root,'root_relative':a.root_relative},relative)
  if a.kind=='booru-downloads':
   found=F.rows(db,f'SELECT post_id,path,path_hash FROM `{a.database}`.downloads WHERE post_id=%s AND path_hash=UNHEX(SHA2(%s,256))',(a.bind_id,remote))
   if not found:raise RuntimeError('The external download does not match the expected path')
   external=f"{a.bind_id}:{bytes(found[0]['path_hash']).hex()}";remote=found[0]['path']
  else:
   found=F.rows(db,f'SELECT current_path FROM `{a.database}`.files WHERE id=%s',(a.bind_id,))
   if not found or found[0]['current_path']!=remote:raise RuntimeError('The external file does not match the expected path')
   external=str(a.bind_id)
  integration=F.rows(db,'SELECT id FROM fm_integrations WHERE integration_key=%s',(a.integration,))[0]['id']
  F.execute(db,'INSERT INTO fm_external_bindings VALUES(%s,%s,%s,%s,%s) ON DUPLICATE KEY UPDATE file_id=VALUES(file_id),last_known_path=VALUES(last_known_path),adapter_kind=VALUES(adapter_kind)',(integration,external,file['id'],remote,a.kind))
  F.execute(db,'INSERT INTO fm_file_dirty(file_id) VALUES(%s) ON DUPLICATE KEY UPDATE generation=generation+1',(file['id'],))
  if file['canonical_file_id']:
   key=hashlib.sha256(f'reconcile|{integration}|{external}|{file["canonical_file_id"]}'.encode()).digest()
   F.execute(db,"INSERT IGNORE INTO fm_work_items(task_kind,file_id,expected_size,expected_ticks,deduplication_key,priority) VALUES('reconcile_file',%s,%s,%s,%s,0)",(file['id'],file['byte_size'],file['modified_utc_ticks'],key))
  else:F.execute(db,"UPDATE fm_work_items SET state='pending',failure_count=0,available_at=UTC_TIMESTAMP(6) WHERE file_id=%s AND task_kind='match_file' AND state IN ('completed','failed','cancelled')",(file['id'],))
 F.execute(db,'UPDATE fm_adapter_config SET enabled=%s,status=%s WHERE integration_key=%s',(a.enable,'Ready; registered files only' if a.enable else 'Configured; writes disabled',a.integration))
 print('Configured',a.integration,'enabled='+str(a.enable),'schema='+fingerprint.hex())
 db.close()
if __name__=='__main__':main()

