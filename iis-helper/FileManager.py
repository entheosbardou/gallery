"""Relational gallery index. All catalog batches are bounded and restartable."""
import json,hashlib,time,uuid,datetime,re,threading

# Structural asset writes must not interleave with duplicate-link transactions.
asset_write_lock=threading.RLock()

def rows(db,sql,args=()):
 with db.cursor() as c:c.execute(sql,args);return list(c.fetchall())
def execute(db,sql,args=()):
 with db.cursor() as c:c.execute(sql,args);return c.lastrowid,c.rowcount
def decode(value):return json.loads(value) if isinstance(value,(str,bytes)) else value
def color_signature(meta):
 values=[]
 for key in ['icc_profile_sha256','gamma','srgb','chromaticity']:
  value=meta.get(key);text='' if value is None else str(value)
  values.append(text.lower() if key=='icc_profile_sha256' else text)
 return hashlib.sha256('\0'.join(values).encode()).digest()

def tag_terms(value,site=''):
 """Do not invent tags from AlphaCoders' scraped card captions."""
 if value is None:return [],None
 if isinstance(value,str):
  try:value=json.loads(value)
  except ValueError:return [],'unstructured_source_text'
 if not isinstance(value,list):return [],'unsupported_tag_shape'
 terms=[];issue=None
 for item in value:
  if isinstance(item,dict):item=item.get('name',item.get('term'))
  if not isinstance(item,str):issue='unsupported_tag_item';continue
  term=item.strip()
  if not term or len(term)>512:issue='invalid_tag_length';continue
  if term not in terms:terms.append(term)
 return terms,issue

META=['variant','image_title','author','uploader','purity','source_category','reason','error','manual_import','metadata_verified','visual_review','age_assessment','exclusion_basis','duplicate_detected_by','image_count','saved_image_count','last_retry_utc']

def index_entry(db,e):
 raw=decode(e['raw_record'])
 if not isinstance(raw,dict):raise ValueError('Entry record is not an object')
 effective=raw.copy()
 for field in ['description','resolution','source_url','image_url']:
  if e.get(field) is not None:effective[{'source_url':'source','image_url':'url'}.get(field,field)]=e[field]
 score=0
 for k,w in [('description',2),('image_title',2),('selftext',1),('tags',10),('source',10),('url',5),('resolution',10),('source_category',5),('purity',5),('age_assessment',5),('discovered_from',5)]:
  v=effective.get(k)
  if v:score+=min(len(json.dumps(v,ensure_ascii=False)),400)*w if k in ['description','image_title','selftext'] else (len(v)*w if isinstance(v,list) else w)
 score=min(score,2147483647)
 previous=rows(db,'SELECT record_sha256,tags_sha256 FROM fm_record_index WHERE entry_id=%s',(e['id'],))
 tag_signature=hashlib.sha256(json.dumps({'present':'tags' in raw,'value':raw.get('tags')},sort_keys=True,ensure_ascii=False,separators=(',',':')).encode()).digest()
 terms,issue=tag_terms(raw.get('tags'),e['site'])
 # Original JSON is retained; only validated terms become searchable tags.
 tag_changed=not previous or previous[0]['tags_sha256'] is None or bytes(previous[0]['tags_sha256'])!=tag_signature
 if tag_changed:
  execute(db,'DELETE FROM inventory_entry_tags WHERE entry_id=%s',(e['id'],))
  for term in terms:
   tid,_=execute(db,'INSERT INTO tags(term) VALUES(%s) ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id)',(term,))
   execute(db,'INSERT IGNORE INTO inventory_entry_tags(entry_id,tag_id) VALUES(%s,%s)',(e['id'],tid))
  execute(db,'INSERT IGNORE INTO inventory_entry_tags(entry_id,tag_id) SELECT entry_id,tag_id FROM fm_external_entry_tags WHERE entry_id=%s',(e['id'],))
 raw_tags=effective.get('tags');old_weight=(len(raw_tags)*10 if isinstance(raw_tags,list) else 10) if raw_tags else 0
 score=min(score-old_weight+len(terms)*10,2147483647)
 if score!=e['metadata_score']:execute(db,'UPDATE inventory_entries SET metadata_score=%s WHERE id=%s',(score,e['id']))
 if issue and tag_changed:
  execute(db,'INSERT INTO fm_tag_import_issues(entry_id,reason,source_text) VALUES(%s,%s,%s) ON DUPLICATE KEY UPDATE reason=VALUES(reason),source_text=VALUES(source_text)',(e['id'],issue,raw.get('tags') if isinstance(raw.get('tags'),str) else json.dumps(raw.get('tags'),ensure_ascii=False)))
 elif tag_changed:execute(db,'DELETE FROM fm_tag_import_issues WHERE entry_id=%s',(e['id'],))
 vals=[]
 for name in META:
  value=raw.get(name)
  if isinstance(value,(dict,list)):value=None
  vals.append(value)
 names=','.join('`'+n+'`' for n in META)
 if not previous or bytes(previous[0]['record_sha256'])!=bytes(e['record_sha256']):execute(db,'INSERT INTO fm_entry_metadata(legacy_entry_id,'+names+') VALUES('+','.join(['%s']*(len(vals)+1))+') ON DUPLICATE KEY UPDATE '+','.join('`'+n+'`=VALUES(`'+n+'`)' for n in META),[e['id'],*vals])
 execute(db,'INSERT INTO fm_record_index(entry_id,record_sha256,tags_sha256) VALUES(%s,%s,%s) ON DUPLICATE KEY UPDATE record_sha256=VALUES(record_sha256),tags_sha256=VALUES(tags_sha256)',(e['id'],e['record_sha256'],tag_signature))
 normalize_entry(db,e,raw,bool(previous and bytes(previous[0]['record_sha256'])!=bytes(e['record_sha256'])))
 route_entry(db,e,raw)
 bridge=rows(db,'SELECT source_record_id FROM fm_entry_bridge WHERE legacy_entry_id=%s',(e['id'],))
 if bridge:
  source=bridge[0]['source_record_id'];execute(db,'UPDATE fm_source_records SET source_url=%s,image_url=%s,title=%s,description=%s WHERE id=%s',(e['source_url'],e['image_url'],raw.get('image_title'),e['description'],source))
 else:
  integration=rows(db,"SELECT id FROM fm_integrations WHERE integration_key='legacy-gallery'")[0]['id']
  source,_=execute(db,'INSERT INTO fm_source_records(integration_id,source_identity_sha256,site,external_id,variant,record_kind,source_url,image_url,title,description) VALUES(%s,%s,%s,%s,%s,%s,%s,%s,%s,%s) ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id),source_url=VALUES(source_url),image_url=VALUES(image_url),title=VALUES(title),description=VALUES(description)',(integration,hashlib.sha256(str(e['id']).encode()).digest(),e['site'],e['source_id'],str(raw.get('variant',''))[:255],e['record_type'],e['source_url'],e['image_url'],raw.get('image_title'),e['description']))
  execute(db,'INSERT IGNORE INTO fm_entry_bridge VALUES(%s,%s)',(e['id'],source))
 execute(db,'DELETE FROM fm_source_tags WHERE source_record_id=%s',(source,))
 for pos,term in enumerate(terms):execute(db,'INSERT INTO fm_source_tags(source_record_id,tag_id,position) SELECT %s,id,%s FROM tags WHERE term=%s',(source,pos,term))
 execute(db,'DELETE FROM fm_source_assets WHERE source_record_id=%s',(source,))
 seen=set()
 links=rows(db,'SELECT COALESCE(f.canonical_file_id,f.id) file_id FROM fm_entry_file_projection l JOIN fm_file_catalog f ON f.id=l.file_id WHERE l.entry_id=%s ORDER BY FIELD(l.reference_kind,\'primary\',\'duplicate\',\'related\',\'saved\'),l.position',(e['id'],))
 for link in links:
  if link['file_id'] in seen:continue
  seen.add(link['file_id']);asset,_=execute(db,'INSERT INTO fm_assets(legacy_canonical_file_id) VALUES(%s) ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id)',(link['file_id'],))
  execute(db,'INSERT INTO fm_source_assets VALUES(%s,%s,%s)',(source,len(seen)-1,asset))
 import CurrentLocations
 CurrentLocations.clean_entry(db,e)
 if e['canonical_entry_id'] is not None:sync_duplicate_category_group(db,e['canonical_entry_id'])
 elif rows(db,'SELECT id FROM inventory_entries WHERE canonical_entry_id=%s AND deleted_at IS NULL LIMIT 1',(e['id'],)):sync_duplicate_category_group(db,e['id'])

