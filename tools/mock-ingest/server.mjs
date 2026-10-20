#!/usr/bin/env node
// Mock Raid Ledger ingest, shaped like the real ROK-1833 endpoint (merged 2026-10-18, PR #1671)
// and contract/v1/CONTRACT.md §1, §6, §7:
//   POST <MESSAGES_PATH>/<kind>      kind = character | map | items | containers | recipes; one JSON message; Bearer token per
//                                    server; serverId must match the token's; optional Content-Encoding: gzip
//                                    (the §7 cap applies to the DECODED body); body kind must match the route
//   PUT  <BLOBS_PATH>/<sha256>       raw PNG, Content-Type: image/png, identity encoding only;
//                                    sha256 + length checked, IHDR read before anything else
//   GET  /health, GET /state         inspection for tests and the operator
// Responses (error body `{ code, message }` exactly like the real one, verified on the acceptance env):
//   202 { status: accepted|stale, missingBlobs } · 201 stored / 200 exists (blobs)
//   400 bad_json | schema_invalid (issues as {path, code}) | kind_mismatch | captured_at_in_future |
//       bitmap_invalid | blob_ref_mismatch · 401 invalid_token · 403 server_mismatch · 404 · 413 payload_too_large |
//       quota_exceeded · 415 unsupported_media · 429 rate_limited (+ Retry-After; 60 messages/min, 20 blobs/min)
import { createServer } from 'node:http';
import { gunzipSync, inflateRawSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { readFileSync, mkdirSync, writeFileSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const here = dirname(fileURLToPath(import.meta.url));
const schema = JSON.parse(readFileSync(resolve(here, '../../contract/v1/schema.json'), 'utf8'));
const ajv = new Ajv2020({ allErrors: false, strict: false });
addFormats(ajv);
const validate = ajv.compile(schema);

// §7 constants (mirrors packages/contract/src/valheim-link-common.schema.ts)
export const CAPS = { character: 2_097_152, map: 8_388_608, items: 12_582_912, containers: 10_485_760, recipes: 12_582_912, binary: 16_777_216, bitmapEdgeMax: 4096 };
export const CAPTURED_AT_MAX_FUTURE_MS = 300_000;
const bitmapBytes = (w, h) => Math.ceil((w * h) / 8);
export const CONTAINER_ENTRIES_MAX = 50_000, RECIPE_ENTRIES_MAX = 12_000;
const INT64_MIN = -(2n ** 63n), INT64_MAX = 2n ** 63n - 1n;

// Zod-only refinements of the containers and recipes kinds (valheim-link-containers.schema.ts,
// valheim-link-recipes*.schema.ts at the pinned sha): schema.json cannot express them. Each
// returns the issues Zod would raise, as {path, code}; empty = valid.
const inInt64 = (v) => { const n = BigInt(v); return n >= INT64_MIN && n <= INT64_MAX; }; // schema.json already enforced the pattern
function uniquePrefabs(list, at, issues) {
  const seen = new Set();
  list.forEach((r, i) => { if (seen.has(r.prefab)) issues.push({ path: `${at}.${i}.prefab`, code: 'custom' }); seen.add(r.prefab); });
}

export function refineContainers(msg) {
  const issues = [];
  if (!inInt64(msg.worldUid)) issues.push({ path: 'worldUid', code: 'custom' });
  const seen = new Set();
  let entries = 0;
  msg.containers.forEach((c, i) => {
    const at = `containers.${i}`;
    if (!inInt64(c.creatorPlayerId)) issues.push({ path: `${at}.creatorPlayerId`, code: 'custom' });
    if (c.creatorPlayerId === '0') issues.push({ path: `${at}.creatorPlayerId`, code: 'custom' });
    if (c.usedSlots > c.slots) issues.push({ path: `${at}.usedSlots`, code: 'custom' });
    if (c.unreadable && (c.items.length > 0 || c.usedSlots !== 0)) issues.push({ path: `${at}.items`, code: 'custom' });
    const stacks = new Set();
    c.items.forEach((s, j) => { const k = `${s.prefab}@${s.quality}`; if (stacks.has(k)) issues.push({ path: `${at}.items.${j}`, code: 'custom' }); stacks.add(k); });
    const key = `${c.prefab}|${c.x}|${c.y}|${c.z}|${c.creatorPlayerId}`;
    if (seen.has(key)) issues.push({ path: at, code: 'custom' });
    seen.add(key);
    entries += c.items.length;
  });
  if (entries > CONTAINER_ENTRIES_MAX) issues.push({ path: 'containers', code: 'too_big' });
  return issues;
}

export function refineRecipes(msg) {
  const issues = [];
  msg.recipes.forEach((r, i) => uniquePrefabs(r.resources, `recipes.${i}.resources`, issues));
  msg.pieces.forEach((p, i) => uniquePrefabs(p.resources, `pieces.${i}.resources`, issues));
  msg.processes.forEach((p, i) => uniquePrefabs(p.inputs, `processes.${i}.inputs`, issues));
  uniquePrefabs(msg.stations, 'stations', issues);
  uniquePrefabs(msg.pieces, 'pieces', issues);
  uniquePrefabs(msg.processors, 'processors', issues);
  const ids = new Set();
  msg.recipes.forEach((r, i) => { if (ids.has(r.id)) issues.push({ path: `recipes.${i}.id`, code: 'custom' }); ids.add(r.id); });
  const stations = new Set(msg.stations.map((s) => s.prefab));
  msg.recipes.forEach((r, i) => {
    for (const field of ['station', 'repairStation']) if (r[field] !== null && !stations.has(r[field])) issues.push({ path: `recipes.${i}.${field}`, code: 'custom' });
  });
  msg.pieces.forEach((p, i) => { if (p.station !== null && !stations.has(p.station)) issues.push({ path: `pieces.${i}.station`, code: 'custom' }); });
  const processors = new Set(msg.processors.map((p) => p.prefab));
  msg.processes.forEach((p, i) => { if (!processors.has(p.processor)) issues.push({ path: `processes.${i}.processor`, code: 'custom' }); });
  for (const key of ['stations', 'pieces']) msg[key].forEach((item, i) => {
    const icon = item.icon;
    if (!icon) return;
    if (!msg.atlas) issues.push({ path: 'atlas', code: 'custom' });
    else if (icon.x + icon.w > msg.atlas.width || icon.y + icon.h > msg.atlas.height) issues.push({ path: `${key}.${i}.icon`, code: 'custom' });
  });
  const entries = msg.recipes.reduce((n, r) => n + r.resources.length, 0) + msg.pieces.reduce((n, p) => n + p.resources.length, 0) + msg.processes.reduce((n, p) => n + p.inputs.length, 0);
  if (entries > RECIPE_ENTRIES_MAX) issues.push({ path: '', code: 'too_big' });
  return issues;
}

export function createMock(opts = {}) {
  const tokens = opts.tokens ?? { [process.env.RLVL_MOCK_TOKEN ?? 'mock-token']: process.env.RLVL_MOCK_SERVER_ID ?? 'srv_mock_01' };
  const messagesPath = opts.messagesPath ?? process.env.RLVL_MOCK_MESSAGES_PATH ?? '/api/plugins/valheim/link/messages';
  const blobsPath = opts.blobsPath ?? process.env.RLVL_MOCK_BLOBS_PATH ?? '/api/plugins/valheim/link/blobs';
  // Real defaults: VALHEIM_LINK_RATE_MESSAGES_PER_MIN=60, VALHEIM_LINK_RATE_BLOBS_PER_MIN=20, per server.
  const rate = { messages: opts.messagesPerMinute ?? Number(process.env.RLVL_MOCK_RATE_MESSAGES ?? 60), blobs: opts.blobsPerMinute ?? Number(process.env.RLVL_MOCK_RATE_BLOBS ?? 20) };
  // Real defaults: 2000 snapshots / 256 MiB and 2000 blobs / 2 GiB per server (413 quota_exceeded).
  const quota = { snapshots: opts.maxSnapshots ?? 2000, snapshotBytes: opts.maxSnapshotBytes ?? 256 * 1024 ** 2, blobs: opts.maxBlobs ?? 2000, blobBytes: opts.maxBlobBytes ?? 2 * 1024 ** 3 };
  const dataDir = opts.dataDir ?? process.env.RLVL_MOCK_DATA ?? null; // null = memory only
  const now = opts.now ?? (() => Date.now());

  const state = { snapshots: new Map(), blobs: new Map(), rate: new Map(), log: [] };
  if (dataDir) mkdirSync(join(dataDir, 'blobs'), { recursive: true });

  const json = (res, code, body) => { res.writeHead(code, { 'content-type': 'application/json' }); res.end(JSON.stringify(body)); };
  const fail = (res, status, code, message, extra = {}) => json(res, status, { code, message, ...extra });
  const readBody = (req, cap) => new Promise((resolveBody, reject) => {
    const chunks = []; let size = 0;
    let over = false;
    req.on('data', (c) => { if (over) return; size += c.length; if (size > cap) { over = true; chunks.length = 0; reject(Object.assign(new Error(`body over ${cap} bytes`), { code: 413 })); return; } chunks.push(c); });
    req.on('end', () => resolveBody(Buffer.concat(chunks)));
    req.on('error', reject);
  });

  function auth(req, res) {
    const h = req.headers.authorization ?? '';
    const m = /^Bearer\s+(\S+)$/.exec(h);
    const serverId = m && tokens[m[1]];
    if (!serverId) { fail(res, 401, 'invalid_token', 'Invalid token'); return null; }
    return serverId;
  }

  function rateLimited(serverId, bucket, res) {
    const key = `${bucket}:${serverId}`;
    const t = now(); const win = state.rate.get(key)?.filter((x) => t - x < 60_000) ?? [];
    if (win.length >= rate[bucket]) {
      const retry = Math.max(1, Math.ceil((60_000 - (t - win[0])) / 1000));
      res.writeHead(429, { 'retry-after': String(retry), 'content-type': 'application/json' });
      res.end(JSON.stringify({ code: 'rate_limited', message: 'Too many requests for this server' })); return true;
    }
    win.push(t); state.rate.set(key, win); return false;
  }

  // CONTRACT.md §6 server-enforced bitmap rule, steps 1–4.
  function checkBitmap(bmp, name) {
    const buf = Buffer.from(bmp.data, 'base64');
    const max = bitmapBytes(bmp.width, bmp.height);
    if (buf.length < 18 || buf[0] !== 0x1f || buf[1] !== 0x8b || buf[2] !== 8) return `${name}: not gzip`;
    if (buf[3] !== 0) return `${name}: gzip FLG must be 0`;
    let out;
    try { out = gunzipSync(buf, { maxOutputLength: max }); } catch (e) { return `${name}: ${e.code ?? e.message}`; }
    if (out.length !== max) return `${name}: inflated ${out.length} bytes, expected ${max}`;
    try {
      const raw = inflateRawSync(buf.subarray(10, buf.length - 8), { maxOutputLength: max, info: true });
      if (raw.engine.bytesWritten !== buf.length - 18) return `${name}: more than one gzip member`;
    } catch (e) { return `${name}: raw inflate ${e.code ?? e.message}`; }
    return null;
  }

  function pngSize(buf) {
    if (buf.length < 33 || buf.readUInt32BE(0) !== 0x89504e47 || buf.readUInt32BE(4) !== 0x0d0a1a0a) return null;
    if (buf.toString('latin1', 12, 16) !== 'IHDR') return null;
    return { width: buf.readUInt32BE(16), height: buf.readUInt32BE(20) };
  }

  // Every binary ref a message carries (base map, atlas, file-icon blobs).
  function blobRefs(msg) {
    const refs = [msg.baseMap, msg.atlas].filter(Boolean);
    for (const it of msg.items ?? []) if (it.icon?.kind === 'file' && it.icon.blob) refs.push(it.icon.blob);
    return refs;
  }

  async function postMessage(req, res, serverId, routeKind) {
    const cap = CAPS[routeKind];
    if (!cap) return fail(res, 400, 'unknown_kind', 'no such kind route');
    const ct = (req.headers['content-type'] ?? '').split(';')[0].trim();
    if (ct !== 'application/json') return fail(res, 415, 'unsupported_media', 'Content-Type must be application/json');
    const enc = (req.headers['content-encoding'] ?? '').trim().toLowerCase();
    if (enc && enc !== 'gzip' && enc !== 'identity') return fail(res, 415, 'unsupported_media', 'Content-Encoding must be gzip or identity');
    const declared = Number(req.headers['content-length'] ?? 0);
    if (declared > cap) return fail(res, 413, 'payload_too_large', `declared ${declared} > ${cap}`);
    let body;
    try { body = await readBody(req, cap); } catch (e) { return fail(res, e.code === 413 ? 413 : 400, e.code === 413 ? 'payload_too_large' : 'bad_json', e.message); }
    if (enc === 'gzip') {
      try { body = gunzipSync(body, { maxOutputLength: cap }); } catch (e) { return e.code === 'ERR_BUFFER_TOO_LARGE' ? fail(res, 413, 'payload_too_large', `inflated body over ${cap}`) : fail(res, 400, 'bad_gzip', 'body is not valid gzip'); }
    }
    let msg;
    try { msg = JSON.parse(body.toString('utf8')); } catch { return fail(res, 400, 'bad_json', 'body is not valid JSON'); }
    if (!validate(msg)) {
      const issues = validate.errors.slice(0, 10).map((e) => ({ path: e.instancePath.replace(/^\//, '').replace(/\//g, '.'), code: e.keyword }));
      return fail(res, 400, 'schema_invalid', 'message does not match the contract', { issues });
    }
    const refined = msg.kind === 'containers' ? refineContainers(msg) : msg.kind === 'recipes' ? refineRecipes(msg) : [];
    if (refined.length) return fail(res, 400, 'schema_invalid', 'message does not match the contract', { issues: refined.slice(0, 10) });
    if (msg.kind !== routeKind) return fail(res, 400, 'kind_mismatch', 'body kind does not match the route');
    if (msg.serverId !== serverId) return fail(res, 403, 'server_mismatch', 'serverId does not match the token');
    const at = Date.parse(msg.capturedAt);
    if (!Number.isFinite(at)) return fail(res, 400, 'schema_invalid', 'capturedAt', { issues: [{ path: 'capturedAt', code: 'invalid_format' }] });
    if (at > now() + CAPTURED_AT_MAX_FUTURE_MS) return fail(res, 400, 'captured_at_in_future', 'capturedAt is ahead of the server');
    if (msg.kind === 'map') {
      // §6 refinements Zod enforces upstream but schema.json cannot: bitmaps and base map are textureSize square.
      const size = msg.grid.textureSize;
      for (const name of ['explored', 'sharedExplored', 'baseMap'])
        if (msg[name] && (msg[name].width !== size || msg[name].height !== size)) return fail(res, 400, 'schema_invalid', `${name} must be ${size}x${size}`, { issues: [{ path: name, code: 'custom' }] });
      for (const name of ['explored', 'sharedExplored']) {
        if (!msg[name]) continue;
        const problem = checkBitmap(msg[name], name);
        if (problem) return fail(res, 400, 'bitmap_invalid', problem);
      }
    }
    const refs = blobRefs(msg);
    for (const ref of refs) {
      const stored = state.blobs.get(ref.sha256);
      if (stored && (stored.byteLength !== ref.byteLength || stored.width !== ref.width || stored.height !== ref.height))
        return fail(res, 400, 'blob_ref_mismatch', 'ref differs from the stored blob');
    }
    const key = msg.kind === 'character' ? ['character', serverId, msg.character.steamId, msg.character.playerId]
      : msg.kind === 'map' ? ['map', serverId, msg.character.steamId, msg.character.playerId, msg.character.worldUid]
      : msg.kind === 'containers' ? ['containers', serverId, msg.worldUid]
      : [msg.kind, serverId, msg.gameVersion]; // items, recipes
    const k = key.join('|');
    const prev = state.snapshots.get(k);
    const missingBlobs = refs.map((r) => r.sha256).filter((h) => !state.blobs.has(h));
    if (prev && Date.parse(prev.capturedAt) >= at) { state.log.push({ kind: msg.kind, key: k, result: 'stale' }); return json(res, 202, { status: 'stale', missingBlobs }); }
    if (!prev) {
      const mine = [...state.snapshots.entries()].filter(([kk]) => kk.split('|')[1] === serverId);
      const bytes = mine.reduce((a, [, v]) => a + v.bytes, 0);
      if (mine.length + 1 > quota.snapshots || bytes + body.length > quota.snapshotBytes) return fail(res, 413, 'quota_exceeded', 'Snapshot storage quota for this server is full');
    }
    state.snapshots.set(k, { capturedAt: msg.capturedAt, bytes: body.length, refs: refs.map((r) => r.sha256), message: msg });
    if (dataDir) writeFileSync(join(dataDir, k.replace(/[^A-Za-z0-9_|.-]/g, '_') + '.json'), body);
    state.log.push({ kind: msg.kind, key: k, bytes: body.length, result: 'accepted' });
    return json(res, 202, { status: 'accepted', missingBlobs });
  }

  async function putBlob(req, res, serverId, sha) {
    if (!/^[0-9a-f]{64}$/.test(sha)) return fail(res, 400, 'hash_mismatch', 'route sha256 must be 64 lowercase hex');
    const ct = (req.headers['content-type'] ?? '').split(';')[0].trim();
    if (ct !== 'image/png') return fail(res, 415, 'unsupported_media', 'Content-Type must be image/png');
    const enc = (req.headers['content-encoding'] ?? '').trim().toLowerCase();
    if (enc && enc !== 'identity') return fail(res, 415, 'unsupported_media', 'blobs are sent uncompressed (identity)');
    const declared = Number(req.headers['content-length'] ?? 0);
    if (declared > CAPS.binary) return fail(res, 413, 'png_too_large', `declared ${declared} > ${CAPS.binary}`);
    const mine = [...state.blobs.values()].filter((b) => b.serverId === serverId);
    if (!state.blobs.has(sha) && (mine.length + 1 > quota.blobs || mine.reduce((a, b) => a + b.byteLength, 0) + declared > quota.blobBytes))
      return fail(res, 413, 'quota_exceeded', 'Blob storage quota for this server is full');
    let body;
    try { body = await readBody(req, CAPS.binary); } catch (e) { return fail(res, e.code === 413 ? 413 : 400, e.code === 413 ? 'png_too_large' : 'not_png', e.message); }
    if (body.length < 8 || body.readUInt32BE(0) !== 0x89504e47 || body.readUInt32BE(4) !== 0x0d0a1a0a) return fail(res, 400, 'not_png', 'body is not a PNG');
    const size = pngSize(body);
    if (!size) return fail(res, 400, 'bad_ihdr', 'PNG IHDR missing or malformed');
    const hash = createHash('sha256').update(body).digest('hex');
    if (hash !== sha) return fail(res, 400, 'hash_mismatch', 'body hash differs from the route sha256');
    // A referencing message may have arrived first: check its declared size/length (either order is fine).
    for (const snap of state.snapshots.values()) {
      for (const ref of blobRefs(snap.message)) {
        if (ref.sha256 !== sha) continue;
        if (ref.byteLength !== body.length || ref.width !== size.width || ref.height !== size.height)
          return fail(res, 400, 'blob_ref_mismatch', `blob ${body.length}B ${size.width}x${size.height} differs from the referencing message`);
      }
    }
    const existed = state.blobs.has(sha);
    state.blobs.set(sha, { byteLength: body.length, ...size, serverId });
    if (dataDir) writeFileSync(join(dataDir, 'blobs', sha + '.png'), body);
    state.log.push({ kind: 'blob', sha, bytes: body.length, result: existed ? 'exists' : 'stored' });
    return json(res, existed ? 200 : 201, { status: existed ? 'exists' : 'stored', sha256: sha }); // ValheimLinkBlobResultSchema is strict: exactly these two keys
  }

  const server = createServer(async (req, res) => {
    const url = new URL(req.url, 'http://localhost');
    if (req.method === 'GET' && url.pathname === '/health') return json(res, 200, { ok: true });
    if (req.method === 'GET' && url.pathname === '/state') return json(res, 200, { snapshots: [...state.snapshots].map(([k, v]) => ({ key: k, capturedAt: v.capturedAt, bytes: v.bytes, refs: v.refs })), blobs: [...state.blobs].map(([k, v]) => ({ sha256: k, ...v })), log: state.log.slice(-50) });
    const serverId = auth(req, res); if (!serverId) return;
    const kind = url.pathname.startsWith(messagesPath + '/') ? url.pathname.slice(messagesPath.length + 1) : null;
    if (req.method === 'POST' && kind !== null) { if (rateLimited(serverId, 'messages', res)) return; return postMessage(req, res, serverId, kind); }
    const m = url.pathname.startsWith(blobsPath + '/') ? url.pathname.slice(blobsPath.length + 1) : null;
    if (req.method === 'PUT' && m !== null) { if (rateLimited(serverId, 'blobs', res)) return; return putBlob(req, res, serverId, m); }
    json(res, 404, { statusCode: 404, message: `Cannot ${req.method} ${url.pathname}` }); // Nest's default 404, not an ingest error
  });
  return { server, state, messagesPath, blobsPath };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const port = Number(process.env.PORT ?? 8787);
  const { server, messagesPath, blobsPath } = createMock();
  server.listen(port, () => console.log(`mock-ingest listening on http://localhost:${port}  POST ${messagesPath}/{character|map|items|containers|recipes}  PUT ${blobsPath}/<sha256>  (token: $RLVL_MOCK_TOKEN, serverId: $RLVL_MOCK_SERVER_ID)`));
}
