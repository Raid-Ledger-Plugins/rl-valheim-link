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

test('snapshot keys: items and recipes of one game version, and containers of two worlds, do not collide', async () => withMock(async (base) => {
  const read = (f) => JSON.parse(readFileSync(join(golden, f), 'utf8'));
  const status = async (m) => (await (await post(base, JSON.stringify(m))).json()).status;
  const items = read('items-atlas.json'), recipes = read('recipes-vanilla.json'), containers = read('containers-basic.json');
  assert.equal(recipes.gameVersion, items.gameVersion); assert.equal(recipes.capturedAt, items.capturedAt);
  assert.equal(await status(items), 'accepted');
  assert.equal(await status(recipes), 'accepted', 'recipes must not share the items key');
  assert.equal(await status({ ...recipes, gameVersion: '1.0.20' }), 'accepted', 'another game version is another recipes snapshot');
  assert.equal(await status(recipes), 'stale', 'same recipes key and capturedAt');
  assert.equal(await status(containers), 'accepted');
  assert.equal(await status({ ...containers, worldUid: '42' }), 'accepted', 'another world is another containers snapshot');
  assert.equal(await status(containers), 'stale', 'same containers key and capturedAt');
}));

test("the mod's own fixtures are accepted under their serverId; file-icon blobs count as refs", async () => withMock(async (base) => {
  for (const f of readdirSync(own).filter((x) => x.endsWith('.json'))) {
    const r = await post(base, readFileSync(join(own, f)), {}, 'rlvl'); const t = await r.text(); assert.equal(r.status, 202, f + ' ' + t);
    if (f === 'items-file-icons.json') assert.equal(JSON.parse(t).missingBlobs.length, 1);
  }
}));

test('invalid fixtures: every one is a 400; Zod-only refinements carry their EXPECTED.json issue', async () => withMock(async (base) => {
  const expected = JSON.parse(readFileSync(join(golden, 'invalid', 'EXPECTED.json'), 'utf8'));
  for (const name of Object.keys(expected)) {
    const raw = readFileSync(join(golden, 'invalid', name + '.json'));
    let kind; try { kind = kindOf(raw); } catch { kind = 'character'; }
    const r = await post(base, raw, {}, TOKEN, ['character', 'map', 'items', 'containers', 'recipes'].includes(kind) ? kind : 'character');
    assert.equal(r.status, 400, `${name}: ${r.status}`);
    const body = await r.json();
    assert.ok(['schema_invalid', 'bitmap_invalid'].includes(body.code), `${name}: ${body.code}`);
    // ajv keywords are not Zod codes, so only the refinements (code custom) compare {path, code}.
    if (expected[name].code === 'custom') assert.ok(body.issues?.some((i) => i.path === expected[name].path && i.code === 'custom'), `${name}: ${JSON.stringify(body)}`);
  }
}));

// Rejected with exactly these {path, code} issues on its kind route.
async function rejects(base, msg, issues, label) {
  const r = await post(base, JSON.stringify(msg));
  const body = await r.json();
  assert.equal(r.status, 400, `${label}: ${JSON.stringify(body)}`);
  assert.equal(body.code, 'schema_invalid', label);
  assert.deepEqual(body.issues, issues, label);
}
const goldenOf = (f) => JSON.parse(readFileSync(join(golden, f), 'utf8'));

test('containers refinements no fixture covers: int64 range, unreadable usedSlots, 50 000 summed entries', async () => withMock(async (base) => {
  const ok = goldenOf('containers-basic.json');
  const edit = (fn) => { const m = structuredClone(ok); fn(m); return m; };
  await rejects(base, edit((m) => { m.containers[0].creatorPlayerId = '9223372036854775808'; }), [{ path: 'containers.0.creatorPlayerId', code: 'custom' }], 'creator above int64');
  await rejects(base, edit((m) => { m.worldUid = '-9223372036854775809'; }), [{ path: 'worldUid', code: 'custom' }], 'worldUid below int64');
  await rejects(base, edit((m) => { Object.assign(m.containers[0], { unreadable: true, items: [], usedSlots: 1 }); }), [{ path: 'containers.0.items', code: 'custom' }], 'unreadable with usedSlots');
  // 196 × 256 stacks = 50 176 entries, each container under its own 256 cap.
  const stacks = Array.from({ length: 256 }, (_, j) => ({ prefab: `Item${j}`, quality: 1, stack: 1 }));
  const big = edit((m) => { m.containers = Array.from({ length: 196 }, (_, i) => ({ ...ok.containers[0], x: i, slots: 256, usedSlots: 256, items: stacks })); });
  await rejects(base, big, [{ path: 'containers', code: 'too_big' }], 'entries over 50 000');
  const edge = edit((m) => { m.worldUid = '-9223372036854775808'; m.containers[0].creatorPlayerId = '9223372036854775807'; });
  const r = await post(base, JSON.stringify(edge)); assert.equal(r.status, 202, 'int64 bounds: ' + await r.text());
  // Same piece and position but another creator is another container.
  const twin = edit((m) => { m.containers.push({ ...m.containers[0], creatorPlayerId: '1000000000000002' }); });
  const r2 = await post(base, JSON.stringify(twin)); assert.equal(r2.status, 202, 'twin by creator: ' + await r2.text());
}));

