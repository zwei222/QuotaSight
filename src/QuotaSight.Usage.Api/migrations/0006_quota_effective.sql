-- Last successful subscription-quota value, one row per (collector, generation,
-- provider).
--
-- 0005 stores only the newest collection's payload, so the next partial or empty
-- collection would replace what the previous one established. This table keeps
-- the newest value that was actually a successful observation, so a reading
-- client can still show what was last known. It is not a history: a newer
-- effective value replaces the row, and every row belongs to one provider.
--
-- Additive only. `quota_snapshot` stays the authority for what the collector
-- last sent, and a Worker that predates this file never touches this table, so
-- migration-first deployment is safe (the reverse order is not: a worker that
-- writes here needs the table).
--
-- What is enforced here, not only by the write path:
--   * only the collector's active generation, with its own owner, may be written
--     (triggers, mirroring 0005);
--   * a provider is one of the seven wire providers, and the windows payload is
--     bounded, so a direct insert cannot widen the contract (CHECK).
CREATE TABLE quota_effective (
  collector_id TEXT NOT NULL REFERENCES collectors(id) ON DELETE CASCADE,
  owner_id TEXT NOT NULL,
  generation INTEGER NOT NULL,
  provider TEXT NOT NULL,
  collected_at TEXT NOT NULL,
  observed_at TEXT,
  freshness TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  PRIMARY KEY (collector_id, generation, provider),
  CHECK (generation > 0 AND generation <= 9007199254740991),
  CHECK (provider IN ('claude', 'codex', 'opencode-go', 'antigravity-gemini',
                      'antigravity-claude-gpt', 'grok-build', 'copilot')),
  CHECK (freshness IN ('fresh', 'cached', 'unknown')),
  CHECK (length(payload_json) <= 4096)
) STRICT, WITHOUT ROWID;

CREATE TRIGGER quota_effective_requires_active_owner_generation
BEFORE INSERT ON quota_effective
WHEN NOT EXISTS (
  SELECT 1 FROM collectors c
  WHERE c.id = NEW.collector_id
    AND c.owner_id = NEW.owner_id
    AND c.active_generation = NEW.generation
)
BEGIN
  SELECT RAISE(ABORT, 'collector_generation_inactive');
END;

CREATE TRIGGER quota_effective_update_requires_active_owner_generation
BEFORE UPDATE ON quota_effective
WHEN NOT EXISTS (
  SELECT 1 FROM collectors c
  WHERE c.id = NEW.collector_id
    AND c.owner_id = NEW.owner_id
    AND c.active_generation = NEW.generation
)
BEGIN
  SELECT RAISE(ABORT, 'collector_generation_inactive');
END;

-- Backfill from the snapshots that already exist: only the collector's active
-- generation, and only a provider whose entry is a usable value. `available`
-- without one finite (or unlimited) window is not a value, an explicit
-- `unavailable` never is, and a `cached` value with no observation time cannot
-- be shown to be a success. An `unknown` value with no observation time *is* a
-- value: the real collector reports Codex, OpenCode Go, Grok and Copilot that
-- way, so it is restored with its collection time as the only time it has and
-- `observed_at` left NULL. An observation dated after the collection that
-- carried it, by more than the five minutes the write contract allows a
-- collection to lead the clock, is not a success either: restoring it would
-- outrank every later observation. The comparison is by instant
-- (`julianday()`, rounded to whole milliseconds so an exactly five-minute lead
-- stays inside the tolerance), not by text. These are the same rules the write
-- path applies, so a restored database and a freshly written one describe the
-- same state.
INSERT INTO quota_effective
  (collector_id, owner_id, generation, provider, collected_at, observed_at,
   freshness, payload_json)
SELECT q.collector_id,
       q.owner_id,
       q.generation,
       json_extract(p.value, '$.provider'),
       q.collected_at,
       json_extract(p.value, '$.observedAt'),
       json_extract(p.value, '$.freshness'),
       json_extract(p.value, '$.windows')
FROM quota_snapshot q
JOIN collectors c
  ON c.id = q.collector_id
 AND c.owner_id = q.owner_id
 AND c.active_generation = q.generation
JOIN json_each(json_extract(q.payload_json, '$.providers')) AS p
WHERE json_extract(p.value, '$.status') = 'available'
  AND json_extract(p.value, '$.provider') IN ('claude', 'codex', 'opencode-go',
                                               'antigravity-gemini',
                                               'antigravity-claude-gpt',
                                               'grok-build', 'copilot')
  AND json_extract(p.value, '$.freshness') IN ('fresh', 'cached', 'unknown')
  AND NOT (json_extract(p.value, '$.freshness') = 'cached'
           AND json_extract(p.value, '$.observedAt') IS NULL)
  AND NOT (
    json_extract(p.value, '$.observedAt') IS NOT NULL
    AND CAST(ROUND(
          (julianday(json_extract(p.value, '$.observedAt')) - julianday(q.collected_at))
          * 86400000.0) AS INTEGER) > 300000
  )
  AND EXISTS (
    SELECT 1
    FROM json_each(json_extract(p.value, '$.windows')) AS w
    WHERE typeof(json_extract(w.value, '$.remainingBasisPoints')) = 'integer'
       OR json_extract(w.value, '$.unlimited') = 1
  );
