import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { gzipSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { resolve, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createMock } from './server.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const golden = resolve(here, '../../contract/v1/fixtures');
const own = resolve(here, '../../fixtures');
const TOKEN = 't-test', SERVER = 'srv_fixture_01';
const MSG = '/api/plugins/valheim/link/messages', BLOB = '/api/plugins/valheim/link/blobs';

async function withMock(fn, opts = {}) {
  const mock = createMock({ tokens: { [TOKEN]: SERVER, 'other-token': 'srv_other', rlvl: 'srv_fixture_rlvl' }, ...opts });
  await new Promise((r) => mock.server.listen(0, r));
  const base = `http://localhost:${mock.server.address().port}`;
  try { await fn(base, mock); } finally { mock.server.close(); }
}
const kindOf = (body) => JSON.parse(Buffer.isBuffer(body) ? body.toString('utf8') : body).kind;
const post = (base, body, headers = {}, token = TOKEN, kind) =>
  fetch(`${base}${MSG}/${kind ?? kindOf(body)}`, { method: 'POST', body, headers: { 'content-type': 'application/json', authorization: `Bearer ${token}`, ...headers } });
const put = (base, sha, body, token = 'rlvl', headers = {}) =>
  fetch(`${base}${BLOB}/${sha}`, { method: 'PUT', body, headers: { 'content-type': 'image/png', authorization: `Bearer ${token}`, ...headers } });
const bumped = (raw, at = '2026-10-17T20:16:00Z') => { const m = JSON.parse(raw); m.capturedAt = at; return JSON.stringify(m); };

test('every golden fixture is accepted on its kind route (plain and gzip); 202 carries status + missingBlobs', async () => {
  // One fresh mock per fixture: two golden items fixtures share a snapshot key and capturedAt.
  for (const f of readdirSync(golden).filter((x) => x.endsWith('.json'))) await withMock(async (base) => {
    const raw = readFileSync(join(golden, f));
    const r1 = await post(base, raw); const t1 = await r1.text(); assert.equal(r1.status, 202, f + ' ' + t1);
    const b1 = JSON.parse(t1); assert.equal(b1.status, 'accepted'); assert.ok(Array.isArray(b1.missingBlobs));
    const r2 = await post(base, gzipSync(bumped(raw)), { 'content-encoding': 'gzip' }, TOKEN, kindOf(raw)); assert.equal(r2.status, 202, f + ' gzip');
    const r3 = await post(base, raw); assert.equal((await r3.json()).status, 'stale', f + ' older re-post');
  });
});

test("the mod's own fixtures are accepted under their serverId; file-icon blobs count as refs", async () => withMock(async (base) => {
  for (const f of readdirSync(own).filter((x) => x.endsWith('.json'))) {
    const r = await post(base, readFileSync(join(own, f)), {}, 'rlvl'); const t = await r.text(); assert.equal(r.status, 202, f + ' ' + t);
    if (f === 'items-file-icons.json') assert.equal(JSON.parse(t).missingBlobs.length, 1);
  }
}));

test('invalid fixtures: 400 schema_invalid / bitmap_invalid, or deferred §6 refinements', async () => withMock(async (base) => {
  const expected = JSON.parse(readFileSync(join(golden, 'invalid', 'EXPECTED.json'), 'utf8'));
  for (const name of Object.keys(expected)) {
    const raw = readFileSync(join(golden, 'invalid', name + '.json'));
    let kind; try { kind = kindOf(raw); } catch { kind = 'character'; }
    const r = await post(base, raw, {}, TOKEN, ['character', 'map', 'items'].includes(kind) ? kind : 'character');
    if (expected[name].code === 'custom' && !name.startsWith('map-bitmap') && !name.startsWith('map-basemap')) continue; // Zod-only refinements: the C# validator covers them
    assert.equal(r.status, 400, `${name}: ${r.status}`);
    const body = await r.json();
    assert.ok(['schema_invalid', 'bitmap_invalid'].includes(body.error), `${name}: ${body.error}`);
  }
}));

test('token, serverId, kind route, caps, encodings, rate buckets', async () => withMock(async (base) => {
  const raw = readFileSync(join(golden, 'character-vanilla.json'), 'utf8');
  assert.equal((await post(base, raw, {}, 'nope')).status, 401);
  assert.equal((await post(base, raw, {}, 'other-token')).status, 403);
  assert.equal((await post(base, raw, {}, TOKEN, 'map')).status, 400);
  assert.equal((await (await post(base, raw, {}, TOKEN, 'map')).json()).error, 'kind_mismatch');
  assert.equal((await post(base, raw, {}, TOKEN, 'nope')).status, 404);
  assert.equal((await (await post(base, bumped(raw, '2099-01-01T00:00:00Z'))).json()).error, 'captured_at_in_future');
  assert.equal((await post(base, raw, { 'content-type': 'text/plain' })).status, 415);
  assert.equal((await post(base, raw, { 'content-encoding': 'br' })).status, 415);
  assert.equal((await (await post(base, Buffer.from('not gzip'), { 'content-encoding': 'gzip' }, TOKEN, 'character')).json()).error, 'bad_gzip');
  assert.equal((await post(base, Buffer.alloc(2_200_000, 0x78), {}, TOKEN, 'character')).status, 413); // over the character cap
  assert.equal((await (await post(base, 'x'.repeat(1000), {}, TOKEN, 'character')).json()).error, 'bad_json');
  for (let i = 0; i < 20; i++) await post(base, bumped(raw, `2026-10-17T20:${20 + i}:00Z`));
  const limited = await post(base, raw);
  assert.equal(limited.status, 429); assert.ok(Number(limited.headers.get('retry-after')) >= 1);
}, { messagesPerMinute: 25 }));

test('blob upload: sha256, IHDR, identity only, cross-check with a referencing message, own bucket', async () => withMock(async (base) => {
  const items = readFileSync(join(own, 'items-atlas.json'));
  assert.equal((await post(base, items, {}, 'rlvl')).status, 202);
  const png = readFileSync(join(own, 'items-atlas.png'));
  const sha = createHash('sha256').update(png).digest('hex');
  assert.equal((await put(base, sha, png)).status, 201);
  assert.equal((await put(base, sha, png)).status, 200);
  assert.equal((await (await put(base, '0'.repeat(64), png)).json()).error, 'sha256_mismatch');
  assert.equal((await (await put(base, sha, Buffer.from('nope'))).json()).error, 'not_png');
  assert.equal((await put(base, sha, gzipSync(png), 'rlvl', { 'content-encoding': 'gzip' })).status, 415);
  assert.equal((await put(base, sha, png, 'rlvl', { 'content-type': 'image/jpeg' })).status, 415);
  // A message whose ref disagrees with the stored blob is 400 blob_ref_mismatch.
  const wrong = JSON.parse(items); wrong.capturedAt = '2026-10-18T13:00:00Z'; wrong.atlas.byteLength += 1;
  assert.equal((await (await post(base, JSON.stringify(wrong), {}, 'rlvl')).json()).error, 'blob_ref_mismatch');
  // 6 authenticated PUTs so far (every one counts, like the real guard); the bucket is 10.
  for (let i = 0; i < 4; i++) assert.equal((await put(base, sha, png)).status, 200);
  assert.equal((await put(base, sha, png)).status, 429);
  const state = await (await fetch(base + '/state')).json();
  assert.equal(state.blobs[0].sha256, sha);
}, { blobsPerMinute: 10 }));

test('per-server quotas → 413 quota_exceeded', async () => withMock(async (base) => {
  const raw = readFileSync(join(golden, 'character-vanilla.json'), 'utf8');
  assert.equal((await post(base, raw)).status, 202);
  const other = JSON.parse(raw); other.character.playerId = '1000000000000002';
  const r = await post(base, JSON.stringify(other));
  assert.equal(r.status, 413); assert.equal((await r.json()).error, 'quota_exceeded');
  assert.equal((await post(base, bumped(raw))).status, 202); // updating an existing key is always admitted
}, { maxSnapshots: 1 }));