def normalize_entry(db,e,raw,replace_graph=False):
 owner=rows(db,'SELECT c.collection_key FROM inventory_documents d JOIN collections c ON c.id=d.collection_id WHERE d.id=%s',(e['inventory_document_id'],))[0]['collection_key']
 refs=[]
 for field,kind,cf in [('file','primary','file_collection'),('duplicate_of','duplicate','duplicate_collection')]:
  if isinstance(raw.get(field),str) and raw[field]:refs.append((kind,raw.get(cf) or owner,raw[field]))
 for field,kind in [('related_image_references','related'),('saved_image_references','saved')]:
  for ref in raw.get(field,[]) or []:
   if isinstance(ref,dict) and isinstance(ref.get('file'),str):refs.append((kind,ref.get('collection') or owner,ref['file']))
 for name in raw.get('related_image_files',[]) or []:
  if isinstance(name,str):refs.append(('related',owner,name))
 refs=list(dict.fromkeys(refs));signature=hashlib.sha256(json.dumps(refs,sort_keys=True,ensure_ascii=False,separators=(',',':')).encode()).digest()
 previous=e.get('indexed_references_sha256')
 import EntryFiles
 EntryFiles.normalize(db,e,raw,refs,signature)
 if refs and (previous is None or bytes(previous)!=signature):execute(db,'UPDATE inventory_entries SET indexed_references_sha256=%s WHERE id=%s',(signature,e['id']))
 for field,kind in [('source','source'),('url','image'),('original_image_urls','original_image')]:
  values=e['source_url'] if field=='source' else e['image_url'] if field=='url' else raw.get(field,[]);values=[values] if isinstance(values,str) else values
  if values is None:
   execute(db,'DELETE FROM inventory_entry_urls WHERE entry_id=%s AND url_kind=%s',(e['id'],kind));continue
  if not isinstance(values,list):continue
  for pos,url in enumerate(values):
   if isinstance(url,str) and url:execute(db,'INSERT INTO inventory_entry_urls(entry_id,url_kind,position,url,url_sha256) VALUES(%s,%s,%s,%s,%s) ON DUPLICATE KEY UPDATE url=VALUES(url),url_sha256=VALUES(url_sha256)',(e['id'],kind,pos,url,hashlib.sha256(url.encode()).digest()))
 graph=rows(db,"SELECT id FROM inventory_entry_relations WHERE entry_id=%s AND relation_kind IN ('parent_of','duplicate_of') LIMIT 1",(e['id'],))
 if replace_graph or not graph:
  refs=[('parent_of',rid,e['site'],raw.get('related_record_collection') or owner) for rid in raw.get('related_image_records',[]) if isinstance(rid,str)]
  if raw.get('duplicate_record_of'):refs.append(('duplicate_of',str(raw['duplicate_record_of']),raw.get('duplicate_record_site') or e['site'],raw.get('duplicate_record_collection') or owner))
  for pos,(kind,rid,site,collection) in enumerate(refs):
   targets=rows(db,'SELECT e.id,e.record_type FROM inventory_entries e JOIN inventory_documents d ON d.id=e.inventory_document_id JOIN collections c ON c.id=e.collection_id WHERE e.site=%s AND e.source_id=%s AND c.collection_key=%s AND e.id<>%s AND e.deleted_at IS NULL',(site,rid,collection,e['id']))
   images=[t for t in targets if t['record_type']=='image'];targets=images or targets
   if len(targets)==1:execute(db,'INSERT IGNORE INTO inventory_entry_relations(entry_id,related_entry_id,relation_kind) VALUES(%s,%s,%s)',(e['id'],targets[0]['id'],kind))

