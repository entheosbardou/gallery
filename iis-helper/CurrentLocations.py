"""Keep current file pointers, without persistent previous-location history."""
import hashlib,json
PATH_FIELDS={'file','file_collection','duplicate_of','duplicate_collection','related_image_references','saved_image_references','related_image_files','images_saved','file_path','local_path','relative_path','recorded_path','recorded_collection','source_path','target_path','origin_collection'}
HISTORY_FIELDS=PATH_FIELDS|{'image_outcomes','download_collection','collection_override','related_record_collection','duplicate_record_collection'}

def history_sql(expr):return 'JSON_REMOVE('+expr+','+','.join("'$."+name+"'" for name in sorted(HISTORY_FIELDS))+')'

def install_history_triggers(db):
 import FileManager as F
 for event in ['INSERT','UPDATE']:
  name='history_current_locations_'+event.lower();F.execute(db,'DROP TRIGGER IF EXISTS '+name)
  F.execute(db,'CREATE TRIGGER '+name+' BEFORE '+event+' ON inventory_entry_history FOR EACH ROW BEGIN SET NEW.previous_record='+history_sql('NEW.previous_record')+'; SET NEW.new_record='+history_sql('NEW.new_record')+'; SET NEW.details='+history_sql('NEW.details')+'; END')

def install_move_privacy(db):
 import FileManager as F
 nullable=F.rows(db,"SELECT IS_NULLABLE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='fm_collection_moves' AND COLUMN_NAME='target_collection_id'")[0]['IS_NULLABLE']
 if nullable=='NO':F.execute(db,'ALTER TABLE fm_collection_moves MODIFY target_collection_id BIGINT UNSIGNED NULL')
 for event in ['INSERT','UPDATE']:
  name='move_current_location_'+event.lower();F.execute(db,'DROP TRIGGER IF EXISTS '+name)
  F.execute(db,'CREATE TRIGGER '+name+' BEFORE '+event+" ON fm_collection_moves FOR EACH ROW BEGIN IF NEW.state='completed' THEN SET NEW.source_collection_id=NULL; SET NEW.target_collection_id=NULL; END IF; END")
 F.execute(db,"UPDATE fm_collection_moves SET source_collection_id=NULL,target_collection_id=NULL WHERE state='completed' AND (source_collection_id IS NOT NULL OR target_collection_id IS NOT NULL)")

def clean(value):
 if isinstance(value,dict):return {k:clean(v) for k,v in value.items() if k not in PATH_FIELDS}
 if isinstance(value,list):return [clean(v) for v in value]
 return value

def clean_entry(db,e):
 import FileManager as F
 raw=F.decode(e['raw_record']);safe=clean(raw)
 if raw==safe:return
 text=json.dumps(safe,sort_keys=True,ensure_ascii=False,separators=(',',':'))
 F.execute(db,'UPDATE inventory_entries SET raw_record=%s,record_sha256=%s,origin_collection_id=NULL WHERE id=%s',(text,hashlib.sha256(text.encode()).digest(),e['id']))

def housekeeping(db):
 import FileManager as F
 # Pending/failed operations retain the temporary recovery information they need.
 F.execute(db,"UPDATE file_operations SET source_path='',target_path='',error=NULL WHERE state IN ('complete','cancelled') AND (source_path<>'' OR target_path<>'' OR error IS NOT NULL)")
 F.execute(db,"UPDATE fm_database_sync SET expected_old_path=NULL,new_path=NULL,error=NULL WHERE state='completed' AND (expected_old_path IS NOT NULL OR new_path IS NOT NULL OR error IS NOT NULL)")
 F.execute(db,"UPDATE files f SET relative_path='',path_key=UNHEX(SHA2(CONCAT('retired-file:',f.id),256)),file_name=NULL WHERE f.canonical_file_id IS NOT NULL AND f.relative_path IS NOT NULL AND f.relative_path<>'' AND NOT EXISTS(SELECT 1 FROM file_operations o WHERE (o.source_file_id=f.id OR o.target_file_id=f.id) AND o.state NOT IN ('complete','cancelled'))")
 F.execute(db,"UPDATE fm_asset_locations l JOIN files f ON f.id=l.legacy_file_id SET l.relative_path='',l.path_key=f.path_key,l.collection_id=NULL WHERE (f.relative_path IS NULL OR f.relative_path='') AND l.relative_path IS NOT NULL AND l.relative_path<>''")

