/**
 * Bounded request-body reading.
 *
 * The size limit is a property of the bytes that actually arrive, not of the
 * string they decode to: `content-length` is caller-supplied and may be absent,
 * and a decoded string can be much larger or smaller than its wire form. So the
 * header is used only as an early rejection, the real ceiling is enforced while
 * the stream is consumed, and decoding happens afterwards on a body that is
 * already known to be within the bound.
 */
import { ApiError } from './errors';
import { MAX_BODY_BYTES } from './schema';

const CONTENT_LENGTH_PATTERN = /^[0-9]{1,16}$/;

function payloadTooLarge(): ApiError {
  return new ApiError(413, 'payload_too_large', `The snapshot body must not exceed ${MAX_BODY_BYTES} bytes.`, {
    reason: 'payload_too_large',
    limitBytes: MAX_BODY_BYTES,
  });
}

function decode(chunks: readonly Uint8Array[], total: number): string {
  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }
  try {
    // `fatal` keeps invalid UTF-8 from being silently replaced with U+FFFD.
    return new TextDecoder('utf-8', { fatal: true, ignoreBOM: false }).decode(bytes);
  } catch {
    throw new ApiError(400, 'invalid_json', 'The request body must be valid JSON.', {
      reason: 'invalid_encoding',
    });
  }
}

export async function readBoundedBodyText(request: Request, maxBytes: number = MAX_BODY_BYTES): Promise<string> {
  const declared = request.headers.get('content-length');
  if (declared !== null) {
    if (!CONTENT_LENGTH_PATTERN.test(declared)) {
      throw new ApiError(400, 'invalid_request', 'content-length must be a decimal byte count.', {
        field: 'content-length',
        reason: 'invalid_content_length',
      });
    }
    if (Number(declared) > maxBytes) throw new ApiError(413, 'payload_too_large', `The request body must not exceed ${maxBytes} bytes.`, { reason: 'payload_too_large', limitBytes: maxBytes });
  }

  const stream = request.body;
  if (stream === null) return '';

  const reader = stream.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    if (value === undefined) continue;
    total += value.byteLength;
    if (total > maxBytes) {
      // Stop consuming immediately: the remaining bytes are never buffered.
      await reader.cancel().catch(() => undefined);
      throw new ApiError(413, 'payload_too_large', `The request body must not exceed ${maxBytes} bytes.`, { reason: 'payload_too_large', limitBytes: maxBytes });
    }
    chunks.push(value);
  }
  reader.releaseLock();
  return decode(chunks, total);
}