def route_entry(db,e,raw):
 # Collection membership is explicit; provenance documents never change on routing.
 if e['deleted_at'] or raw.get('collection_override'):return
 target=raw.get('download_collection')
 if target:
  execute(db,'UPDATE inventory_entries SET collection_id=(SELECT id FROM collections WHERE collection_key=%s) WHERE id=%s AND collection_id<>(SELECT id FROM collections WHERE collection_key=%s)',(target,e['id'],target))


def folder_rank(relative,owner):
 name=relative if owner=='.' else relative[len(owner)+1:]
 ranks={'sfw':1,'not_provided':0,'sketchy':2,'nsfw':3}
 return max([ranks.get(p.lower(),0) for p in name.replace('\\','/').split('/')[:-1]]+[0])

def index_file(db,f):
 previous_location=rows(db,'SELECT l.is_present,l.byte_size,l.modified_utc_ticks,a.id asset_id,a.canonical_asset_id,a.width,a.height,a.category FROM fm_asset_locations l JOIN fm_assets a ON a.id=l.asset_id WHERE l.legacy_file_id=%s',(f['id'],))
 reappeared=bool(previous_location and f['is_present'] and not f['canonical_file_id'] and (not previous_location[0]['is_present'] or previous_location[0]['canonical_asset_id']))
 if reappeared and previous_location[0]['canonical_asset_id']:
  # A newly downloaded copy at an old path must not repurpose the old merge ID.
  execute(db,'UPDATE fm_assets SET legacy_canonical_file_id=NULL WHERE id=%s',(previous_location[0]['asset_id'],))
 if reappeared:execute(db,"UPDATE fm_work_items SET state='pending',failure_count=0,available_at=UTC_TIMESTAMP(6) WHERE file_id=%s AND expected_size=%s AND expected_ticks=%s AND state IN ('completed','cancelled') AND NOT EXISTS (SELECT 1 FROM gallery_state manual WHERE manual.state_key=CONCAT('manual_file_job:',fm_work_items.id))",(f['id'],f['byte_size'],f['modified_utc_ticks']))
 # Match the existing access policy: most restrictive file-linked label/folder wins.
 labels=rows(db,"SELECT COUNT(*) active_records,MAX(CASE WHEN e.record_type='image' THEN CASE LOWER(COALESCE(e.category,'')) WHEN 'sfw' THEN 1 WHEN 'sketchy' THEN 2 WHEN 'nsfw' THEN 3 ELSE 0 END ELSE CASE LOWER(COALESCE(e.category,'')) WHEN 'sketchy' THEN 2 WHEN 'nsfw' THEN 3 ELSE 0 END END) AS rank_value FROM (SELECT e.record_type,e.category FROM files f JOIN inventory_entries e ON e.file_id=f.id AND e.deleted_at IS NULL WHERE f.id=%s OR f.canonical_file_id=%s UNION ALL SELECT parent.record_type,parent.category FROM files f JOIN inventory_entries child ON child.file_id=f.id AND child.deleted_at IS NULL JOIN inventory_entry_relations r ON r.related_entry_id=child.id AND r.relation_kind='parent_of' JOIN inventory_entries parent ON parent.id=r.entry_id AND parent.deleted_at IS NULL WHERE f.id=%s OR f.canonical_file_id=%s) e",(f['id'],f['id'],f['id'],f['id']))[0]
 rank=max(labels['rank_value'] or 0,folder_rank(f['relative_path'],f['owner']))
 category={0:'not_provided',1:'sfw',2:'sketchy',3:'nsfw'}[rank] if labels['active_records'] else '__unlisted__'
 execute(db,'UPDATE files SET access_category=%s,file_name=%s WHERE id=%s AND (NOT(access_category <=> %s) OR NOT(file_name <=> %s))',(category,f['relative_path'].replace('\\','/').split('/')[-1],f['id'],category,f['relative_path'].replace('\\','/').split('/')[-1]))
 aid,_=execute(db,'INSERT INTO fm_assets(legacy_canonical_file_id,category,width,height) VALUES(%s,%s,%s,%s) ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id),category=VALUES(category),width=VALUES(width),height=VALUES(height)',(f['id'],category,f['width'],f['height']))
 if f['canonical_file_id']:
  target,_=execute(db,'INSERT INTO fm_assets(legacy_canonical_file_id) VALUES(%s) ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id)',(f['canonical_file_id'],))
 else:target=None
 execute(db,'UPDATE fm_assets SET canonical_asset_id=%s WHERE id=%s AND NOT(canonical_asset_id <=> %s)',(target,aid,target))
 execute(db,'INSERT INTO fm_asset_locations(asset_id,legacy_file_id,collection_id,relative_path,path_key,byte_size,modified_utc_ticks,is_present,is_preferred) VALUES(%s,%s,%s,%s,%s,%s,%s,%s,%s) ON DUPLICATE KEY UPDATE asset_id=VALUES(asset_id),collection_id=VALUES(collection_id),relative_path=VALUES(relative_path),path_key=VALUES(path_key),byte_size=VALUES(byte_size),modified_utc_ticks=VALUES(modified_utc_ticks),is_present=VALUES(is_present),is_preferred=VALUES(is_preferred)',(aid,f['id'],f['collection_id'],f['relative_path'],f['path_key'],f['byte_size'],f['modified_utc_ticks'],f['is_present'],not bool(target)))
 event=None
 if not previous_location or previous_location[0]['asset_id']!=aid:event='asset_registered'
 else:
  old=previous_location[0]
  if bool(old['is_present'])!=bool(f['is_present']):event='asset_restored' if f['is_present'] else 'asset_missing'
  elif old['byte_size']!=f['byte_size'] or old['modified_utc_ticks']!=f['modified_utc_ticks'] or old['category']!=category or old['width']!=f['width'] or old['height']!=f['height']:event='asset_updated'
 if event:
  execute(db,'UPDATE fm_assets SET revision=revision+1 WHERE id=%s',(aid,))
  execute(db,'INSERT INTO fm_change_events(event_kind,asset_id,asset_revision,location_id) SELECT %s,a.id,a.revision,l.id FROM fm_assets a JOIN fm_asset_locations l ON l.asset_id=a.id AND l.legacy_file_id=%s WHERE a.id=%s',(event,f['id'],aid))
 if not f['is_present'] or target:return
 auto_bind_wallpaper(db,f)
 # Missing file placeholders retain their IDs and are filled by reconciliation.
 hashes=rows(db,"SELECT id,hash_kind,hash_value,parameters FROM file_hashes WHERE file_id=%s AND source_byte_size=%s AND source_modified_utc_ticks=%s AND algorithm_version='1'",(f['id'],f['byte_size'],f['modified_utc_ticks']))
 bykind={h['hash_kind']:h for h in hashes}
 for h in hashes:
  params=decode(h['parameters']) or {}
  keys=['color','orientation','animation_timing','comparison','display_width','display_height','compared_automatically','decode_error','decoder','frames']
  values=[params.get(k) for k in keys]
  execute(db,'INSERT INTO fm_hash_settings(legacy_hash_id,'+','.join(keys)+') VALUES('+','.join(['%s']*(len(keys)+1))+') ON DUPLICATE KEY UPDATE '+','.join(k+'=VALUES('+k+')' for k in keys),[h['id'],*values])
 if 'pixel_sha256' in bykind:
  meta=decode(f.get('embedded_metadata')) or {};icc=meta.get('icc_profile_sha256');icc=bytes.fromhex(icc) if isinstance(icc,str) and re.fullmatch('[a-fA-F0-9]{64}',icc) else None
  animation=hashlib.sha256(json.dumps(meta.get('animation_durations_ms',[]),ensure_ascii=False,separators=(',',':')).encode()).digest();loop=str(meta.get('animation_loop',0))[:64]
  execute(db,'UPDATE files SET icc_profile_sha256=%s,color_signature=%s,animation_signature=%s,animation_loop=%s,fm_metadata_ticks=%s WHERE id=%s',(icc,color_signature(meta),animation,loop,f['modified_utc_ticks'],f['id']))
 if 'perceptual' in bykind:
  for band,value in visual_bands(bytes(bykind['perceptual']['hash_value'])):execute(db,'INSERT INTO fm_visual_bands VALUES(%s,%s,%s) ON DUPLICATE KEY UPDATE band_value=VALUES(band_value)',(f['id'],band,value))
 if len(bykind)<(1 if f['relative_path'].lower().endswith('.swf') else 3):
  key=hashlib.sha256(f"hash|{f['id']}|{f['byte_size']}|{f['modified_utc_ticks']}|v1".encode()).digest()
  execute(db,"INSERT IGNORE INTO fm_work_items(task_kind,file_id,expected_size,expected_ticks,deduplication_key,priority) VALUES('hash_file',%s,%s,%s,%s,5)",(f['id'],f['byte_size'],f['modified_utc_ticks'],key))
 else:
  key=hashlib.sha256(f"match|{f['id']}|{f['byte_size']}|{f['modified_utc_ticks']}|v1".encode()).digest()
  execute(db,"INSERT IGNORE INTO fm_work_items(task_kind,file_id,expected_size,expected_ticks,deduplication_key) VALUES('match_file',%s,%s,%s,%s)",(f['id'],f['byte_size'],f['modified_utc_ticks'],key))
 aliases_for_file(db,f)