test('recipes refinements no fixture covers: piece/processor uniqueness, repairStation, piece resources, station icons, 12 000 entries', async () => withMock(async (base) => {
  const ok = goldenOf('recipes-vanilla.json');
  const edit = (fn) => { const m = structuredClone(ok); fn(m); return m; };
  await rejects(base, edit((m) => { m.pieces.push(m.pieces[0]); }), [{ path: 'pieces.1.prefab', code: 'custom' }], 'duplicate piece');
  await rejects(base, edit((m) => { m.processors.push(m.processors[0]); }), [{ path: 'processors.1.prefab', code: 'custom' }], 'duplicate processor');
  await rejects(base, edit((m) => { m.recipes[1].repairStation = 'forge_nope'; }), [{ path: 'recipes.1.repairStation', code: 'custom' }], 'unknown repairStation');
  await rejects(base, edit((m) => { m.pieces[0].resources.push(m.pieces[0].resources[0]); }), [{ path: 'pieces.0.resources.1.prefab', code: 'custom' }], 'duplicate piece resource');
  const icon = { kind: 'atlas', x: 0, y: 0, w: 64, h: 64 };
  await rejects(base, edit((m) => { m.stations[1].icon = icon; }), [{ path: 'atlas', code: 'custom' }], 'station icon without atlas');
  const atlas = { sha256: 'a'.repeat(64), byteLength: 100, contentType: 'image/png', width: 64, height: 64 };
  await rejects(base, edit((m) => { m.atlas = atlas; m.stations[1].icon = { ...icon, x: 1 }; }), [{ path: 'stations.1.icon', code: 'custom' }], 'station icon outside atlas');
  // 750 recipes × 16 resources + 1 piece resource + 1 process input = 12 002 entries.
  const resources = Array.from({ length: 16 }, (_, j) => ({ ...ok.recipes[1].resources[0], prefab: `Res${j}` }));
  const big = edit((m) => { m.recipes = Array.from({ length: 750 }, (_, i) => ({ ...ok.recipes[1], id: `Recipe_${i}`, resources })); });
  await rejects(base, big, [{ path: '', code: 'too_big' }], 'entries over 12 000');
  big.recipes[0].resources = resources.slice(2); // 12 000 exactly is allowed
  const r = await post(base, JSON.stringify(big)); assert.equal(r.status, 202, await r.text());
  const fits = edit((m) => { m.capturedAt = '2026-10-17T20:16:00Z'; m.atlas = atlas; m.stations[1].icon = icon; });
  const r2 = await post(base, JSON.stringify(fits)); assert.equal(r2.status, 202, await r2.text());
}));

