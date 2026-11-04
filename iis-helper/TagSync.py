"""Bounded, repeatable downloader-to-gallery tag synchronization."""
from collections import defaultdict
import FileManager as F

SOURCES=(('booru','booru','booru-locations'),('wallpaper-download','wallpaper-desk','wallpaper-current'))

def sync_batch(db,limit=100):
 total=0
 for integration,schema,kind in SOURCES:
  cfg=F.rows(db,'SELECT i.id FROM fm_integrations i JOIN fm_adapter_config c ON c.integration_key=i.integration_key WHERE i.integration_key=%s AND c.enabled=TRUE AND c.adapter_kind=%s',(integration,kind))
  if not cfg:continue
  iid=cfg[0]['id']
  state=F.rows(db,'SELECT last_external_id FROM fm_tag_sync_cursor WHERE integration_id=%s',(iid,))
  after=state[0]['last_external_id'] if state else ''
  bindings=F.rows(db,'SELECT external_id,file_id FROM fm_external_bindings WHERE integration_id=%s AND adapter_kind=%s AND external_id>%s ORDER BY external_id LIMIT %s',(iid,kind,after,limit))
  if not bindings:
   F.execute(db,'INSERT INTO fm_tag_sync_cursor(integration_id,last_external_id) VALUES(%s,%s) ON DUPLICATE KEY UPDATE last_external_id=VALUES(last_external_id)',(iid,''))
   continue
  ids=[int(b['external_id']) for b in bindings]
  marks=','.join(['%s']*len(ids))
  if kind=='booru-locations':
   sql=f'SELECT l.id external_id,pm.post_id,pt.tag_id,t.name FROM `{schema}`.file_locations l JOIN `{schema}`.media_files mf ON mf.file_id=l.file_id JOIN `{schema}`.post_media pm ON pm.id=mf.media_id LEFT JOIN `{schema}`.post_tags pt ON pt.post_id=pm.post_id LEFT JOIN `{schema}`.tags t ON t.id=pt.tag_id WHERE l.id IN ({marks})'
  else:
   sql=f'SELECT mf.file_id external_id,pm.post_id,pt.tag_id,t.name FROM `{schema}`.media_files mf JOIN `{schema}`.post_media pm ON pm.id=mf.media_id LEFT JOIN `{schema}`.post_tags pt ON pt.post_id=pm.post_id LEFT JOIN `{schema}`.tags t ON t.id=pt.tag_id WHERE mf.file_id IN ({marks})'
  source=F.rows(db,sql,ids)
  entries=F.rows(db,'SELECT id,file_id FROM inventory_entries WHERE file_id IN ('+','.join(['%s']*len(bindings))+') AND record_type=%s AND deleted_at IS NULL',[*[b['file_id'] for b in bindings],'image'])
  byfile=defaultdict(list)
  for e in entries:byfile[e['file_id']].append(e['id'])
  byexternal={int(b['external_id']):b['file_id'] for b in bindings}
  pairs=defaultdict(set);terms={}
  for r in source:
   if r['post_id'] is None:continue
   for entry in byfile[byexternal[r['external_id']]]:
    pair=(int(r['post_id']),entry)
    pairs[pair]
    if r['tag_id'] is not None and isinstance(r['name'],str) and 0<len(r['name'])<=512:
     pairs[pair].add(int(r['tag_id']));terms[int(r['tag_id'])]=r['name']
  db.begin()
  try:
   mapped={r['external_tag_id']:r['tag_id'] for r in F.rows(db,'SELECT external_tag_id,tag_id FROM fm_external_tag_map WHERE integration_id=%s AND external_tag_id IN ('+','.join(['%s']*len(terms))+')',[iid,*terms])} if terms else {}
   for external_id,name in terms.items():
    if external_id in mapped:continue
    tag_id,_=F.execute(db,'INSERT INTO tags(term) VALUES(%s) ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id)',(name,))
    F.execute(db,'INSERT INTO fm_external_tag_map(integration_id,external_tag_id,tag_id) VALUES(%s,%s,%s)',(iid,external_id,tag_id))
    mapped[external_id]=tag_id
   affected=defaultdict(set)
   for (post,entry),source_ids in pairs.items():
    old=F.rows(db,'SELECT tag_id FROM fm_external_entry_tags WHERE integration_id=%s AND external_post_id=%s AND entry_id=%s',(iid,post,entry))
    affected[entry].update(r['tag_id'] for r in old)
    F.execute(db,'DELETE FROM fm_external_entry_tags WHERE integration_id=%s AND external_post_id=%s AND entry_id=%s',(iid,post,entry))
    for source_id in source_ids:
     tag_id=mapped[source_id]
     F.execute(db,'INSERT IGNORE INTO fm_external_entry_tags(integration_id,external_post_id,entry_id,tag_id) VALUES(%s,%s,%s,%s)',(iid,post,entry,tag_id))
     F.execute(db,'INSERT IGNORE INTO inventory_entry_tags(entry_id,tag_id) VALUES(%s,%s)',(entry,tag_id))
     affected[entry].discard(tag_id)
   for entry,tag_ids in affected.items():
    for tag_id in tag_ids:
     F.execute(db,'DELETE et FROM inventory_entry_tags et WHERE et.entry_id=%s AND et.tag_id=%s AND NOT EXISTS(SELECT 1 FROM fm_external_entry_tags ext WHERE ext.entry_id=et.entry_id AND ext.tag_id=et.tag_id) AND NOT EXISTS(SELECT 1 FROM fm_entry_bridge br JOIN fm_source_tags native ON native.source_record_id=br.source_record_id AND native.tag_id=et.tag_id WHERE br.legacy_entry_id=et.entry_id)',(entry,tag_id))
   F.execute(db,'INSERT INTO fm_tag_sync_cursor(integration_id,last_external_id) VALUES(%s,%s) ON DUPLICATE KEY UPDATE last_external_id=VALUES(last_external_id)',(iid,bindings[-1]['external_id']))
   db.commit();total+=len(bindings)
  except Exception:db.rollback();raise
 return total
