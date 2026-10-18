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

async function withMock(fn, opts = {}) {
  const mock = createMock({ tokens: { [TOKEN]: SERVER, 'other-token': 'srv_other' }, ...opts });
  await new Promise((r) => mock.server.listen(0, r));
  const base = `http://localhost:${mock.server.address().port}`;
  try { await fn(base, mock); } finally { mock.server.close(); }
}
const post = (base, body, headers = {}, token = TOKEN) => fetch(base + '/api/valheim-link/messages', { method: 'POST', body, headers: { 'content-type': 'application/json', authorization: `Bearer ${token}`, ...headers } });

test('every golden fixture is accepted (plain and gzip)', async () => withMock(async (base) => {
  for (const f of readdirSync(golden).filter((x) => x.endsWith('.json'))) {
    const raw = readFileSync(join(golden, f));
    const r1 = await post(base, raw); assert.equal(r1.status, 202, f + ' ' + await r1.text());
    const bumped = JSON.parse(raw); bumped.capturedAt = '2026-10-17T20:16:00Z';
    const r2 = await post(base, gzipSync(JSON.stringify(bumped)), { 'content-encoding': 'gzip' }); assert.equal(r2.status, 202, f + ' gzip');
  }
}));

test("the mod's own fixtures are accepted under their serverId", async () => withMock(async (base) => {
  for (const f of readdirSync(own).filter((x) => x.endsWith('.json'))) {
    const r = await post(base, readFileSync(join(own, f)), {}, 'rlvl'); assert.equal(r.status, 202, f + ' ' + await r.text());
  }
}, { tokens: { rlvl: 'srv_fixture_rlvl' } }));

test('invalid fixtures are rejected (400 schema, or 422 for §6 bitmap rules the mock inflates)', async () => withMock(async (base) => {
  const expected = JSON.parse(readFileSync(join(golden, 'invalid', 'EXPECTED.json'), 'utf8'));
  for (const name of Object.keys(expected)) {
    const r = await post(base, readFileSync(join(golden, 'invalid', name + '.json')));
    if (expected[name].code === 'custom' && !name.startsWith('map-bitmap')) continue; // Zod-only refinements: not the mock's job (C# validator covers them)
    assert.ok(r.status === 400 || r.status === 422, `${name}: ${r.status}`);
  }
}));

test('token, serverId, caps, rate limit, latest-wins', async () => withMock(async (base) => {
  const msg = JSON.parse(readFileSync(join(golden, 'character-vanilla.json'), 'utf8'));
  assert.equal((await post(base, JSON.stringify(msg), {}, 'nope')).status, 401);
  assert.equal((await post(base, JSON.stringify(msg), {}, 'other-token')).status, 403);
  assert.equal((await post(base, JSON.stringify({ ...msg, capturedAt: '2099-01-01T00:00:00Z' }))).status, 400);
  const big = { ...msg, knownItems: Array.from({ length: 8192 }, (_, i) => 'A'.repeat(90) + i) };
  assert.equal((await post(base, JSON.stringify(big))).status, 202);
  const r1 = await post(base, JSON.stringify(msg)); assert.equal(r1.status, 202);
  const older = await post(base, JSON.stringify({ ...msg, capturedAt: '2026-10-17T20:14:00Z' }));
  assert.equal((await older.json()).result, 'ignored');
  const r2 = await post(base, Buffer.alloc(11_000_000, 0x78)); assert.equal(r2.status, 413); // over the largest cap
  const r3 = await post(base, 'x'.repeat(3_000_000)); assert.equal(r3.status, 400);           // under it but not JSON
  for (let i = 0; i < 40; i++) await post(base, JSON.stringify(msg));
  assert.equal((await post(base, JSON.stringify(msg))).status, 429);
}, { ratePerMinute: 30 }));

test('blob upload: sha256, IHDR, cross-check with a referencing message', async () => withMock(async (base) => {
  const items = readFileSync(join(own, 'items-atlas.json'));
  assert.equal((await post(base, items, {}, 'rlvl')).status, 202);
  const png = readFileSync(join(own, 'items-atlas.png'));
  const sha = createHash('sha256').update(png).digest('hex');
  const put = (p, body, ct = 'image/png') => fetch(`${base}/api/valheim-link/blobs/${p}`, { method: 'PUT', body, headers: { 'content-type': ct, authorization: 'Bearer rlvl' } });
  assert.equal((await put(sha, png)).status, 201);
  assert.equal((await put(sha, png)).status, 200);
  assert.equal((await put('0'.repeat(64), png)).status, 422);
  assert.equal((await put(sha, Buffer.from('nope'))).status, 422);
  const state = await (await fetch(base + '/state')).json();
  assert.equal(state.blobs[0].sha256, sha);
}, { tokens: { rlvl: 'srv_fixture_rlvl' } }));
