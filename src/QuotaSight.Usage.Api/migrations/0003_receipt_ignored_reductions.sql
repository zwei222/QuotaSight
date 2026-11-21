-- QuotaSight Usage API :: an accepted daily total never goes backwards
--
-- A snapshot may lower a day that was already accepted: upstream totals are
-- re-scanned, so a shorter log window can produce a smaller number for the same
-- day. The write path now keeps the accepted total for such a day instead of
-- refusing the whole snapshot, and reports how many days it ignored in
-- `ignoredReductions` on the receipt.
--
-- A replay has to answer with the receipt of the first write, so the count is
-- part of the receipt row rather than being recomputed from `usage_day` (which
-- may have moved on for other days by then).
--
-- `usage_day_rejects_unverified_reduction` from 0001 is intentionally kept. The
-- upsert now stores `MAX(accepted, incoming)`, so the statement itself can never
-- lower a row and the trigger never fires on the API path, while it remains the
-- schema-level guard that a direct `UPDATE` cannot move a stored total
-- backwards either.

ALTER TABLE snapshot_receipts
  ADD COLUMN ignored_reductions INTEGER NOT NULL DEFAULT 0
  CHECK (ignored_reductions >= 0 AND ignored_reductions <= 62);
