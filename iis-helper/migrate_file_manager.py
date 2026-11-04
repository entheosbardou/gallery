"""Additive, resumable schema migration. Production requires a verified backup."""
import argparse,configparser,pathlib,sys,json,time
import FileManager as fm

def migrate(db,schema):
 if fm.rows(db,"SELECT version FROM schema_migrations WHERE version=11"):return
 sql=pathlib.Path(__file__).with_name('file-manager-schema.sql').read_text(encoding='utf-8-sig')
 sql='\n'.join(l for l in sql.splitlines() if not l.lstrip().startswith('--')).replace('CREATE TABLE ','CREATE TABLE IF NOT EXISTS ')
 for stmt in sql.split(';'):
  if stmt.strip():fm.execute(db,stmt)
 for stmt in pathlib.Path(__file__).with_name('collection-moves-schema.sql').read_text(encoding='utf-8-sig').split(';'):
  if stmt.strip():fm.execute(db,stmt)
 extra=[
 "CREATE TABLE IF NOT EXISTS fm_record_index(entry_id BIGINT UNSIGNED PRIMARY KEY,record_sha256 BINARY(32) NOT NULL,tags_sha256 BINARY(32) NULL)",
 "CREATE TABLE IF NOT EXISTS fm_entry_dirty(entry_id BIGINT UNSIGNED PRIMARY KEY,generation BIGINT UNSIGNED NOT NULL DEFAULT 1)",
 "CREATE TABLE IF NOT EXISTS fm_file_dirty(file_id BIGINT UNSIGNED PRIMARY KEY,generation BIGINT UNSIGNED NOT NULL DEFAULT 1)",
 "CREATE TABLE IF NOT EXISTS fm_tag_import_issues(entry_id BIGINT UNSIGNED PRIMARY KEY,reason VARCHAR(64) NOT NULL,source_text LONGTEXT NOT NULL)",
 "CREATE TABLE IF NOT EXISTS fm_collection_discovery(collection_id BIGINT UNSIGNED PRIMARY KEY,is_marked BOOLEAN NOT NULL DEFAULT TRUE)",
 "CREATE TABLE IF NOT EXISTS fm_adapter_config(integration_key VARCHAR(128) PRIMARY KEY,database_name VARCHAR(64) NULL,enabled BOOLEAN NOT NULL DEFAULT FALSE,status VARCHAR(128) NOT NULL)",
 "CREATE TABLE IF NOT EXISTS fm_visual_bands(file_id BIGINT UNSIGNED NOT NULL,band TINYINT UNSIGNED NOT NULL,band_value SMALLINT UNSIGNED NOT NULL,PRIMARY KEY(file_id,band),KEY band_lookup(band,band_value,file_id))",
 "CREATE TABLE IF NOT EXISTS fm_deferred_files(file_id BIGINT UNSIGNED PRIMARY KEY,reason VARCHAR(255) NOT NULL,updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP)",
 "CREATE TABLE IF NOT EXISTS fm_external_bindings(integration_id BIGINT UNSIGNED NOT NULL,external_id VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,file_id BIGINT UNSIGNED NOT NULL,last_known_path VARCHAR(2048) NOT NULL,adapter_kind ENUM('booru-downloads','wallpaper-files') NOT NULL,PRIMARY KEY(integration_id,external_id),KEY file_bindings(file_id,integration_id))",
 "CREATE TABLE IF NOT EXISTS fm_match_progress(work_id BIGINT UNSIGNED PRIMARY KEY,last_candidate_id BIGINT UNSIGNED NOT NULL DEFAULT 0)",
 "CREATE TABLE IF NOT EXISTS fm_control(setting_key VARCHAR(128) PRIMARY KEY,setting_value VARCHAR(512) NOT NULL)",
 "CREATE TABLE IF NOT EXISTS fm_external_entries(integration_id BIGINT UNSIGNED NOT NULL,external_post_id BIGINT UNSIGNED NOT NULL,entry_id BIGINT UNSIGNED NOT NULL,source_revision DOUBLE NOT NULL,file_id BIGINT UNSIGNED NOT NULL,PRIMARY KEY(integration_id,external_post_id),KEY gallery_entry(entry_id))",
 ]
 for stmt in extra:fm.execute(db,stmt)
 event_type=fm.rows(db,"SELECT COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=%s AND TABLE_NAME='fm_change_events' AND COLUMN_NAME='event_kind'",(schema,))[0]['COLUMN_TYPE']
 if 'asset_updated' not in event_type:fm.execute(db,"ALTER TABLE fm_change_events MODIFY event_kind ENUM('asset_registered','asset_moved','asset_merged','asset_missing','asset_restored','asset_updated') NOT NULL")
 for table,column,definition in [('files','access_category',"VARCHAR(128) NOT NULL DEFAULT 'not_provided'"),('files','file_name','VARCHAR(512) NULL'),('files','icc_profile_sha256','BINARY(32) NULL'),('files','animation_signature','BINARY(32) NULL'),('files','animation_loop',"VARCHAR(64) NOT NULL DEFAULT '0'"),('files','fm_metadata_ticks','BIGINT NULL'),('inventory_entry_files','reference_path_key','BINARY(32) NULL'),('fm_work_items','failure_count','INT UNSIGNED NOT NULL DEFAULT 0'),('fm_work_items','priority','TINYINT UNSIGNED NOT NULL DEFAULT 10'),('fm_record_index','tags_sha256','BINARY(32) NULL'),('fm_hash_settings','decoder','VARCHAR(128) NULL'),('fm_hash_settings','frames','VARCHAR(128) NULL'),('fm_adapter_config','schema_sha256','BINARY(32) NULL'),('fm_adapter_config','external_root','VARCHAR(2048) NULL'),('fm_adapter_config','root_relative',"VARCHAR(512) NOT NULL DEFAULT '.'")]:
  if not fm.rows(db,'SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=%s AND TABLE_NAME=%s AND COLUMN_NAME=%s',(schema,table,column)):
   fm.execute(db,f'ALTER TABLE `{table}` ADD COLUMN `{column}` {definition}')
 if not fm.rows(db,"SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=%s AND TABLE_NAME='files' AND COLUMN_NAME='color_signature'",(schema,)):fm.execute(db,'ALTER TABLE files ADD COLUMN color_signature BINARY(32) NULL')
 colors=[]
 for name in ['icc_profile_sha256','gamma','srgb','chromaticity']:
  value="JSON_EXTRACT(embedded_metadata,'$."+name+"')";value="IF(JSON_TYPE("+value+")='NULL',NULL,JSON_UNQUOTE("+value+"))"
  if name=='icc_profile_sha256':value='LOWER('+value+')'
  colors.append("COALESCE("+value+",'')")
 fm.execute(db,"UPDATE files SET color_signature=UNHEX(SHA2(CONCAT_WS(CHAR(0),"+','.join(colors)+"),256)) WHERE color_signature IS NULL AND fm_metadata_ticks=modified_utc_ticks")
 for table,name,cols in [('inventory_entries','ix_fm_page','is_hidden,deleted_at,id'),('inventory_entries','ix_fm_doc_page','inventory_document_id,is_hidden,deleted_at,category,id'),('inventory_entry_files','ix_fm_missing_path','file_id,reference_path_key'),('files','ix_fm_file_name','file_name,id'),('fm_work_items','ix_fm_claim_priority','state,priority,available_at,id')]:
  if not fm.rows(db,'SELECT 1 FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=%s AND TABLE_NAME=%s AND INDEX_NAME=%s',(schema,table,name)):
   fm.execute(db,f'ALTER TABLE `{table}` ADD INDEX `{name}`({cols}), ALGORITHM=INPLACE, LOCK=NONE')
 if not fm.rows(db,"SELECT 1 FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=%s AND TABLE_NAME='inventory_entry_tags' AND CONSTRAINT_NAME='fk_fm_tag_entry'",(schema,)):
  fm.execute(db,'ALTER TABLE inventory_entry_tags ADD CONSTRAINT fk_fm_tag_entry FOREIGN KEY(entry_id) REFERENCES inventory_entries(id) ON DELETE CASCADE')
 triggers={
 'fm_entry_insert':"AFTER INSERT ON inventory_entries FOR EACH ROW INSERT INTO fm_entry_dirty(entry_id) VALUES(NEW.id) ON DUPLICATE KEY UPDATE generation=generation+1",
 'fm_entry_update':"AFTER UPDATE ON inventory_entries FOR EACH ROW BEGIN IF NEW.revision<>OLD.revision OR NOT(NEW.record_sha256 <=> OLD.record_sha256) THEN INSERT INTO fm_entry_dirty(entry_id) VALUES(NEW.id) ON DUPLICATE KEY UPDATE generation=generation+1; INSERT INTO fm_file_dirty(file_id) SELECT DISTINCT COALESCE(f.canonical_file_id,f.id) FROM inventory_entry_files l JOIN files f ON f.id=l.file_id WHERE l.entry_id=NEW.id ON DUPLICATE KEY UPDATE generation=generation+1; END IF; END",
 'fm_file_insert':"AFTER INSERT ON files FOR EACH ROW INSERT INTO fm_file_dirty(file_id) VALUES(NEW.id) ON DUPLICATE KEY UPDATE generation=generation+1",
 'fm_file_update':"AFTER UPDATE ON files FOR EACH ROW BEGIN IF NOT(NEW.relative_path <=> OLD.relative_path) OR NOT(NEW.collection_id <=> OLD.collection_id) OR NOT(NEW.byte_size <=> OLD.byte_size) OR NOT(NEW.modified_utc_ticks <=> OLD.modified_utc_ticks) OR NOT(NEW.canonical_file_id <=> OLD.canonical_file_id) OR NOT(NEW.is_present <=> OLD.is_present) OR NOT(NEW.width <=> OLD.width) OR NOT(NEW.height <=> OLD.height) THEN INSERT INTO fm_file_dirty(file_id) VALUES(NEW.id) ON DUPLICATE KEY UPDATE generation=generation+1; INSERT INTO fm_entry_dirty(entry_id) SELECT entry_id FROM inventory_entry_files WHERE file_id=NEW.id ON DUPLICATE KEY UPDATE generation=generation+1; END IF; END",
 'fm_link_delete':"AFTER DELETE ON inventory_entry_files FOR EACH ROW BEGIN IF OLD.file_id IS NOT NULL THEN INSERT INTO fm_file_dirty(file_id) VALUES(OLD.file_id) ON DUPLICATE KEY UPDATE generation=generation+1; END IF; END",
 'fm_link_update':"AFTER UPDATE ON inventory_entry_files FOR EACH ROW BEGIN IF NOT(NEW.file_id <=> OLD.file_id) THEN IF OLD.file_id IS NOT NULL THEN INSERT INTO fm_file_dirty(file_id) VALUES(OLD.file_id) ON DUPLICATE KEY UPDATE generation=generation+1; END IF; IF NEW.file_id IS NOT NULL THEN INSERT INTO fm_file_dirty(file_id) VALUES(NEW.file_id) ON DUPLICATE KEY UPDATE generation=generation+1; END IF; END IF; END",
 }
 for name,body in triggers.items():
  present=fm.rows(db,'SELECT ACTION_STATEMENT FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA=%s AND TRIGGER_NAME=%s',(schema,name))
  desired=body.split('FOR EACH ROW ',1)[1]
  if present and ' '.join(present[0]['ACTION_STATEMENT'].split()).casefold()!=' '.join(desired.split()).casefold():
   fm.execute(db,'DROP TRIGGER `'+name+'`');present=[]
  if not present:
   fm.execute(db,'CREATE TRIGGER `'+name+'` '+body)
 fm.execute(db,"UPDATE inventory_entry_files SET reference_path_key=UNHEX(SHA2(LOWER(REPLACE(CONCAT(IF(recorded_collection='.', '', CONCAT(recorded_collection,'/')),recorded_path),CHAR(92),'/')),256)) WHERE reference_path_key IS NULL")
 fm.execute(db,"UPDATE files SET file_name=SUBSTRING_INDEX(relative_path,'/',-1) WHERE file_name IS NULL")
 fm.execute(db,"INSERT IGNORE INTO fm_adapter_config(integration_key,database_name,enabled,status) VALUES('booru','booru',FALSE,'Waiting for booru schema migration'),('wallpaper-download','wallpaper-desk',FALSE,'Waiting for downloader MySQL migration')")
 fm.execute(db,"INSERT IGNORE INTO fm_integrations(integration_key,role) VALUES('booru','booru'),('wallpaper-download','downloader'),('legacy-gallery','manual')")
 for key,path in [('booru',r'\\nas.thelinclan.com\data\wallpapers\booru'),('wallpaper-download',r'\\nas.thelinclan.com\data\wallpapers\wallpaper-download')]:
  found=fm.rows(db,'SELECT r.id FROM fm_intake_roots r JOIN fm_integrations i ON i.id=r.integration_id WHERE i.integration_key=%s',(key,))
  if not found:fm.execute(db,'INSERT INTO fm_intake_roots(integration_id,unc_path) SELECT id,%s FROM fm_integrations WHERE integration_key=%s',(path,key))
 # Missing relational projections are queued once; subsequent runs are incremental.
 fm.execute(db,'INSERT IGNORE INTO fm_entry_dirty(entry_id) SELECT e.id FROM inventory_entries e LEFT JOIN fm_record_index i ON i.entry_id=e.id WHERE i.entry_id IS NULL OR NOT(i.record_sha256 <=> e.record_sha256)')
 fm.execute(db,"INSERT IGNORE INTO fm_file_dirty(file_id) SELECT f.id FROM files f LEFT JOIN fm_asset_locations l ON l.legacy_file_id=f.id WHERE l.id IS NULL OR (f.is_present=TRUE AND f.canonical_file_id IS NULL AND f.fm_metadata_ticks IS NULL AND EXISTS(SELECT 1 FROM file_hashes h WHERE h.file_id=f.id AND h.hash_kind='pixel_sha256' AND h.source_byte_size=f.byte_size AND h.source_modified_utc_ticks=f.modified_utc_ticks))")
 version=fm.rows(db,"SELECT setting_value FROM fm_control WHERE setting_key='projection_version'")
 if not version or version[0]['setting_value']!='20261028.1':
  fm.execute(db,'INSERT IGNORE INTO fm_entry_dirty(entry_id) SELECT id FROM inventory_entries')
  fm.execute(db,'INSERT IGNORE INTO fm_file_dirty(file_id) SELECT id FROM files')
  fm.execute(db,"INSERT INTO fm_control VALUES('projection_version','20261028.1') ON DUPLICATE KEY UPDATE setting_value=VALUES(setting_value)")
 marker=fm.rows(db,"SELECT value FROM gallery_state WHERE state_key='collection_markers'")
 if marker:fm.discover_markers(db,fm.decode(marker[0]['value']).get('collections',[]))
 fm.execute(db,"INSERT IGNORE INTO schema_migrations(version,description) VALUES(10,'File manager relational tags, bounded browsing, assets and incremental index queues')")
 db.commit()

