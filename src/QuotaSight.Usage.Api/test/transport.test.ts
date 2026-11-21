/**
 * Transport-level bounds and error hygiene.
 *
 * The byte bound has to be enforced on the bytes the Worker actually receives,
 * not on the size of the decoded string: `content-length` is only a hint the
 * caller controls. The media type is matched exactly (parameters allowed), the
 * query is parsed without silently picking one of two duplicate values, and no
 * caller-supplied key is ever echoed back in an error body.
 */
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { createApp } from '../src/app';
import { fixtureAuthenticator } from './fixture-auth';
import { createHarness, json, resetData, scalar, snapshotBody, type Harness } from './harness';

let h: Harness;

beforeAll(async () => {
  h = await createHarness();
}, 60_000);

afterAll(async () => {
  await h.dispose();
});

beforeEach(async () => {
  await resetData(h.db);
});

const encoder = new TextEncoder();

async function expectNothingStored(): Promise<void> {
  expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(0);
  expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_day')).toBe(0);
}

/** A distinctive token that must never appear in a response body. */
const MARKER = 'probe_9f3a_marker';

describe('request body byte bound', () => {
  it('rejects a streamed body over the cap that carries no content-length', async () => {
    // A syntactically valid JSON object; only its size is unacceptable.
    const payload = JSON.stringify({ pad: 'a'.repeat(40_000) });
    const response = await h.putStream({ chunks: [encoder.encode(payload)] });

    expect(response.status).toBe(413);
    expect((await json(response)).error.code).toBe('payload_too_large');
    await expectNothingStored();
  });

  it('rejects an oversized body split into many small chunks', async () => {
    const payload = encoder.encode(JSON.stringify({ pad: 'b'.repeat(40_000) }));
    const chunks: Uint8Array[] = [];
    for (let offset = 0; offset < payload.byteLength; offset += 1_000) {
      chunks.push(payload.slice(offset, Math.min(offset + 1_000, payload.byteLength)));
    }
    const response = await h.putStream({ chunks });

    expect(response.status).toBe(413);
    expect((await json(response)).error.code).toBe('payload_too_large');
    await expectNothingStored();
  });

  it('bounds the bytes before decoding them', async () => {
    // Bytes that are not valid UTF-8 and not JSON either. A decoder-first
    // implementation reports invalid_json; the bound must win first.
    const invalid = new Uint8Array(40_000).fill(0xff);
    const response = await h.putStream({ chunks: [invalid] });

    expect(response.status).toBe(413);
    expect((await json(response)).error.code).toBe('payload_too_large');
    await expectNothingStored();
  });

  it('rejects invalid UTF-8 inside the cap as invalid JSON', async () => {
    const response = await h.putSnapshot({
      revision: 1,
      body: null,
      rawBytes: new Uint8Array([0x7b, 0xff, 0xfe, 0x7d]),
    });

    expect(response.status).toBe(400);
    const body = await json(response);
    expect(body.error.code).toBe('invalid_json');
    expect(body.error.details?.reason).toBe('invalid_encoding');
    await expectNothingStored();
  });

  it('accepts a streamed body under the cap', async () => {
    const payload = JSON.stringify(snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 12 }] }));
    const response = await h.putStream({ chunks: [encoder.encode(payload)] });

    expect(response.status).toBe(200);
    expect(await json(response)).toMatchObject({ status: 'applied' });
  });

  it('rejects a declared content-length above the cap without reading the body', async () => {
    // Dispatch through the same Hono app with a tiny body but an oversized
    // declared length. An implementation that reads first and measures the
    // decoded text would accept this request, so this pins the early check.
    const app = createApp({ authenticator: fixtureAuthenticator });
    const response = await app.request('https://usage.test/v1/collectors/col-a1/snapshots/1', {
      method: 'PUT',
      headers: {
        authorization: 'Bearer write-a1',
        'content-type': 'application/json',
        'content-length': '40000',
      },
      body: JSON.stringify(snapshotBody()),
    });

    expect(response.status).toBe(413);
    const body = await json(response);
    expect(body.error.code).toBe('payload_too_large');
  });

  it('rejects a malformed content-length instead of trusting it', async () => {
    const app = createApp({ authenticator: fixtureAuthenticator });
    const response = await app.request('https://usage.test/v1/collectors/col-a1/snapshots/1', {
      method: 'PUT',
      headers: {
        authorization: 'Bearer write-a1',
        'content-type': 'application/json',
        'content-length': '12, 12',
      },
      body: JSON.stringify(snapshotBody()),
    });

    expect(response.status).toBe(400);
    expect((await json(response)).error.code).toBe('invalid_request');
  });
});

