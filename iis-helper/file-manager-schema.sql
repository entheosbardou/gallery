-- Candidate additive production schema. NOT a cutover script.
-- First validate in an isolated database. Existing files/entries remain intact.
CREATE TABLE fm_assets (
 id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
 legacy_canonical_file_id BIGINT UNSIGNED NULL UNIQUE,
 canonical_asset_id BIGINT UNSIGNED NULL,
 media_kind ENUM('image','animation','flash','other') NOT NULL DEFAULT 'image',
 category VARCHAR(128) NOT NULL DEFAULT 'not_provided',
 width INT UNSIGNED NULL,height INT UNSIGNED NULL,
 revision BIGINT UNSIGNED NOT NULL DEFAULT 1,
 deleted_at DATETIME(6) NULL,
 created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
 updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
 KEY browse(category,deleted_at,canonical_asset_id,id),
 FOREIGN KEY(canonical_asset_id) REFERENCES fm_assets(id)
) ENGINE=InnoDB;
CREATE TABLE fm_asset_locations (
 id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
 asset_id BIGINT UNSIGNED NOT NULL,
 legacy_file_id BIGINT UNSIGNED NULL UNIQUE,
 collection_id BIGINT UNSIGNED NULL,
 relative_path TEXT NOT NULL,path_key BINARY(32) NOT NULL UNIQUE,
 byte_size BIGINT UNSIGNED NOT NULL,
 modified_utc_ticks BIGINT NOT NULL,
 is_present BOOLEAN NOT NULL DEFAULT TRUE,
 is_preferred BOOLEAN NOT NULL DEFAULT FALSE,
 KEY asset_presence(asset_id,is_present,id),
 KEY collection_browse(collection_id,is_present,id),
 FOREIGN KEY(asset_id) REFERENCES fm_assets(id)
) ENGINE=InnoDB;
CREATE TABLE fm_integrations (
 id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
 integration_key VARCHAR(128) COLLATE utf8mb4_bin NOT NULL UNIQUE,
 base_url TEXT NULL,
 role ENUM('booru','downloader','manual') NOT NULL,
 enabled BOOLEAN NOT NULL DEFAULT TRUE
) ENGINE=InnoDB;
CREATE TABLE fm_source_records (
 id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
 integration_id BIGINT UNSIGNED NOT NULL,
 source_identity_sha256 BINARY(32) NOT NULL,
 site VARCHAR(128) NOT NULL,external_id VARCHAR(512) COLLATE utf8mb4_bin NOT NULL,
 variant VARCHAR(255) COLLATE utf8mb4_bin NOT NULL DEFAULT '',
 record_kind VARCHAR(64) NOT NULL,
 source_url TEXT NULL,image_url TEXT NULL,title TEXT NULL,description LONGTEXT NULL,
 source_revision VARCHAR(255) NULL,
 UNIQUE KEY identity_key(integration_id,source_identity_sha256),
 KEY external_lookup(integration_id,site,external_id),
 FOREIGN KEY(integration_id) REFERENCES fm_integrations(id)
) ENGINE=InnoDB;
CREATE TABLE fm_source_assets (
 source_record_id BIGINT UNSIGNED NOT NULL,
 position INT UNSIGNED NOT NULL,
 asset_id BIGINT UNSIGNED NOT NULL,
 PRIMARY KEY(source_record_id,position),KEY asset_sources(asset_id,source_record_id),
 FOREIGN KEY(source_record_id) REFERENCES fm_source_records(id),
 FOREIGN KEY(asset_id) REFERENCES fm_assets(id)
) ENGINE=InnoDB;
CREATE TABLE fm_entry_bridge (
 legacy_entry_id BIGINT UNSIGNED PRIMARY KEY,
 source_record_id BIGINT UNSIGNED NOT NULL,
 FOREIGN KEY(source_record_id) REFERENCES fm_source_records(id)
) ENGINE=InnoDB;
CREATE TABLE tags (
 id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
 term VARCHAR(512) COLLATE utf8mb4_bin NOT NULL UNIQUE
) ENGINE=InnoDB;
CREATE TABLE inventory_entry_tags (
 entry_id BIGINT UNSIGNED NOT NULL,tag_id BIGINT UNSIGNED NOT NULL,
 PRIMARY KEY(entry_id,tag_id),KEY tag_entries(tag_id,entry_id),
 FOREIGN KEY(tag_id) REFERENCES tags(id)
) ENGINE=InnoDB;
-- Add the entry_id FK to inventory_entries(id) when rehearsing against the full
-- clone. This standalone candidate schema intentionally has no legacy tables.
CREATE TABLE fm_source_tags (
 source_record_id BIGINT UNSIGNED NOT NULL,tag_id BIGINT UNSIGNED NOT NULL,
 position INT UNSIGNED NOT NULL,
 PRIMARY KEY(source_record_id,tag_id),KEY tag_sources(tag_id,source_record_id),
 FOREIGN KEY(source_record_id) REFERENCES fm_source_records(id),
 FOREIGN KEY(tag_id) REFERENCES tags(id)
) ENGINE=InnoDB;
CREATE TABLE fm_work_items (
 id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
 task_kind ENUM('index_file','hash_file','match_file','organize_file','reconcile_file') NOT NULL,
 file_id BIGINT UNSIGNED NOT NULL,
 expected_size BIGINT UNSIGNED NOT NULL,expected_ticks BIGINT NOT NULL,
 deduplication_key BINARY(32) NOT NULL UNIQUE,
 state ENUM('pending','leased','completed','failed','cancelled') NOT NULL DEFAULT 'pending',
 available_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
 lease_owner VARCHAR(128) NULL,lease_token BINARY(16) NULL,lease_expires_at DATETIME(6) NULL,
 attempts INT UNSIGNED NOT NULL DEFAULT 0,error LONGTEXT NULL,
 KEY claim_work(state,available_at,id),KEY expired_leases(state,lease_expires_at),
 KEY file_work(file_id,state,id)
) ENGINE=InnoDB;
CREATE TABLE fm_ingest_receipts (
 id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
 integration_id BIGINT UNSIGNED NOT NULL,idempotency_key BINARY(32) NOT NULL,
 request_sha256 BINARY(32) NOT NULL,
 source_record_id BIGINT UNSIGNED NULL,asset_id BIGINT UNSIGNED NULL,
 state ENUM('accepted','processing','completed','failed') NOT NULL,
 error LONGTEXT NULL,received_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
 UNIQUE KEY retry_identity(integration_id,idempotency_key),
 FOREIGN KEY(integration_id) REFERENCES fm_integrations(id),
 FOREIGN KEY(source_record_id) REFERENCES fm_source_records(id),
 FOREIGN KEY(asset_id) REFERENCES fm_assets(id)
) ENGINE=InnoDB;
CREATE TABLE fm_change_events (
 id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
 event_kind ENUM('asset_registered','asset_moved','asset_merged','asset_missing','asset_restored','asset_updated') NOT NULL,
 asset_id BIGINT UNSIGNED NOT NULL,asset_revision BIGINT UNSIGNED NOT NULL,
 replacement_asset_id BIGINT UNSIGNED NULL,location_id BIGINT UNSIGNED NULL,
 created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
 FOREIGN KEY(asset_id) REFERENCES fm_assets(id),
 FOREIGN KEY(replacement_asset_id) REFERENCES fm_assets(id),
 FOREIGN KEY(location_id) REFERENCES fm_asset_locations(id)
) ENGINE=InnoDB;
CREATE TABLE fm_event_consumers (
 integration_id BIGINT UNSIGNED PRIMARY KEY,last_event_id BIGINT UNSIGNED NOT NULL DEFAULT 0,
 updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
 FOREIGN KEY(integration_id) REFERENCES fm_integrations(id)
) ENGINE=InnoDB;
CREATE TABLE fm_intake_roots (
 id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
 integration_id BIGINT UNSIGNED NOT NULL,
 unc_path TEXT NOT NULL,
 enabled BOOLEAN NOT NULL DEFAULT TRUE,
 completion_mode ENUM('notify','stable_probe','both') NOT NULL DEFAULT 'both',
 FOREIGN KEY(integration_id) REFERENCES fm_integrations(id)
) ENGINE=InnoDB;
-- Collection identity continues to come from the existing collections table.
-- Closure rows cache ancestry; only discovered marker folders enter the tree.
CREATE TABLE fm_collection_ancestry (
 ancestor_collection_id BIGINT UNSIGNED NOT NULL,
 descendant_collection_id BIGINT UNSIGNED NOT NULL,depth INT UNSIGNED NOT NULL,
 PRIMARY KEY(ancestor_collection_id,descendant_collection_id),
 KEY descendants(descendant_collection_id,depth)
) ENGINE=InnoDB;
CREATE TABLE fm_entry_metadata (
 legacy_entry_id BIGINT UNSIGNED PRIMARY KEY,
 variant VARCHAR(255) NULL,image_title TEXT NULL,author TEXT NULL,uploader TEXT NULL,
 purity VARCHAR(128) NULL,source_category VARCHAR(128) NULL,
 reason TEXT NULL,error LONGTEXT NULL,manual_import BOOLEAN NULL,
 metadata_verified BOOLEAN NULL,visual_review BOOLEAN NULL,
 age_assessment TEXT NULL,exclusion_basis TEXT NULL,duplicate_detected_by VARCHAR(128) NULL,
 image_count BIGINT UNSIGNED NULL,saved_image_count BIGINT UNSIGNED NULL,
 last_retry_utc VARCHAR(64) NULL,
 KEY manual_entries(manual_import,legacy_entry_id)
) ENGINE=InnoDB;
CREATE TABLE fm_hash_settings (
 legacy_hash_id BIGINT UNSIGNED PRIMARY KEY,
 color VARCHAR(128) NULL,orientation VARCHAR(128) NULL,
 animation_timing VARCHAR(128) NULL,comparison VARCHAR(128) NULL,
 display_width INT UNSIGNED NULL,display_height INT UNSIGNED NULL,
 compared_automatically BOOLEAN NULL,decode_error TEXT NULL
) ENGINE=InnoDB;
-- Gallery-owned queue for authorized writes to the OTHER app databases.
-- Target adapters compare expected_old_path and use a receipt in their own DB.
CREATE TABLE fm_database_sync (
 id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
 integration_id BIGINT UNSIGNED NOT NULL,event_id BIGINT UNSIGNED NOT NULL,
 external_record_id VARCHAR(512) COLLATE utf8mb4_bin NOT NULL,
 operation ENUM('path_changed','duplicate_linked','file_missing','file_restored') NOT NULL,
 asset_id BIGINT UNSIGNED NOT NULL,canonical_asset_id BIGINT UNSIGNED NULL,
 expected_old_path TEXT NULL,new_path TEXT NULL,
 idempotency_key BINARY(32) NOT NULL,
 state ENUM('pending','leased','completed','conflict','failed') NOT NULL DEFAULT 'pending',
 attempts INT UNSIGNED NOT NULL DEFAULT 0,
 available_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
 lease_token BINARY(16) NULL,lease_expires_at DATETIME(6) NULL,
 error LONGTEXT NULL,completed_at DATETIME(6) NULL,
 UNIQUE KEY replay_guard(integration_id,idempotency_key),
 KEY pending_sync(state,available_at,id),KEY expired_sync(state,lease_expires_at),
 FOREIGN KEY(integration_id) REFERENCES fm_integrations(id),
 FOREIGN KEY(event_id) REFERENCES fm_change_events(id),
 FOREIGN KEY(asset_id) REFERENCES fm_assets(id),
 FOREIGN KEY(canonical_asset_id) REFERENCES fm_assets(id)
) ENGINE=InnoDB;