def main():
 p=argparse.ArgumentParser();p.add_argument('--defaults-file',required=True);p.add_argument('--database',required=True);p.add_argument('--backup');p.add_argument('--drain',action='store_true');a=p.parse_args()
 if a.database=='wallpapers' and (not a.backup or not pathlib.Path(a.backup).is_file()):p.error('A verified production backup path is required')
 if a.database!='wallpapers' and not a.database.startswith('wallpapers_fm_'):p.error('Unexpected database name')
 import pymysql
 c=configparser.ConfigParser(interpolation=None);c.read(a.defaults_file);v=c['client']
 db=pymysql.connect(host=v.get('host','localhost'),user=v['user'],password=v['password'],database=a.database,autocommit=True,charset='utf8mb4',cursorclass=pymysql.cursors.DictCursor)
 if a.database!='wallpapers':fm.execute(db,'SET SESSION sql_log_bin=0')
 else:fm.execute(db,"SET SESSION binlog_row_image='MINIMAL'")
 migrate(db,a.database)
 if a.drain:
  count=0
  while True:
   n=fm.drain(db,batches=10);count+=n
   print('Indexed',count,flush=True)
   if not n:break
 print(json.dumps({'database':a.database,'version':10,'tags':fm.rows(db,'SELECT COUNT(*) n FROM tags')[0]['n'],'tag_relations':fm.rows(db,'SELECT COUNT(*) n FROM inventory_entry_tags')[0]['n'],'tag_import_issues':fm.rows(db,'SELECT COUNT(*) n FROM fm_tag_import_issues')[0]['n']}),flush=True)
 db.close()
if __name__=='__main__':main()
