"""Offline migration to entry-owned collection and single file pointers.

Run with writers stopped. A backup is required for production. No filesystem I/O.
"""
import argparse,configparser,json,pathlib
import pymysql
import FileManager as F
import EntryFiles as E

def migrate(db):
 schema=F.rows(db,'SELECT DATABASE() n')[0]['n']
 def columns(table):return {r['COLUMN_NAME'] for r in F.rows(db,'SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=%s AND TABLE_NAME=%s',(schema,table))}
 done=F.rows(db,'SELECT version FROM schema_migrations WHERE version=11')
 if done:
  import CurrentLocations
  CurrentLocations.migrate(db)
  return {'version':12,'already_applied':True}
 print('Preparing entry/file schema',flush=True)
 before={t:F.rows(db,'SELECT COUNT(*) n FROM '+t)[0]['n'] for t in ['inventory_entries','files','inventory_entry_relations']}
 for t in F.rows(db,"SELECT TRIGGER_NAME FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA=%s AND (EVENT_OBJECT_TABLE='inventory_entry_files' OR TRIGGER_NAME IN ('fm_entry_update','fm_file_update','file_path_revision','inventory_entry_revision'))",(schema,)):
  F.execute(db,'DROP TRIGGER `'+t['TRIGGER_NAME']+'`')
 for name in ['collection_id','file_id']:
  if name not in columns('inventory_entries'):F.execute(db,'ALTER TABLE inventory_entries ADD COLUMN '+name+' BIGINT UNSIGNED NULL')
 F.execute(db,'UPDATE inventory_entries e JOIN inventory_documents d ON d.id=e.inventory_document_id SET e.collection_id=d.collection_id WHERE e.collection_id IS NULL')
 for name,cols in [('ix_entry_collection_page','collection_id,is_hidden,deleted_at,id'),('ix_entry_collection_category','collection_id,category,is_hidden,deleted_at,id'),('ix_entry_file','file_id,id')]:
  if not F.rows(db,'SELECT 1 FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=%s AND TABLE_NAME=%s AND INDEX_NAME=%s',(schema,'inventory_entries',name)):F.execute(db,'ALTER TABLE inventory_entries ADD INDEX '+name+'('+cols+')')
 relation_type=F.rows(db,"SELECT COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=%s AND TABLE_NAME='inventory_entry_relations' AND COLUMN_NAME='relation_kind'",(schema,))[0]['COLUMN_TYPE']
 if 'contains' in relation_type:
  F.execute(db,"ALTER TABLE inventory_entry_relations MODIFY relation_kind ENUM('contains','parent_of','duplicate_of','fallback_for','discovered_from') NOT NULL")
  F.execute(db,"UPDATE inventory_entry_relations SET relation_kind='parent_of' WHERE relation_kind='contains'")
  F.execute(db,"ALTER TABLE inventory_entry_relations MODIFY relation_kind ENUM('parent_of','duplicate_of','fallback_for','discovered_from') NOT NULL")
 F.execute(db,"INSERT IGNORE INTO inventory_entry_relations(entry_id,related_entry_id,relation_kind) SELECT id,canonical_entry_id,'duplicate_of' FROM inventory_entries WHERE canonical_entry_id IS NOT NULL AND id<>canonical_entry_id")
 for name in ['position','details']:
  if name in columns('inventory_entry_relations'):F.execute(db,'ALTER TABLE inventory_entry_relations DROP COLUMN '+name)
 print('Mapping file associations to entries',flush=True)
 F.execute(db,'DROP PROCEDURE IF EXISTS fm_bind_entry_file')
 F.execute(db,pathlib.Path(__file__).with_name('entry-files-routines.sql').read_text())
 old=bool(F.rows(db,"SELECT 1 FROM information_schema.TABLES WHERE TABLE_SCHEMA=%s AND TABLE_NAME='inventory_entry_files'",(schema,)))
 if old:
  # Fail before losing any association if an individual image has several files.
  conflicts=F.rows(db,"SELECT e.id,COUNT(DISTINCT COALESCE(f.canonical_file_id,f.id)) n FROM inventory_entries e JOIN inventory_entry_files l ON l.entry_id=e.id JOIN files f ON f.id=l.file_id WHERE e.record_type NOT IN ('post','album','post_or_album') GROUP BY e.id HAVING n>1")
  if conflicts:raise RuntimeError('Conflicting image records: '+json.dumps(conflicts))
  F.execute(db,"UPDATE inventory_entries e JOIN inventory_entry_files l ON l.entry_id=e.id JOIN files f ON f.id=l.file_id SET e.file_id=COALESCE(f.canonical_file_id,f.id) WHERE e.record_type NOT IN ('post','album','post_or_album')")
  links=F.rows(db,'SELECT l.*,e.record_type,e.collection_id FROM inventory_entry_files l JOIN inventory_entries e ON e.id=l.entry_id ORDER BY l.entry_id,l.id')
  missing=0
  db.begin()
  try:
   for l in links:
    fid=l['file_id']
    if fid is not None and l['record_type'] not in E.PARENTS:continue
    if fid is None:
     e=F.rows(db,'SELECT * FROM inventory_entries WHERE id=%s',(l['entry_id'],))[0]
     owner=l['recorded_collection'] or F.rows(db,'SELECT collection_key FROM collections WHERE id=%s',(e['collection_id'],))[0]['collection_key']
     E.bind(db,e['id'],E.resolve(db,owner,l['recorded_path']));missing+=1
     continue
    f=F.rows(db,'SELECT COALESCE(canonical_file_id,id) id FROM files WHERE id=%s',(fid,))[0]
    if l['record_type'] in E.PARENTS:E.bind(db,l['entry_id'],f['id'])
   db.commit()
  except Exception:db.rollback();raise
  missing_links=F.rows(db,"SELECT l.entry_id,l.file_id FROM inventory_entry_files l JOIN files f ON f.id=l.file_id JOIN inventory_entries e ON e.id=l.entry_id WHERE NOT(e.file_id <=> COALESCE(f.canonical_file_id,f.id)) AND NOT EXISTS(SELECT 1 FROM inventory_entry_relations r JOIN inventory_entries child ON child.id=r.related_entry_id WHERE r.entry_id=e.id AND r.relation_kind='parent_of' AND child.file_id=COALESCE(f.canonical_file_id,f.id)) LIMIT 10")
  if missing_links:raise RuntimeError('Unmigrated links: '+json.dumps(missing_links))
  F.execute(db,'DROP TABLE inventory_entry_files')
 else:missing=0
 F.execute(db,"UPDATE inventory_entries SET file_id=NULL WHERE record_type IN ('post','album','post_or_album')")
 if F.rows(db,"SELECT IS_NULLABLE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='inventory_entries' AND COLUMN_NAME='collection_id'")[0]['IS_NULLABLE']=='YES':F.execute(db,'ALTER TABLE inventory_entries MODIFY collection_id BIGINT UNSIGNED NOT NULL')
 for name,col,target,action in [('fk_entry_collection','collection_id','collections','RESTRICT'),('fk_entry_file','file_id','files','RESTRICT')]:
  if not F.rows(db,'SELECT 1 FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=%s AND TABLE_NAME=%s AND CONSTRAINT_NAME=%s',(schema,'inventory_entries',name)):F.execute(db,f'ALTER TABLE inventory_entries ADD CONSTRAINT {name} FOREIGN KEY({col}) REFERENCES {target}(id) ON DELETE {action}')
 if not F.rows(db,"SELECT 1 FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=%s AND CONSTRAINT_NAME='ck_parent_no_file'",(schema,)):F.execute(db,"ALTER TABLE inventory_entries ADD CONSTRAINT ck_parent_no_file CHECK(record_type NOT IN ('post','album','post_or_album') OR file_id IS NULL)")
 if 'collection_id' in columns('files'):
  if F.rows(db,"SELECT 1 FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=DATABASE() AND TABLE_NAME='files' AND CONSTRAINT_NAME='fk_files_collection'"):F.execute(db,'ALTER TABLE files DROP FOREIGN KEY fk_files_collection')
  if F.rows(db,"SELECT 1 FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='files' AND INDEX_NAME='ix_files_collection'"):F.execute(db,'ALTER TABLE files DROP INDEX ix_files_collection,ALGORITHM=INPLACE,LOCK=NONE')
  F.execute(db,'ALTER TABLE files DROP COLUMN collection_id,ALGORITHM=INSTANT')
 print('Creating computed file projections',flush=True)
 # These views store no data. Physical folder ownership is derived from the path;
 # entry membership is always inventory_entries.collection_id.
 F.execute(db,"CREATE OR REPLACE ALGORITHM=MERGE SQL SECURITY INVOKER VIEW fm_file_catalog AS SELECT f.*,c.id collection_id FROM files f LEFT JOIN collections c ON f.relative_path IS NOT NULL AND f.relative_path<>'' AND EXISTS(SELECT 1 FROM fm_collection_discovery marked WHERE marked.collection_id=c.id AND marked.is_marked=TRUE) AND (c.collection_key='.' OR LEFT(f.relative_path,CHAR_LENGTH(c.collection_key)+1) COLLATE utf8mb4_0900_as_ci=CONCAT(c.collection_key,'/')) AND NOT EXISTS(SELECT 1 FROM collections nested WHERE EXISTS(SELECT 1 FROM fm_collection_discovery marked_child WHERE marked_child.collection_id=nested.id AND marked_child.is_marked=TRUE) AND CHAR_LENGTH(nested.collection_key)>CHAR_LENGTH(c.collection_key) AND LEFT(f.relative_path,CHAR_LENGTH(nested.collection_key)+1) COLLATE utf8mb4_0900_as_ci=CONCAT(nested.collection_key,'/'))")
 F.execute(db,"""CREATE OR REPLACE SQL SECURITY INVOKER VIEW fm_entry_file_projection AS
 SELECT e.id id,e.id entry_id,e.file_id,IF(e.status='duplicate','duplicate','primary') reference_kind,0 position FROM inventory_entries e WHERE e.file_id IS NOT NULL
 UNION ALL SELECT child.id,r.entry_id,child.file_id,'related',0 FROM inventory_entry_relations r JOIN inventory_entries child ON child.id=r.related_entry_id WHERE r.relation_kind='parent_of' AND child.file_id IS NOT NULL AND child.deleted_at IS NULL""")
 # New entries inherit collection from import provenance only at insertion.
 F.execute(db,'DROP TRIGGER IF EXISTS entry_collection_insert')
 F.execute(db,"CREATE TRIGGER entry_collection_insert BEFORE INSERT ON inventory_entries FOR EACH ROW BEGIN IF NEW.collection_id IS NULL OR NEW.collection_id=0 THEN SET NEW.collection_id=(SELECT collection_id FROM inventory_documents WHERE id=NEW.inventory_document_id); END IF; END")
 revision="""CREATE TRIGGER inventory_entry_revision BEFORE UPDATE ON inventory_entries FOR EACH ROW BEGIN
 IF NOT(NEW.raw_record <=> OLD.raw_record) OR NOT(NEW.site <=> OLD.site) OR NOT(NEW.source_id <=> OLD.source_id) OR NOT(NEW.record_type <=> OLD.record_type) OR NOT(NEW.status <=> OLD.status) OR NOT(NEW.category <=> OLD.category) OR NOT(NEW.description <=> OLD.description) OR NOT(NEW.source_url <=> OLD.source_url) OR NOT(NEW.image_url <=> OLD.image_url) OR NOT(NEW.resolution <=> OLD.resolution) OR NOT(NEW.is_hidden <=> OLD.is_hidden) OR NOT(NEW.deleted_at <=> OLD.deleted_at) OR NOT(NEW.canonical_entry_id <=> OLD.canonical_entry_id) OR NOT(NEW.metadata_score <=> OLD.metadata_score) OR NOT(NEW.collection_id <=> OLD.collection_id) OR NOT(NEW.file_id <=> OLD.file_id) OR NEW.revision>OLD.revision THEN SET NEW.revision=OLD.revision+1; ELSE SET NEW.revision=OLD.revision; END IF; END"""
 F.execute(db,revision)
 F.execute(db,"""CREATE TRIGGER fm_entry_update AFTER UPDATE ON inventory_entries FOR EACH ROW BEGIN
 IF NEW.revision<>OLD.revision OR NOT(NEW.record_sha256 <=> OLD.record_sha256) THEN
 INSERT INTO fm_entry_dirty(entry_id) VALUES(NEW.id) ON DUPLICATE KEY UPDATE generation=generation+1;
 IF NEW.file_id IS NOT NULL THEN INSERT INTO fm_file_dirty(file_id) VALUES(NEW.file_id) ON DUPLICATE KEY UPDATE generation=generation+1; END IF;
 IF OLD.file_id IS NOT NULL AND NOT(NEW.file_id <=> OLD.file_id) THEN INSERT INTO fm_file_dirty(file_id) VALUES(OLD.file_id) ON DUPLICATE KEY UPDATE generation=generation+1; END IF;
 END IF; END""")
 F.execute(db,"""CREATE TRIGGER fm_file_update AFTER UPDATE ON files FOR EACH ROW BEGIN
 IF NOT(NEW.relative_path <=> OLD.relative_path) OR NOT(NEW.byte_size <=> OLD.byte_size) OR NOT(NEW.modified_utc_ticks <=> OLD.modified_utc_ticks) OR NOT(NEW.canonical_file_id <=> OLD.canonical_file_id) OR NOT(NEW.is_present <=> OLD.is_present) OR NOT(NEW.width <=> OLD.width) OR NOT(NEW.height <=> OLD.height) THEN
 INSERT INTO fm_file_dirty(file_id) VALUES(NEW.id) ON DUPLICATE KEY UPDATE generation=generation+1;
 INSERT INTO fm_entry_dirty(entry_id) SELECT id FROM inventory_entries WHERE file_id=NEW.id ON DUPLICATE KEY UPDATE generation=generation+1;
 END IF; END""")
 F.execute(db,"""CREATE TRIGGER file_path_revision AFTER UPDATE ON files FOR EACH ROW BEGIN IF NOT(NEW.relative_path <=> OLD.relative_path) OR NOT(NEW.canonical_file_id <=> OLD.canonical_file_id) OR NEW.is_present<>OLD.is_present THEN UPDATE inventory_entries SET revision=revision+1 WHERE file_id=NEW.id; END IF; END""")
 for event,ref in [('INSERT','NEW'),('DELETE','OLD')]:
  name='entry_relation_'+event.lower();F.execute(db,'DROP TRIGGER IF EXISTS '+name)
  F.execute(db,f'CREATE TRIGGER {name} AFTER {event} ON inventory_entry_relations FOR EACH ROW INSERT INTO fm_entry_dirty(entry_id) VALUES({ref}.entry_id) ON DUPLICATE KEY UPDATE generation=generation+1')
 F.execute(db,"CREATE OR REPLACE SQL SECURITY INVOKER VIEW inventory_file_details AS SELECT e.id entry_id,e.collection_id,c.collection_key,e.site,e.source_id,e.record_type,e.status,e.category,e.description,e.file_id,f.relative_path file_path,f.is_present,f.byte_size,f.width,f.height,f.image_format FROM inventory_entries e JOIN collections c ON c.id=e.collection_id LEFT JOIN files f ON f.id=e.file_id")
 print('Removing previous-location history',flush=True)
 import CurrentLocations
 CurrentLocations.migrate(db)
 E.install_triggers(db)
 F.execute(db,'INSERT IGNORE INTO fm_entry_dirty(entry_id) SELECT id FROM inventory_entries')
 # New child entries must be usable immediately when the application restarts.
 for e in F.rows(db,'SELECT e.* FROM inventory_entries e LEFT JOIN fm_record_index i ON i.entry_id=e.id WHERE i.entry_id IS NULL'):
  db.begin()
  try:F.index_entry(db,e);db.commit()
  except Exception:db.rollback();raise
 F.execute(db,"INSERT INTO schema_migrations(version,description) VALUES(11,'Entry-owned collections and single file pointers; directed parent and duplicate relations')")
 after={t:F.rows(db,'SELECT COUNT(*) n FROM '+t)[0]['n'] for t in before}
 return {'version':11,'before':before,'after':after,'unresolved_references_preserved':missing}

def main():
 p=argparse.ArgumentParser();p.add_argument('--database',required=True);p.add_argument('--defaults-file',default='/etc/mysql/debian.cnf');p.add_argument('--backup');a=p.parse_args()
 if a.database=='wallpapers' and (not a.backup or not pathlib.Path(a.backup).is_file()):p.error('Production backup required')
 if a.database!='wallpapers' and not a.database.startswith('wallpapers_fm_'):p.error('Unexpected database')
 cfg=configparser.ConfigParser(interpolation=None);cfg.read(a.defaults_file);v=cfg['client']
 db=pymysql.connect(host=v.get('host','localhost'),user=v['user'],password=v['password'],database=a.database,autocommit=True,charset='utf8mb4',cursorclass=pymysql.cursors.DictCursor)
 print(json.dumps(migrate(db)),flush=True)
if __name__=='__main__':main()
