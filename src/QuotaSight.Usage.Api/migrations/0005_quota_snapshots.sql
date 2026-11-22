-- Independent latest-value quota snapshots; daily usage remains untouched.
CREATE TABLE quota_snapshot (
  collector_id TEXT NOT NULL REFERENCES collectors(id) ON DELETE CASCADE,
  owner_id TEXT NOT NULL,
  generation INTEGER NOT NULL,
  collected_at TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  PRIMARY KEY (collector_id, generation),
  CHECK (generation > 0 AND generation <= 9007199254740991),
  CHECK (length(payload_json) <= 4096)
) STRICT, WITHOUT ROWID;

CREATE TRIGGER quota_snapshot_requires_active_owner_generation
BEFORE INSERT ON quota_snapshot
WHEN NOT EXISTS (
  SELECT 1 FROM collectors c
  WHERE c.id = NEW.collector_id
    AND c.owner_id = NEW.owner_id
    AND c.active_generation = NEW.generation
)
BEGIN
  SELECT RAISE(ABORT, 'collector_generation_inactive');
END;

CREATE TRIGGER quota_snapshot_update_requires_active_owner_generation
BEFORE UPDATE ON quota_snapshot
WHEN NOT EXISTS (
  SELECT 1 FROM collectors c
  WHERE c.id = NEW.collector_id
    AND c.owner_id = NEW.owner_id
    AND c.active_generation = NEW.generation
)
BEGIN
  SELECT RAISE(ABORT, 'collector_generation_inactive');
END;
