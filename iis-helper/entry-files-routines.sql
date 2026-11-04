CREATE PROCEDURE fm_bind_entry_file(IN p_entry BIGINT UNSIGNED, IN p_file BIGINT UNSIGNED)
BEGIN
 DECLARE parent_kind VARCHAR(64);
 DECLARE child_id BIGINT UNSIGNED DEFAULT NULL;
 DECLARE candidates INT DEFAULT 0;
 DECLARE doc_id BIGINT UNSIGNED;
 DECLARE ordinal_value INT UNSIGNED;
 SELECT record_type,inventory_document_id INTO parent_kind,doc_id FROM inventory_entries WHERE id=p_entry;
 IF parent_kind IN ('post','album','post_or_album') THEN
  UPDATE inventory_entries SET file_id=NULL WHERE id=p_entry AND file_id IS NOT NULL;
  SELECT MIN(c.id),COUNT(*) INTO child_id,candidates FROM inventory_entry_relations r JOIN inventory_entries c ON c.id=r.related_entry_id WHERE r.entry_id=p_entry AND r.relation_kind='parent_of' AND c.file_id=p_file AND c.deleted_at IS NULL;
  IF candidates=0 THEN
   SELECT MIN(c.id),COUNT(*) INTO child_id,candidates FROM inventory_entries c JOIN inventory_entries p ON p.id=p_entry WHERE c.file_id=p_file AND c.record_type='image' AND c.collection_id=p.collection_id AND c.site=p.site AND c.deleted_at IS NULL;
   IF candidates<>1 THEN
    SELECT id INTO doc_id FROM inventory_documents WHERE id=doc_id FOR UPDATE;
    SELECT COALESCE(MAX(source_ordinal),-1)+1 INTO ordinal_value FROM inventory_entries WHERE inventory_document_id=doc_id;
    INSERT INTO inventory_entries(inventory_document_id,source_ordinal,collection_id,file_id,site,source_id,record_type,status,category,description,source_url,raw_record,record_sha256)
     SELECT doc_id,ordinal_value,collection_id,p_file,site,CONCAT('parent-',p_entry,'-file-',p_file),'image',IF((SELECT is_present FROM files WHERE id=p_file),'saved','inventory_only'),category,description,source_url,JSON_OBJECT('site',site,'id',CONCAT('parent-',p_entry,'-file-',p_file),'record_type','image','status',IF((SELECT is_present FROM files WHERE id=p_file),'saved','inventory_only'),'generated_from_parent_id',p_entry),UNHEX(SHA2(CONCAT('parent-',p_entry,'-file-',p_file),256)) FROM inventory_entries WHERE id=p_entry;
    SET child_id=LAST_INSERT_ID();
    UPDATE inventory_entries SET record_sha256=UNHEX(SHA2(CAST(raw_record AS CHAR),256)) WHERE id=child_id;
   END IF;
   INSERT IGNORE INTO inventory_entry_relations(entry_id,related_entry_id,relation_kind) VALUES(p_entry,child_id,'parent_of');
  END IF;
 ELSE
  IF EXISTS(SELECT 1 FROM inventory_entries WHERE id=p_entry AND file_id IS NOT NULL AND file_id<>p_file) THEN
   SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='An image entry cannot reference more than one file';
  END IF;
  UPDATE inventory_entries SET file_id=p_file WHERE id=p_entry AND NOT(file_id <=> p_file);
 END IF;
END
