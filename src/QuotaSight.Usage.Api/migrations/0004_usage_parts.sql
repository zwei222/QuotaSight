-- QuotaSight Usage API :: per-client/provider/model breakdown (wire v2)
--
-- Additive only. 0001-0003 stay exactly as they were applied, so an existing
-- local or remote database is upgraded in place: `usage_day` keeps the
-- authoritative daily total and `usage_part` adds the breakdown that wire v2
-- carries. A v1 write path never touches the new table, and a v2 write path
-- writes both, so v1 and v2 clients can talk to the same database.
--
-- Key choice: the breakdown row is keyed by (collector, generation, day,
-- client, provider, model). That makes "one row per key per day per generation"
-- a storage fact rather than an application convention, so a replayed or
-- overlapping write cannot produce two rows for the same key. The generation is
-- part of the key for the same reason as in `usage_day`: a superseded
-- generation keeps its history and can never be written again.
--
-- Invariants enforced here, not only by the write path:
--   * only the collector's active generation may be written (trigger)
--   * a stored component total may never be lowered (trigger)
--   * total_tokens is exactly the sum of the five components (CHECK)
--   * every component and the total is a non-negative safe integer (CHECK)
--   * identifiers are bounded to the wire contract's own limits (CHECK)
--
-- `total_tokens` is stored rather than computed so the monotonic-upsert
-- statement can compare one integer, and so the rows that add up to the day's
-- attribution can be summed without recomputing five columns per row.

CREATE TABLE usage_part (
  collector_id       TEXT    NOT NULL REFERENCES collectors (id) ON DELETE CASCADE,
  generation         INTEGER NOT NULL,
  bucket_date        TEXT    NOT NULL,
  client             TEXT    NOT NULL,
  provider           TEXT    NOT NULL,
  model              TEXT    NOT NULL,
  input_tokens       INTEGER NOT NULL,
  output_tokens      INTEGER NOT NULL,
  cache_read_tokens  INTEGER NOT NULL,
  cache_write_tokens INTEGER NOT NULL,
  reasoning_tokens   INTEGER NOT NULL,
  total_tokens       INTEGER NOT NULL,
  updated_at         TEXT    NOT NULL,
  PRIMARY KEY (collector_id, generation, bucket_date, client, provider, model),
  CHECK (bucket_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'),
  CHECK (generation > 0 AND generation <= 9007199254740991),
  -- Wire v2 bounds the normalized identifiers; an over-long value here would
  -- mean a client normalized something the API is not allowed to store.
  CHECK (length(client) <= 32),
  CHECK (length(provider) <= 64),
  CHECK (length(model) <= 96),
  CHECK (input_tokens >= 0 AND input_tokens <= 9007199254740991),
  CHECK (output_tokens >= 0 AND output_tokens <= 9007199254740991),
  CHECK (cache_read_tokens >= 0 AND cache_read_tokens <= 9007199254740991),
  CHECK (cache_write_tokens >= 0 AND cache_write_tokens <= 9007199254740991),
  CHECK (reasoning_tokens >= 0 AND reasoning_tokens <= 9007199254740991),
  CHECK (total_tokens >= 0 AND total_tokens <= 9007199254740991),
  CHECK (total_tokens = input_tokens + output_tokens + cache_read_tokens
                        + cache_write_tokens + reasoning_tokens)
) STRICT, WITHOUT ROWID;

CREATE TRIGGER usage_part_requires_active_generation
BEFORE INSERT ON usage_part
WHEN NOT EXISTS (
  SELECT 1 FROM collectors c
  WHERE c.id = NEW.collector_id AND c.active_generation = NEW.generation
)
BEGIN
  SELECT RAISE(ABORT, 'collector_generation_inactive');
END;

CREATE TRIGGER usage_part_rejects_unverified_reduction
BEFORE UPDATE OF total_tokens ON usage_part
WHEN NEW.total_tokens < OLD.total_tokens
BEGIN
  SELECT RAISE(ABORT, 'usage_part_reduction_unverified');
END;

-- The receipt has to explain a v2 write as completely as it explains a v1
-- write: `ignored_reductions` counts day totals that were kept at a higher
-- accepted value, and the new column counts the same thing per breakdown row.
-- The bound is the wire's own per-snapshot part limit (32 days x 32 parts).
ALTER TABLE snapshot_receipts
  ADD COLUMN ignored_part_reductions INTEGER NOT NULL DEFAULT 0
  CHECK (ignored_part_reductions >= 0 AND ignored_part_reductions <= 1024);