test('character, map and items refinements no fixture covers', async () => withMock(async (base) => {
  const edited = (f, fn) => { const m = goldenOf(f); fn(m); return m; };
  const ch = (fn) => edited('character-eaqs.json', fn);
  await rejects(base, ch((m) => { m.inventory.items.push(m.inventory.items[0]); }), [{ path: 'inventory.items.5', code: 'custom' }], 'duplicate inventory slot');
  await rejects(base, ch((m) => { m.skills.push(m.skills[0]); }), [{ path: 'skills.3', code: 'custom' }], 'duplicate skill');
  await rejects(base, ch((m) => { m.inventory.items[0].item.maxDurability = null; }), [{ path: 'inventory.items.0.item.maxDurability', code: 'custom' }], 'inventory durability half null');
  await rejects(base, ch((m) => { m.character.worldUid = '9223372036854775808'; }), [{ path: 'character.worldUid', code: 'custom' }], 'worldUid above int64');
  await rejects(base, ch((m) => { m.inventory.grid.extraRows[1].y = 6; }), [{ path: 'inventory.grid.extraRows.1', code: 'custom' }], 'extra row outside grid');
  await rejects(base, ch((m) => { m.inventory.grid.extraRows[1].y = 4; }), [{ path: 'inventory.grid.extraRows.1', code: 'custom' }], 'duplicate extra row');
  const r1 = await post(base, JSON.stringify(ch((m) => { m.character.steamId = '76561197960265728'; }))); assert.equal(r1.status, 202, 'steamId at the individual base: ' + await r1.text());

  const map = (fn) => edited('map-shared-layer.json', fn);
  await rejects(base, map((m) => { m.character.steamId = '76561197960265727'; }), [{ path: 'character.steamId', code: 'custom' }], 'map steamId below base');
  await rejects(base, map((m) => { m.character.playerId = '-9223372036854775809'; }), [{ path: 'character.playerId', code: 'custom' }], 'map playerId below int64');
  await rejects(base, map((m) => { m.explored.data = 'H4sI' + 'A'.repeat(25); }), [{ path: 'explored.data', code: 'custom' }], 'base64 length not a multiple of 4');
  await rejects(base, map((m) => { m.sharedExplored.width = 1024; }), [{ path: 'sharedExplored', code: 'custom' }], 'shared layer not textureSize square');

  const items = (fn) => edited('items-atlas.json', fn);
  await rejects(base, items((m) => { m.items.push(m.items[0]); }), [{ path: 'items.4.prefab', code: 'custom' }], 'duplicate item prefab');
  await rejects(base, items((m) => { m.items[0].icon.x = 961; }), [{ path: 'items.0.icon', code: 'custom' }], 'item icon outside atlas');
  const r2 = await post(base, JSON.stringify(items((m) => { m.items[0].icon.x = 960; }))); assert.equal(r2.status, 202, 'icon on the atlas edge: ' + await r2.text());
}));

test('token, serverId, kind route, caps, encodings, rate buckets', async () => withMock(async (base) => {
  const raw = readFileSync(join(golden, 'character-vanilla.json'), 'utf8');
  assert.equal((await post(base, raw, {}, 'nope')).status, 401);
  assert.equal((await post(base, raw, {}, 'other-token')).status, 403);
  assert.equal((await post(base, raw, {}, TOKEN, 'map')).status, 400);
  assert.equal((await (await post(base, raw, {}, TOKEN, 'map')).json()).code, 'kind_mismatch');
  assert.equal((await (await post(base, raw, {}, TOKEN, 'nope')).json()).code, 'unknown_kind');
  assert.equal((await (await post(base, bumped(raw, '2099-01-01T00:00:00Z'))).json()).code, 'captured_at_in_future');
  assert.equal((await post(base, raw, { 'content-type': 'text/plain' })).status, 415);
  assert.equal((await post(base, raw, { 'content-encoding': 'br' })).status, 415);
  assert.equal((await (await post(base, Buffer.from('not gzip'), { 'content-encoding': 'gzip' }, TOKEN, 'character')).json()).code, 'bad_gzip');
  assert.equal((await post(base, Buffer.alloc(2_200_000, 0x78), {}, TOKEN, 'character')).status, 413); // over the character cap
  assert.equal((await (await post(base, 'x'.repeat(1000), {}, TOKEN, 'character')).json()).code, 'bad_json');
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
  assert.equal((await (await put(base, '0'.repeat(64), png)).json()).code, 'hash_mismatch');
  assert.equal((await (await put(base, sha, Buffer.from('nope'))).json()).code, 'not_png');
  assert.equal((await put(base, sha, gzipSync(png), 'rlvl', { 'content-encoding': 'gzip' })).status, 415);
  assert.equal((await put(base, sha, png, 'rlvl', { 'content-type': 'image/jpeg' })).status, 415);
  // A message whose ref disagrees with the stored blob is 400 blob_ref_mismatch.
  const wrong = JSON.parse(items); wrong.capturedAt = '2026-10-18T13:00:00Z'; wrong.atlas.byteLength += 1;
  assert.equal((await (await post(base, JSON.stringify(wrong), {}, 'rlvl')).json()).code, 'blob_ref_mismatch');
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
  assert.equal(r.status, 413); assert.equal((await r.json()).code, 'quota_exceeded');
  assert.equal((await post(base, bumped(raw))).status, 202); // updating an existing key is always admitted
}, { maxSnapshots: 1 }));