def visual_bands(value):
 bits=int.from_bytes(value,'big');shift=0
 for band,width in enumerate((11,11,11,11,10,10)):
  yield band,(bits>>shift)&((1<<width)-1);shift+=width

def duplicate_label(row):
 raw=decode(row['raw_record'])
 value=raw.get('duplicate_source_category',row['category'])
 return value.lower() if isinstance(value,str) and value.lower() in ('nsfw','sketchy','sfw') else 'not_provided'

def sync_duplicate_category_group(db,primary_id):
 group=rows(db,'SELECT id,category,raw_record,canonical_entry_id,revision FROM inventory_entries WHERE deleted_at IS NULL AND (id=%s OR canonical_entry_id=%s) ORDER BY id FOR UPDATE',(primary_id,primary_id))
 primary=next((e for e in group if e['id']==primary_id and e['canonical_entry_id'] is None),None)
 if not primary or len(group)<2:return 0
 chosen=next(label for label in ('nsfw','sketchy','sfw','not_provided') if any(duplicate_label(e)==label for e in group))
 if primary['category']==chosen:return 0
 raw=decode(primary['raw_record']).copy()
 if 'duplicate_source_category' not in raw:raw['duplicate_source_category']=duplicate_label(primary)
 raw['category']=chosen
 serialized=json.dumps(raw,sort_keys=True,ensure_ascii=False,separators=(',',':'))
 _,changed=execute(db,'UPDATE inventory_entries SET category=%s,raw_record=%s,record_sha256=%s WHERE id=%s AND revision=%s',(chosen,serialized,hashlib.sha256(serialized.encode()).digest(),primary_id,primary['revision']))
 if changed!=1:raise RuntimeError('Duplicate category changed concurrently; retry this group.')
 execute(db,"INSERT INTO inventory_entry_history(entry_id,revision,action,previous_record,new_record,actor) SELECT id,revision,'duplicate_category_inherited',%s,%s,'File manager' FROM inventory_entries WHERE id=%s",(primary['raw_record'],serialized,primary_id))
 return 1

