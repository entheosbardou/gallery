import argparse,configparser,json
import pymysql
import FileManager as F,CurrentLocations as L
p=argparse.ArgumentParser();p.add_argument('--database',required=True);p.add_argument('--prepare-clone',action='store_true');a=p.parse_args()
c=configparser.ConfigParser(interpolation=None);c.read('/etc/mysql/debian.cnf');v=c['client']
db=pymysql.connect(host='localhost',user=v['user'],password=v['password'],database=a.database,autocommit=True,charset='utf8mb4',cursorclass=pymysql.cursors.DictCursor)
if a.prepare_clone:
 assert a.database=='wallpapers_fm_entries_20261029'
 L.migrate(db)
 for cfg in F.rows(db,"SELECT database_name FROM fm_adapter_config WHERE integration_key='wallpaper-download' AND enabled=TRUE"):
  F.execute(db,"UPDATE fm_adapter_config SET schema_sha256=%s WHERE integration_key='wallpaper-download'",(F.adapter_fingerprint(db,cfg['database_name'],'wallpaper-files'),))
 for e in F.rows(db,'SELECT e.* FROM inventory_entries e LEFT JOIN fm_record_index i ON i.entry_id=e.id WHERE i.entry_id IS NULL'):
  db.begin()
  try:F.index_entry(db,e);db.commit()
  except Exception:db.rollback();raise
checks={
 'no_file_junction_table':"SELECT COUNT(*) n FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='inventory_entry_files'",
 'no_files_collection_column':"SELECT COUNT(*) n FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='files' AND COLUMN_NAME='collection_id'",
 'no_relation_position_or_details':"SELECT COUNT(*) n FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='inventory_entry_relations' AND COLUMN_NAME IN ('position','details')",
 'parents_have_no_file':"SELECT COUNT(*) n FROM inventory_entries WHERE record_type IN ('post','album','post_or_album') AND file_id IS NOT NULL",
 'all_entries_have_valid_collection':"SELECT COUNT(*) n FROM inventory_entries e LEFT JOIN collections c ON c.id=e.collection_id WHERE c.id IS NULL",
 'all_file_pointers_valid':"SELECT COUNT(*) n FROM inventory_entries e LEFT JOIN files f ON f.id=e.file_id WHERE e.file_id IS NOT NULL AND f.id IS NULL",
 'no_completed_move_path_history':"SELECT COUNT(*) n FROM file_operations WHERE state IN ('complete','cancelled') AND (source_path<>'' OR target_path<>'')",
 'no_completed_sync_path_history':"SELECT COUNT(*) n FROM fm_database_sync WHERE state='completed' AND (expected_old_path IS NOT NULL OR new_path IS NOT NULL)",
 'no_retired_paths_without_active_work':"SELECT COUNT(*) n FROM files f WHERE canonical_file_id IS NOT NULL AND relative_path IS NOT NULL AND relative_path<>'' AND NOT EXISTS(SELECT 1 FROM file_operations o WHERE o.source_file_id=f.id AND o.state NOT IN ('complete','cancelled'))",
 'no_entry_path_snapshots':"SELECT COUNT(*) n FROM inventory_entries WHERE JSON_CONTAINS_PATH(raw_record,'one','$.file','$.duplicate_of','$.related_image_references','$.saved_image_references','$.related_image_files')",
 'no_inventory_input_snapshots':"SELECT COUNT(*) n FROM inventory_documents WHERE original_content IS NOT NULL AND original_content<>'[]'"
}
report={name:F.rows(db,sql)[0]['n']==0 for name,sql in checks.items()}
report['counts']={t:F.rows(db,'SELECT COUNT(*) n FROM '+t)[0]['n'] for t in ['inventory_entries','files','inventory_entry_relations','file_hashes','tags','inventory_entry_tags']}
report['hash_fingerprint']=F.rows(db,'SELECT COUNT(*) n,BIT_XOR(CRC32(CONCAT(id,HEX(hash_value)))) checksum FROM file_hashes')[0]
report['passed']=all(v is True for k,v in report.items() if k not in ('counts','hash_fingerprint'))
print(json.dumps(report,indent=2));assert report['passed'],report