describe('media type', () => {
  for (const contentType of [
    'application/jsonx',
    'application/json-patch+json',
    'application/ld+json',
    'application/x-json',
    'text/json',
    'application/json-',
    'application/',
  ]) {
    it(`rejects ${contentType}`, async () => {
      const response = await h.putSnapshot({ revision: 1, body: snapshotBody(), contentType });
      expect(response.status).toBe(415);
      expect((await json(response)).error.code).toBe('unsupported_media_type');
      await expectNothingStored();
    });
  }

  it('rejects a wrong media type before reading an oversized body', async () => {
    const oversized = encoder.encode(JSON.stringify({ pad: 'a'.repeat(40_000) }));
    const response = await h.putStream({ chunks: [oversized], contentType: 'text/plain' });

    expect(response.status).toBe(415);
    expect((await json(response)).error.code).toBe('unsupported_media_type');
    await expectNothingStored();
  });

  it('accepts application/json with parameters and case differences', async () => {
    const response = await h.putSnapshot({
      revision: 1,
      body: snapshotBody(),
      contentType: 'Application/JSON; charset=utf-8',
    });
    expect(response.status).toBe(200);
  });
});

describe('daily query parameter parsing', () => {
  it('rejects a duplicated from parameter', async () => {
    const response = await h.getDaily('?from=2026-11-01&from=2026-11-02&to=2026-11-30');
    expect(response.status).toBe(400);
    const body = await json(response);
    expect(body.error.code).toBe('invalid_query');
    expect(body.error.details?.reason).toBe('duplicate_parameter');
    expect(body.error.details?.field).toBe('from');
  });

  it('rejects a duplicated to parameter', async () => {
    const response = await h.getDaily('?from=2026-11-01&to=2026-11-02&to=2026-11-03');
    expect(response.status).toBe(400);
    expect((await json(response)).error.details?.reason).toBe('duplicate_parameter');
  });

  it('rejects a duplicated collector parameter', async () => {
    const response = await h.getDaily(
      '?from=2026-11-01&to=2026-11-30&collector=col-a1&collector=col-a2',
    );
    expect(response.status).toBe(400);
    expect((await json(response)).error.details?.reason).toBe('duplicate_parameter');
  });

  it('still accepts each parameter exactly once', async () => {
    const response = await h.getDaily('?from=2026-11-01&to=2026-11-30&collector=col-a1');
    expect(response.status).toBe(200);
  });
});

describe('error bodies never echo caller input', () => {
  it('does not echo an unknown snapshot field name', async () => {
    const response = await h.putSnapshot({
      revision: 1,
      body: snapshotBody({ [MARKER]: 'x' }),
    });
    expect(response.status).toBe(400);
    const text = await response.clone().text();
    expect(text).not.toContain(MARKER);
    const body = JSON.parse(text);
    expect(body.error.code).toBe('invalid_snapshot');
    expect(body.error.details?.reason).toBe('unexpected_field');
    expect(body.error.details?.field).toBe('$');
    await expectNothingStored();
  });

  it('does not echo an unknown field inside a day', async () => {
    const response = await h.putSnapshot({
      revision: 1,
      body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 1, [MARKER]: 1 }] }),
    });
    expect(response.status).toBe(400);
    const text = await response.clone().text();
    expect(text).not.toContain(MARKER);
    const body = JSON.parse(text);
    expect(body.error.details?.field).toBe('days[0]');
    await expectNothingStored();
  });

  it('does not echo an unknown field inside coveredRange', async () => {
    const response = await h.putSnapshot({
      revision: 1,
      body: snapshotBody({ coveredRange: { from: '2026-11-18', to: '2026-11-18', [MARKER]: 1 } }),
    });
    expect(response.status).toBe(400);
    const text = await response.clone().text();
    expect(text).not.toContain(MARKER);
    expect(JSON.parse(text).error.code).toBe('invalid_snapshot');
  });

  it('does not echo an unknown query parameter name', async () => {
    const response = await h.getDaily(`?from=2026-11-01&to=2026-11-30&${MARKER}=1`);
    expect(response.status).toBe(400);
    const text = await response.clone().text();
    expect(text).not.toContain(MARKER);
    const body = JSON.parse(text);
    expect(body.error.code).toBe('invalid_query');
    expect(body.error.details?.reason).toBe('unexpected_parameter');
  });
});