def sync_duplicate_categories_batch(db,size=200):
 key='duplicate_category_backfill_v1'
 saved=rows(db,'SELECT value FROM gallery_state WHERE state_key=%s',(key,))
 state=decode(saved[0]['value']) if saved else {'after':0}
 if state.get('complete'):return 0
 batch=rows(db,'SELECT id,canonical_entry_id FROM inventory_entries WHERE id>%s AND canonical_entry_id IS NOT NULL AND deleted_at IS NULL ORDER BY id LIMIT %s',(state['after'],size))
 if not batch:
  execute(db,'INSERT INTO gallery_state(state_key,value) VALUES(%s,%s) ON DUPLICATE KEY UPDATE value=VALUES(value)',(key,json.dumps({'after':state['after'],'complete':True})))
  return 0
 db.begin()
 try:
  changed=sum(sync_duplicate_category_group(db,primary_id) for primary_id in sorted({r['canonical_entry_id'] for r in batch}))
  execute(db,'INSERT INTO gallery_state(state_key,value) VALUES(%s,%s) ON DUPLICATE KEY UPDATE value=VALUES(value)',(key,json.dumps({'after':batch[-1]['id']})))
  db.commit()
 except Exception:db.rollback();raise
 return changed

def aliases_for_file(db,f):
 entries=rows(db,"SELECT DISTINCT e.id,e.site,e.source_id,e.record_type,e.metadata_score,e.is_hidden,e.canonical_entry_id,e.status,e.collection_id,EXISTS(SELECT 1 FROM fm_collection_moves mv WHERE mv.entry_id=e.id AND mv.state='completed') manual_placement FROM inventory_entries e WHERE e.deleted_at IS NULL AND e.file_id=%s AND e.record_type='image'",(f['id'],))
 if not entries:return
 primary=max(entries,key=lambda e:(e['manual_placement'],e['metadata_score'],e['collection_id']==f['collection_id'],e['record_type']=='image',-e['id']))
 for e in entries:
  hidden=e['id']!=primary['id'] and not e['manual_placement'];canonical=primary['id'] if hidden else None;status='duplicate' if hidden else 'saved'
  if bool(e['is_hidden'])==hidden and e['canonical_entry_id']==canonical and e['status']==status:continue
  execute(db,'UPDATE inventory_entries SET is_hidden=%s,canonical_entry_id=%s,status=%s WHERE id=%s',(hidden,canonical,status,e['id']))
  if hidden:execute(db,"INSERT IGNORE INTO inventory_entry_relations(entry_id,related_entry_id,relation_kind) VALUES(%s,%s,'duplicate_of')",(e['id'],primary['id']))
  execute(db,"INSERT INTO inventory_entry_history(entry_id,revision,action,details,actor) SELECT id,revision,'canonical_entry_selected',JSON_OBJECT('canonical_entry_id',%s,'file_id',%s),'File manager' FROM inventory_entries WHERE id=%s",(primary['id'],f['id'],e['id']))
 sync_duplicate_category_group(db,primary['id'])

