-- Additive schema for the optional external media bridge.
-- Apply manually before deploying code that uses the external grant API.
CREATE TABLE IF NOT EXISTS external_device_grants (
 grant_id_hash BINARY(32) NOT NULL PRIMARY KEY,
 client_instance_id VARCHAR(128) NOT NULL,
 device_id VARCHAR(128) NOT NULL,
 oidc_issuer VARCHAR(512) NOT NULL,
 oidc_sub VARCHAR(255) NOT NULL,
 refresh_token_encrypted MEDIUMBLOB NOT NULL,
 state ENUM('active','revoked') NOT NULL DEFAULT 'active',
 expires_at DATETIME(6) NOT NULL,
 created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
 updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
 KEY owner_grants(oidc_issuer(191),oidc_sub(191),state),
 KEY device_grants(client_instance_id,device_id,state)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS external_device_grant_items (
 grant_id_hash BINARY(32) NOT NULL,
 file_id BIGINT UNSIGNED NOT NULL,
 PRIMARY KEY(grant_id_hash,file_id),
 KEY item_grants(file_id,grant_id_hash),
 CONSTRAINT fk_external_grant_items_grant FOREIGN KEY(grant_id_hash)
  REFERENCES external_device_grants(grant_id_hash) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