def migrate(db):
 import FileManager as F
 if not F.rows(db,"SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='fm_collection_moves' AND COLUMN_NAME='source_collection_id'"):
  F.execute(db,'ALTER TABLE fm_collection_moves ADD COLUMN source_collection_id BIGINT UNSIGNED NULL,ADD CONSTRAINT fk_move_source_collection FOREIGN KEY(source_collection_id) REFERENCES collections(id) ON DELETE SET NULL')
  F.execute(db,"UPDATE fm_collection_moves m JOIN inventory_entries e ON e.id=m.entry_id SET m.source_collection_id=e.collection_id WHERE m.state IN ('pending','running','failed')")
 install_move_privacy(db)
 for event in ['INSERT','UPDATE']:
  name='file_operation_current_'+event.lower();F.execute(db,'DROP TRIGGER IF EXISTS '+name)
  F.execute(db,'CREATE TRIGGER '+name+" BEFORE "+event+" ON file_operations FOR EACH ROW BEGIN IF NEW.state IN ('complete','cancelled') THEN SET NEW.source_path=''; SET NEW.target_path=''; SET NEW.error=NULL; END IF; END")
 F.execute(db,'DROP TRIGGER IF EXISTS file_operation_retired_path')
 F.execute(db,"CREATE TRIGGER file_operation_retired_path AFTER UPDATE ON file_operations FOR EACH ROW BEGIN IF NEW.state IN ('complete','cancelled') THEN UPDATE files SET relative_path='',path_key=UNHEX(SHA2(CONCAT('retired-file:',id),256)),file_name=NULL WHERE id=NEW.source_file_id AND canonical_file_id IS NOT NULL AND relative_path<>''; UPDATE fm_asset_locations l JOIN files f ON f.id=l.legacy_file_id SET l.relative_path='',l.path_key=f.path_key,l.collection_id=NULL WHERE f.id=NEW.source_file_id AND (f.relative_path IS NULL OR f.relative_path=''); END IF; END")
 if F.rows(db,'SELECT version FROM schema_migrations WHERE version=12'):
  install_history_triggers(db);return
 F.execute(db,'UPDATE inventory_documents SET original_content=NULL WHERE original_content IS NOT NULL')
 F.execute(db,"UPDATE collection_documents SET json_content=NULL,text_content=NULL WHERE document_kind IN ('inventory_input','source_report')")
 # History remains useful for metadata edits, but contains no saved file paths.
 F.execute(db,'UPDATE inventory_entry_history SET previous_record='+history_sql('previous_record')+',new_record='+history_sql('new_record')+',details='+history_sql('details'))
 install_history_triggers(db)
 trigger=F.rows(db,"SELECT ACTION_STATEMENT FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA=DATABASE() AND TRIGGER_NAME='fm_entry_update'")
 if trigger:F.execute(db,'DROP TRIGGER fm_entry_update')
 last=0
 while True:
  batch=F.rows(db,'SELECT id,raw_record FROM inventory_entries WHERE id>%s ORDER BY id LIMIT 500',(last,))
  if not batch:break
  db.begin()
  try:
   for e in batch:clean_entry(db,e)
   db.commit()
  except Exception:db.rollback();raise
  last=batch[-1]['id'];print('Removed path snapshots through entry '+str(last),flush=True)
 if trigger:F.execute(db,'CREATE TRIGGER fm_entry_update AFTER UPDATE ON inventory_entries FOR EACH ROW '+trigger[0]['ACTION_STATEMENT'])
 housekeeping(db)
 for cfg in F.rows(db,"SELECT database_name FROM fm_adapter_config WHERE integration_key='wallpaper-download' AND enabled=TRUE"):
  fingerprint=F.adapter_fingerprint(db,cfg['database_name'],'wallpaper-files')
  F.execute(db,"UPDATE fm_adapter_config SET schema_sha256=%s WHERE integration_key='wallpaper-download'",(fingerprint,))
 F.execute(db,"INSERT INTO schema_migrations(version,description) VALUES(12,'Current file locations only; remove previous paths from completed work and source snapshots')")