def auto_bind_wallpaper(db,f):
 import DownloaderLocations as D
 D.bind(db,f['id'],f['relative_path'])
 if rows(db,"SELECT 1 FROM fm_adapter_config WHERE integration_key='wallpaper-download' AND adapter_kind='wallpaper-current' AND enabled=TRUE"):return
 cfg=rows(db,"SELECT c.*,i.id integration_id FROM fm_adapter_config c JOIN fm_integrations i ON i.integration_key=c.integration_key WHERE c.integration_key='wallpaper-download' AND c.enabled=TRUE")
 if not cfg:return
 config=cfg[0];schema=config['database_name']
 if not f['relative_path'].casefold().startswith('wallpaper-download/') and not rows(db,'SELECT file_id FROM fm_external_bindings WHERE integration_id=%s AND file_id=%s LIMIT 1',(config['integration_id'],f['id'])):return
 if adapter_fingerprint(db,schema,'wallpaper-files')!=bytes(config['schema_sha256'] or b''):return
 from FileManagerWorker import external_path
 remote=external_path(config,f['relative_path']);key=hashlib.sha256(remote.replace('\\','/').rstrip('/').casefold().encode()).digest()
 files=rows(db,f'SELECT id,current_path FROM `{schema}`.files WHERE path_hash=%s',(key,))
 if not files:return
 external=files[0]
 execute(db,"INSERT INTO fm_external_bindings(integration_id,external_id,file_id,last_known_path,adapter_kind) VALUES(%s,%s,%s,%s,'wallpaper-files') ON DUPLICATE KEY UPDATE file_id=VALUES(file_id),last_known_path=VALUES(last_known_path)",(config['integration_id'],str(external['id']),f['id'],external['current_path']))
 posts=rows(db,f'SELECT p.*,pr.code provider FROM `{schema}`.posts p JOIN `{schema}`.providers pr ON pr.id=p.provider_id WHERE p.file_id=%s',(external['id'],))
 for post in posts:
  mapped=rows(db,'SELECT * FROM fm_external_entries WHERE integration_id=%s AND external_post_id=%s',(config['integration_id'],post['id']))
  if mapped and mapped[0]['source_revision']==post['indexed_at'] and mapped[0]['file_id']==f['id']:continue
  if mapped and rows(db,'SELECT id FROM inventory_entries WHERE id=%s AND deleted_at IS NOT NULL',(mapped[0]['entry_id'],)):continue
  terms=[t['term'] for t in rows(db,f"SELECT t.term FROM `{schema}`.post_tags pt JOIN `{schema}`.tag_terms t ON t.id=pt.tag_id WHERE pt.post_id=%s AND pt.origin='source' ORDER BY t.id",(post['id'],))]
  owner=f['owner'];name=f['relative_path'] if owner=='.' else f['relative_path'][len(owner)+1:];category={'s':'sfw','q':'sketchy','e':'nsfw'}.get(post['rating_code'],'not_provided');site={'wallhaven':'Wallhaven','alphacoders':'AlphaCoders','reddit':'Reddit'}.get(post['provider'],post['provider'])
  record={'site':site,'id':post['remote_id'],'record_type':'image','status':'saved','category':category,'description':post['title'],'source':post['post_url'] or post['source_url'],'url':post['original_url'],'file':name,'file_collection':owner,'tags':terms,'import_provenance':{'integration':'wallpaper-download','post_id':post['id']}}
  if post['width'] and post['height']:record['resolution']=f"{post['width']}x{post['height']}"
  serialized=json.dumps(record,sort_keys=True,ensure_ascii=False,separators=(',',':'));digest=hashlib.sha256(serialized.encode()).digest()
  if mapped:
   eid=mapped[0]['entry_id'];execute(db,'UPDATE inventory_entries SET site=%s,source_id=%s,category=%s,description=%s,source_url=%s,image_url=%s,resolution=%s,raw_record=%s,record_sha256=%s WHERE id=%s',(site,post['remote_id'],category,post['title'],record['source'],post['original_url'],record.get('resolution'),serialized,digest,eid))
  else:
   doc=rows(db,'SELECT id FROM inventory_documents WHERE collection_id=%s FOR UPDATE',(f['collection_id'],))[0]['id'];ordinal=rows(db,'SELECT COALESCE(MAX(source_ordinal),-1)+1 n FROM inventory_entries WHERE inventory_document_id=%s',(doc,))[0]['n']
   eid,_=execute(db,"INSERT INTO inventory_entries(inventory_document_id,source_ordinal,site,source_id,record_type,status,category,description,source_url,image_url,resolution,raw_record,record_sha256) VALUES(%s,%s,%s,%s,'image','saved',%s,%s,%s,%s,%s,%s,%s)",(doc,ordinal,site,post['remote_id'],category,post['title'],record['source'],post['original_url'],record.get('resolution'),serialized,digest))
   sid,_=execute(db,"INSERT INTO fm_source_records(integration_id,source_identity_sha256,site,external_id,record_kind,source_url,image_url,title,description) VALUES(%s,%s,%s,%s,'image',%s,%s,%s,%s) ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id)",(config['integration_id'],hashlib.sha256(('post:'+str(post['id'])).encode()).digest(),site,str(post['id']),record['source'],post['original_url'],post['title'],post['title']))
   execute(db,'INSERT INTO fm_entry_bridge VALUES(%s,%s)',(eid,sid))
   execute(db,'UPDATE inventory_entries SET file_id=%s WHERE id=%s',(f['id'],eid))
  execute(db,'INSERT INTO fm_external_entries VALUES(%s,%s,%s,%s,%s) ON DUPLICATE KEY UPDATE entry_id=VALUES(entry_id),source_revision=VALUES(source_revision),file_id=VALUES(file_id)',(config['integration_id'],post['id'],eid,post['indexed_at'],f['id']))
  execute(db,"INSERT INTO inventory_entry_history(entry_id,revision,action,new_record,actor) SELECT id,revision,'external_source_updated',raw_record,'Wallpaper downloader adapter' FROM inventory_entries WHERE id=%s",(eid,))

