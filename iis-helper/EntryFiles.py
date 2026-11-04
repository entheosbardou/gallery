"""One file per image entry; parents reach files through parent_of relations."""
import hashlib,json,posixpath

PARENTS=('post','album','post_or_album')

def install_triggers(db):
 import FileManager as F
 definitions={
 'fm_entry_insert':"AFTER INSERT ON inventory_entries FOR EACH ROW BEGIN INSERT INTO fm_entry_dirty(entry_id) VALUES(NEW.id) ON DUPLICATE KEY UPDATE generation=generation+1; IF NEW.file_id IS NOT NULL THEN INSERT INTO fm_file_dirty(file_id) VALUES(NEW.file_id) ON DUPLICATE KEY UPDATE generation=generation+1; END IF; END",
 'fm_entry_update':"""AFTER UPDATE ON inventory_entries FOR EACH ROW BEGIN
 IF NEW.revision<>OLD.revision OR NOT(NEW.record_sha256 <=> OLD.record_sha256) THEN
 INSERT INTO fm_entry_dirty(entry_id) VALUES(NEW.id) ON DUPLICATE KEY UPDATE generation=generation+1;
 IF NEW.file_id IS NOT NULL THEN INSERT INTO fm_file_dirty(file_id) VALUES(NEW.file_id) ON DUPLICATE KEY UPDATE generation=generation+1; END IF;
 IF OLD.file_id IS NOT NULL AND NOT(NEW.file_id <=> OLD.file_id) THEN INSERT INTO fm_file_dirty(file_id) VALUES(OLD.file_id) ON DUPLICATE KEY UPDATE generation=generation+1; END IF;
 INSERT INTO fm_file_dirty(file_id) SELECT child.file_id FROM inventory_entry_relations r JOIN inventory_entries child ON child.id=r.related_entry_id WHERE r.entry_id=NEW.id AND r.relation_kind='parent_of' AND child.file_id IS NOT NULL ON DUPLICATE KEY UPDATE generation=generation+1;
 INSERT INTO fm_entry_dirty(entry_id) SELECT r.entry_id FROM inventory_entry_relations r WHERE r.related_entry_id=NEW.id AND r.relation_kind='parent_of' ON DUPLICATE KEY UPDATE generation=generation+1;
 END IF; END"""
 }
 for event,ref in [('INSERT','NEW'),('DELETE','OLD')]:
  definitions['entry_relation_'+event.lower()]=f"""AFTER {event} ON inventory_entry_relations FOR EACH ROW BEGIN
 INSERT INTO fm_entry_dirty(entry_id) VALUES({ref}.entry_id) ON DUPLICATE KEY UPDATE generation=generation+1;
 IF {ref}.relation_kind='parent_of' THEN INSERT INTO fm_file_dirty(file_id) SELECT file_id FROM inventory_entries WHERE id={ref}.related_entry_id AND file_id IS NOT NULL ON DUPLICATE KEY UPDATE generation=generation+1; END IF;
 END"""
 for name,body in definitions.items():
  F.execute(db,'DROP TRIGGER IF EXISTS '+name);F.execute(db,'CREATE TRIGGER '+name+' '+body)

def bind(db,entry_id,file_id):
 with db.cursor() as c:
  c.execute('CALL fm_bind_entry_file(%s,%s)',(entry_id,file_id))
  while c.nextset():pass

def resolve(db,owner,name):
 import FileManager as F
 relative=posixpath.normpath(((owner+'/' if owner!='.' else '')+name).replace('\\','/'))
 if relative.startswith('../') or relative.startswith('/') or ':' in relative:raise ValueError('Inventory file path is outside the wallpaper root')
 key=hashlib.sha256(relative.casefold().encode()).digest()
 files=F.rows(db,'SELECT COALESCE(canonical_file_id,id) id FROM files WHERE path_key=%s',(key,))
 if files:return files[0]['id']
 # Missing downloads still have one authoritative path; never mark them present.
 return F.execute(db,'INSERT INTO files(relative_path,path_key,file_name,byte_size,modified_utc_ticks,is_present,last_seen_at) VALUES(%s,%s,%s,0,0,FALSE,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id)',(relative,key,posixpath.basename(relative)))[0]

def normalize(db,e,raw,refs,signature):
 import FileManager as F
 previous=e.get('indexed_references_sha256')
 changed=previous is not None and bytes(previous)!=signature
 graph=F.rows(db,"SELECT id FROM inventory_entry_relations WHERE entry_id=%s AND relation_kind='parent_of' LIMIT 1",(e['id'],)) if e['record_type'] in PARENTS else []
 if graph and (raw.get('collection_override') or F.rows(db,"SELECT p.file_id FROM inventory_entry_relations r JOIN inventory_entries child ON child.id=r.related_entry_id JOIN fm_file_placement p ON p.file_id=child.file_id WHERE r.entry_id=%s AND r.relation_kind='parent_of' LIMIT 1",(e['id'],))):return
 if graph and (not refs or (previous is not None and not changed)):return
 # A manual binding remains authoritative until source references actually change.
 if e['record_type'] not in PARENTS and e.get('file_id') is not None:return
 found=[]
 for kind,owner,name in refs:
  if graph:
   relative=posixpath.normpath(((owner+'/' if owner!='.' else '')+name).replace('\\','/'))
   known=F.rows(db,'SELECT COALESCE(canonical_file_id,id) id FROM files WHERE path_key=%s',(hashlib.sha256(relative.casefold().encode()).digest(),))
   if not known:continue  # An old source filename cannot recreate a moved file.
   fid=known[0]['id']
  else:fid=resolve(db,owner,name)
  if fid not in found:found.append(fid)
 if e['record_type'] not in PARENTS:
  if len(found)>1:raise ValueError('Image entry has multiple distinct file references: '+str(e['id']))
  if changed:F.execute(db,'UPDATE inventory_entries SET file_id=NULL WHERE id=%s',(e['id'],))
 for fid in found:bind(db,e['id'],fid)
