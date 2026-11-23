CREATE TABLE IF NOT EXISTS schema_meta (
  key TEXT NOT NULL PRIMARY KEY,
  value INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS trusted_device (
  device_id BLOB NOT NULL PRIMARY KEY,
  display_name TEXT NOT NULL,
  device_type INTEGER NOT NULL,
  public_key BLOB NOT NULL,
  auto_accept INTEGER NOT NULL DEFAULT 0,
  paired_at INTEGER NOT NULL,
  last_ip INTEGER,
  last_port INTEGER
);

CREATE TABLE IF NOT EXISTS text_message (
  seq INTEGER NOT NULL PRIMARY KEY,
  peer_id BLOB NOT NULL,
  message_id BLOB NOT NULL,
  direction INTEGER NOT NULL,
  peer_name TEXT NOT NULL,
  text_utf8 BLOB NOT NULL,
  sent_at INTEGER NOT NULL,
  recorded_at INTEGER NOT NULL,
  recorded_tz INTEGER NOT NULL,
  status INTEGER NOT NULL,
  UNIQUE (peer_id, message_id)
);
CREATE INDEX IF NOT EXISTS ix_text_message_order ON text_message (recorded_at, seq);

CREATE TABLE IF NOT EXISTS transfer_task (
  transfer_id BLOB NOT NULL PRIMARY KEY,
  peer_id BLOB NOT NULL,
  peer_name TEXT NOT NULL,
  direction INTEGER NOT NULL,
  state INTEGER NOT NULL,
  interrupt_reason INTEGER,
  interrupt_detail TEXT,
  receive_dir TEXT,
  recorded_at INTEGER NOT NULL,
  recorded_tz INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_transfer_task_order ON transfer_task (recorded_at);
CREATE INDEX IF NOT EXISTS ix_transfer_task_peer_state ON transfer_task (peer_id, state);

CREATE TABLE IF NOT EXISTS transfer_file (
  transfer_id BLOB NOT NULL,
  file_index INTEGER NOT NULL,
  seq INTEGER NOT NULL,
  original_name TEXT NOT NULL,
  size INTEGER NOT NULL,
  mime TEXT NOT NULL,
  mtime INTEGER,
  state INTEGER NOT NULL,
  persisted INTEGER NOT NULL DEFAULT 0,
  sha256 BLOB,
  source_ref TEXT,
  temp_ref TEXT,
  started_at TEXT,
  last_write_at INTEGER,
  final_name TEXT,
  full_path TEXT,
  stored_ref TEXT,
  PRIMARY KEY (transfer_id, file_index)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_transfer_file_seq ON transfer_file (seq);

CREATE TABLE IF NOT EXISTS pending_cancel (
  peer_id BLOB NOT NULL,
  transfer_id BLOB NOT NULL,
  created_at INTEGER NOT NULL,
  PRIMARY KEY (peer_id, transfer_id)
);