def drain(db,batches=4,size=100,kind='both'):
 with asset_write_lock:return _drain_unlocked(db,batches,size,kind)

def _drain_unlocked(db,batches=4,size=100,kind='both'):
 """Generation-checked deletes cannot erase a concurrently queued update."""
 if kind not in ('both','entry','file'):raise ValueError('Unknown index kind')
 done=0
 for _ in range(batches if kind in ('both','entry') else 0):
  db.begin()
  try:
   dirty=rows(db,'SELECT entry_id,generation FROM fm_entry_dirty ORDER BY entry_id LIMIT %s FOR UPDATE SKIP LOCKED',(size,))
   if not dirty:
    db.rollback();break
   for item in dirty:
    source=rows(db,'SELECT * FROM inventory_entries WHERE id=%s FOR UPDATE',(item['entry_id'],))
    if source:index_entry(db,source[0])
    execute(db,'DELETE FROM fm_entry_dirty WHERE entry_id=%s AND generation=%s',(item['entry_id'],item['generation']))
    done+=1
   db.commit()
  except Exception:db.rollback();raise
 for _ in range(batches if kind in ('both','file') else 0):
  db.begin()
  try:
   dirty=rows(db,'SELECT file_id,generation FROM fm_file_dirty ORDER BY file_id LIMIT %s FOR UPDATE SKIP LOCKED',(size,))
   if not dirty:
    db.rollback();break
   for item in dirty:
    source=rows(db,'SELECT f.*,c.collection_key AS owner FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id WHERE f.id=%s FOR UPDATE',(item['file_id'],))
    if source:index_file(db,source[0])
    execute(db,'DELETE FROM fm_file_dirty WHERE file_id=%s AND generation=%s',(item['file_id'],item['generation']))
    done+=1
   db.commit()
  except Exception:db.rollback();raise
 return done

def discover_markers(db,keys):
 """Call only after a complete successful traversal of the accessible share."""
 db.begin()
 try:
  execute(db,'UPDATE fm_collection_discovery SET is_marked=FALSE')
  for key in keys:
   execute(db,'INSERT INTO fm_collection_discovery(collection_id,is_marked) SELECT id,TRUE FROM collections WHERE collection_key=%s ON DUPLICATE KEY UPDATE is_marked=TRUE',(key,))
  execute(db,'DELETE FROM fm_collection_ancestry')
  owners=rows(db,'SELECT c.id,c.collection_key FROM collections c JOIN fm_collection_discovery d ON d.collection_id=c.id WHERE d.is_marked=TRUE')
  lookup={r['collection_key'].casefold():r['id'] for r in owners};pending=[]
  for child in owners:
   key=child['collection_key'];parts=[] if key=='.' else key.split('/')
   ancestors=['.']+['/'.join(parts[:i]) for i in range(1,len(parts)+1)]
   for ancestor in ancestors:
    if ancestor.casefold() in lookup:pending.append((lookup[ancestor.casefold()],child['id'],len(parts)-(0 if ancestor=='.' else len(ancestor.split('/')))))
   if len(pending)>=1000:
    with db.cursor() as cursor:cursor.executemany('INSERT INTO fm_collection_ancestry VALUES(%s,%s,%s)',pending)
    pending=[]
  if pending:
   with db.cursor() as cursor:cursor.executemany('INSERT INTO fm_collection_ancestry VALUES(%s,%s,%s)',pending)
  db.commit()
 except Exception:db.rollback();raise

def supported_adapter(db,schema):
 if not re.fullmatch(r'[a-zA-Z0-9_-]+',schema):raise ValueError('Invalid database identifier')
 cols=rows(db,"SELECT COLUMN_NAME,COLUMN_TYPE,EXTRA FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=%s AND TABLE_NAME='downloads'",(schema,))
 columns={r['COLUMN_NAME']:r for r in cols}
 if not {'post_id','path','md5','completed','path_hash'}<=columns.keys():return False
 engines=rows(db,"SELECT ENGINE FROM information_schema.TABLES WHERE TABLE_SCHEMA=%s AND TABLE_NAME='downloads'",(schema,))
 return bool(engines and engines[0]['ENGINE']=='InnoDB' and 'GENERATED' in columns['path_hash']['EXTRA'])

