-- QuotaSight Usage API :: schema v1 (first vertical slice)
--
-- Narrow by design: per-collector daily token totals plus the receipt ledger
-- that makes writes idempotent and ordered. There is intentionally no
-- source/model/provider breakdown, no retention job and no management tables
-- in this slice.
--
-- Invariants enforced in the write path itself (not only by pre-checks):
--   * only the collector's active generation may be written
--   * revisions are monotonic per (collector, generation)
--   * an existing daily total may never be silently reduced
--   * absence of a day never deletes a row

CREATE TABLE collectors (
  id                TEXT    NOT NULL PRIMARY KEY,
  owner_id          TEXT    NOT NULL,
  active_generation INTEGER NOT NULL,
  created_at        TEXT    NOT NULL,
  CHECK (active_generation > 0 AND active_generation <= 9007199254740991),
  CHECK (length(id) BETWEEN 1 AND 64),
  CHECK (length(owner_id) BETWEEN 1 AND 128)
) STRICT;

-- One row per accepted snapshot. body_digest allows the same revision with the
-- identical payload to be recognised as a harmless replay.
CREATE TABLE snapshot_receipts (
  collector_id TEXT    NOT NULL REFERENCES collectors (id) ON DELETE CASCADE,
  generation   INTEGER NOT NULL,
  revision     INTEGER NOT NULL,
  body_digest  TEXT    NOT NULL,
  received_at  TEXT    NOT NULL,
  PRIMARY KEY (collector_id, generation, revision),
  CHECK (revision > 0 AND revision <= 9007199254740991),
  CHECK (generation > 0 AND generation <= 9007199254740991),
  CHECK (length(body_digest) = 64)
) STRICT, WITHOUT ROWID;

-- Authoritative daily token totals, keyed by generation so a superseded
-- generation keeps its history but can never be written again.
CREATE TABLE usage_day (
  collector_id TEXT    NOT NULL REFERENCES collectors (id) ON DELETE CASCADE,
  generation   INTEGER NOT NULL,
  bucket_date  TEXT    NOT NULL,
  total_tokens INTEGER NOT NULL,
  updated_at   TEXT    NOT NULL,
  PRIMARY KEY (collector_id, generation, bucket_date),
  CHECK (bucket_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'),
  CHECK (total_tokens >= 0 AND total_tokens <= 9007199254740991),
  CHECK (generation > 0 AND generation <= 9007199254740991)
) STRICT, WITHOUT ROWID;

CREATE INDEX usage_day_by_bucket_date ON usage_day (bucket_date);

CREATE TRIGGER usage_day_requires_active_generation
BEFORE INSERT ON usage_day
WHEN NOT EXISTS (
  SELECT 1 FROM collectors c
  WHERE c.id = NEW.collector_id AND c.active_generation = NEW.generation
)
BEGIN
  SELECT RAISE(ABORT, 'collector_generation_inactive');
END;

CREATE TRIGGER usage_day_rejects_unverified_reduction
BEFORE UPDATE OF total_tokens ON usage_day
WHEN NEW.total_tokens < OLD.total_tokens
BEGIN
  SELECT RAISE(ABORT, 'usage_reduction_unverified');
END;

CREATE TRIGGER snapshot_receipts_requires_active_generation
BEFORE INSERT ON snapshot_receipts
WHEN NOT EXISTS (
  SELECT 1 FROM collectors c
  WHERE c.id = NEW.collector_id AND c.active_generation = NEW.generation
)
BEGIN
  SELECT RAISE(ABORT, 'collector_generation_inactive');
END;

CREATE TRIGGER snapshot_receipts_requires_monotonic_revision
BEFORE INSERT ON snapshot_receipts
WHEN EXISTS (
  SELECT 1
  FROM snapshot_receipts r
  WHERE r.collector_id = NEW.collector_id
    AND r.generation = NEW.generation
    AND r.revision > NEW.revision
)
BEGIN
  SELECT RAISE(ABORT, 'collector_revision_stale');
END;