def adapter_fingerprint(db,schema,kind):
 if kind in ('booru-locations','wallpaper-current'):
  import DownloaderLocations as D
  return D.fingerprint(db,schema,kind)
 if not re.fullmatch(r'[a-zA-Z0-9_-]+',schema):raise ValueError('Invalid database identifier')
 tables=['downloads'] if kind=='booru-downloads' else ['files','posts']
 metadata=rows(db,'SELECT TABLE_NAME,COLUMN_NAME,COLUMN_TYPE,EXTRA,IS_NULLABLE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=%s AND TABLE_NAME IN ('+','.join(['%s']*len(tables))+') ORDER BY TABLE_NAME,ORDINAL_POSITION',[schema,*tables])
 engines=rows(db,'SELECT TABLE_NAME,ENGINE FROM information_schema.TABLES WHERE TABLE_SCHEMA=%s AND TABLE_NAME IN ('+','.join(['%s']*len(tables))+') ORDER BY TABLE_NAME',[schema,*tables])
 if len(engines)!=len(tables) or any(e['ENGINE']!='InnoDB' for e in engines):raise RuntimeError('Expected InnoDB integration tables are unavailable')
 if kind=='booru-downloads' and not supported_adapter(db,schema):raise RuntimeError('Booru downloads schema is not compatible')
 if kind=='wallpaper-files':
  required={'files':{'id','current_path','path_hash','sha256','byte_size','state','managed_by','updated_at'},'posts':{'file_id'}}
  for table,names in required.items():
   if not names<={c['COLUMN_NAME'] for c in metadata if c['TABLE_NAME']==table}:raise RuntimeError('Wallpaper downloader schema is not compatible')
 return hashlib.sha256(json.dumps(metadata,sort_keys=True,separators=(',',':')).encode()).digest()

def sync_download_path(db,schema,post_id,old_path,new_path,new_md5=None):
 """Caller transaction covers both the catalog change and external database edit."""
 if not supported_adapter(db,schema):raise RuntimeError('External schema is not compatible: '+schema)
 if old_path==new_path:return 'already_applied'
 current=rows(db,f'SELECT path,md5,completed FROM `{schema}`.downloads WHERE post_id=%s AND path_hash=UNHEX(SHA2(%s,256)) FOR UPDATE',(post_id,old_path))
 if not current:
  exists=rows(db,f'SELECT path FROM `{schema}`.downloads WHERE post_id=%s AND path_hash=UNHEX(SHA2(%s,256))',(post_id,new_path))
  if exists:return 'already_applied'
  raise RuntimeError('External path changed; synchronization conflict')
 if current[0]['path']!=old_path:raise RuntimeError('External path hash collision')
 exists=rows(db,f'SELECT md5 FROM `{schema}`.downloads WHERE post_id=%s AND path_hash=UNHEX(SHA2(%s,256)) FOR UPDATE',(post_id,new_path))
 if exists:
  if exists[0]['md5']!=(new_md5 or current[0]['md5']):raise RuntimeError('Different content at external destination')
  execute(db,f'DELETE FROM `{schema}`.downloads WHERE post_id=%s AND path_hash=UNHEX(SHA2(%s,256))',(post_id,old_path))
 else:execute(db,f'UPDATE `{schema}`.downloads SET path=%s,md5=COALESCE(%s,md5) WHERE post_id=%s AND path_hash=UNHEX(SHA2(%s,256))',(new_path,new_md5,post_id,old_path))
 return 'updated'

def sync_wallpaper_path(db,schema,file_id,old_path,new_path,new_sha256=None,new_size=None):
 if not re.fullmatch(r'[a-zA-Z0-9_-]+',schema):raise ValueError('Invalid database identifier')
 old=rows(db,f'SELECT * FROM `{schema}`.files WHERE id=%s FOR UPDATE',(file_id,))
 if not old:raise RuntimeError('External file is missing')
 f=old[0]
 if f['current_path']==new_path:return 'already_applied'
 if f['current_path']!=old_path:raise RuntimeError('External file path changed; synchronization conflict')
 # File identity remains stable in the downloader, even when several file
 # identities now refer to the same canonical bytes owned by the gallery.
 location_key=hashlib.sha256(new_path.replace('\\','/').rstrip('/').casefold().encode()).digest()
 other=rows(db,f'SELECT id,sha256 FROM `{schema}`.files WHERE path_hash=%s FOR UPDATE',(location_key,))
 if other and other[0]['id']!=file_id:
  if other[0]['sha256'] and (new_sha256 or f['sha256']) and other[0]['sha256']!=(new_sha256 or f['sha256']):raise RuntimeError('External destination contains different file bytes')
  execute(db,f'UPDATE `{schema}`.posts SET file_id=%s WHERE file_id=%s',(other[0]['id'],file_id))
  execute(db,f'UPDATE `{schema}`.files SET current_path=NULL,path_hash=NULL,state=%s,managed_by=%s,updated_at=%s WHERE id=%s',('duplicate','gallery',time.time(),file_id))
  execute(db,f"UPDATE `{schema}`.files SET managed_by='gallery',state='available',updated_at=%s WHERE id=%s",(time.time(),other[0]['id']))
 else:
  execute(db,f"UPDATE `{schema}`.files SET current_path=%s,path_hash=%s,sha256=COALESCE(%s,sha256),byte_size=COALESCE(%s,byte_size),state='available',managed_by=%s,updated_at=%s WHERE id=%s",(new_path,location_key,new_sha256,new_size,'gallery',time.time(),file_id))
 return 'updated'

