#!/usr/bin/env node
// Mock Raid Ledger ingest (acceptance target until ROK-1833 ships). Implements what
// contract/v1/CONTRACT.md §1, §6 and §7 say the server must do:
//   POST <MESSAGES_PATH>            one JSON message; Bearer token per server; serverId must match;
//                                   optional Content-Encoding: gzip (caps apply to the decoded body)
//   PUT  <BLOBS_PATH>/<sha256>      raw PNG; sha256 + length checked, IHDR read before anything else
//   GET  /health, GET /state        inspection for tests and the operator
// Responses: 202 accepted · 400 bad JSON/schema · 401 bad token · 403 serverId mismatch ·
//            404 unknown route · 413 over cap · 415 wrong content type · 429 rate limited
//            422 bitmap/blob rule broken (inflate, size, IHDR, hash)
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
export const CAPS = { character: 2_097_152, map: 8_388_608, items: 10_485_760, binary: 16_777_216, bitmapEdgeMax: 4096 };
export const CAPTURED_AT_MAX_FUTURE_MS = 300_000;
const bitmapBytes = (w, h) => Math.ceil((w * h) / 8);

export function createMock(opts = {}) {
  const tokens = opts.tokens ?? { [process.env.RLVL_MOCK_TOKEN ?? 'mock-token']: process.env.RLVL_MOCK_SERVER_ID ?? 'srv_mock_01' };
  const messagesPath = opts.messagesPath ?? process.env.RLVL_MOCK_MESSAGES_PATH ?? '/api/valheim-link/messages';
  const blobsPath = opts.blobsPath ?? process.env.RLVL_MOCK_BLOBS_PATH ?? '/api/valheim-link/blobs';
  const ratePerMinute = opts.ratePerMinute ?? Number(process.env.RLVL_MOCK_RATE ?? 30);
  const dataDir = opts.dataDir ?? process.env.RLVL_MOCK_DATA ?? null; // null = memory only
  const now = opts.now ?? (() => Date.now());

  const state = { snapshots: new Map(), blobs: new Map(), rate: new Map(), log: [] };
  if (dataDir) mkdirSync(join(dataDir, 'blobs'), { recursive: true });

  const json = (res, code, body) => { res.writeHead(code, { 'content-type': 'application/json' }); res.end(JSON.stringify(body)); };
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
    if (!serverId) { json(res, 401, { error: 'bad token' }); return null; }
    return serverId;
  }

  function rateLimited(serverId, res) {
    const t = now(); const win = state.rate.get(serverId)?.filter((x) => t - x < 60_000) ?? [];
    if (win.length >= ratePerMinute) { res.writeHead(429, { 'retry-after': '60', 'content-type': 'application/json' }); res.end(JSON.stringify({ error: 'rate limited' })); return true; }
    win.push(t); state.rate.set(serverId, win); return false;
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

  async function postMessage(req, res, serverId) {
    const ct = (req.headers['content-type'] ?? '').split(';')[0].trim();
    if (ct !== 'application/json') return json(res, 415, { error: 'Content-Type must be application/json' });
    const enc = req.headers['content-encoding'];
    const hardCap = CAPS.items; // largest kind; the exact kind is only known after decoding
    let body;
    try { body = await readBody(req, hardCap); } catch (e) { return json(res, e.code === 413 ? 413 : 400, { error: e.message }); }
    if (enc === 'gzip') {
      try { body = gunzipSync(body, { maxOutputLength: hardCap }); } catch (e) { return json(res, e.code === 'ERR_BUFFER_TOO_LARGE' ? 413 : 400, { error: 'bad gzip body: ' + (e.code ?? e.message) }); }
    } else if (enc) return json(res, 415, { error: 'unsupported Content-Encoding' });
    let msg;
    try { msg = JSON.parse(body.toString('utf8')); } catch { return json(res, 400, { error: 'not JSON' }); }
    const kind = msg?.kind;
    const cap = CAPS[kind];
    if (!cap) return json(res, 400, { error: 'unknown kind' });
    if (body.length > cap) return json(res, 413, { error: `${kind} body ${body.length} > ${cap}` });
    if (!validate(msg)) { const e = validate.errors[0]; return json(res, 400, { error: 'schema', path: e.instancePath, message: e.message }); }
    if (msg.serverId !== serverId) return json(res, 403, { error: 'serverId does not match the token' });
    const at = Date.parse(msg.capturedAt);
    if (!Number.isFinite(at)) return json(res, 400, { error: 'capturedAt' });
    if (at > now() + CAPTURED_AT_MAX_FUTURE_MS) return json(res, 400, { error: 'capturedAt is more than 5 min in the future' });
    if (kind === 'map') {
      // §6 refinements Zod enforces upstream but schema.json cannot: bitmaps and base map are textureSize square.
      const size = msg.grid.textureSize;
      for (const name of ['explored', 'sharedExplored', 'baseMap'])
        if (msg[name] && (msg[name].width !== size || msg[name].height !== size)) return json(res, 400, { error: `${name} must be ${size}x${size}` });
      for (const name of ['explored', 'sharedExplored']) {
        if (!msg[name]) continue;
        const problem = checkBitmap(msg[name], name);
        if (problem) return json(res, 422, { error: problem });
      }
    }
    const key = kind === 'character' ? ['character', serverId, msg.character.steamId, msg.character.playerId]
      : kind === 'map' ? ['map', serverId, msg.character.steamId, msg.character.playerId, msg.character.worldUid]
      : ['items', serverId, msg.gameVersion];
    const k = key.join('|');
    const prev = state.snapshots.get(k);
    if (prev && Date.parse(prev.capturedAt) >= at) { state.log.push({ kind, key: k, result: 'ignored-older' }); return json(res, 202, { result: 'ignored', reason: 'older than the stored snapshot', key }); }
    const refs = [msg.baseMap, msg.atlas].filter(Boolean).map((r) => r.sha256);
    state.snapshots.set(k, { capturedAt: msg.capturedAt, bytes: body.length, refs, message: msg });
    if (dataDir) writeFileSync(join(dataDir, k.replace(/[^A-Za-z0-9_|.-]/g, '_') + '.json'), body);
    state.log.push({ kind, key: k, bytes: body.length, result: 'stored' });
    const missing = refs.filter((h) => !state.blobs.has(h));
    return json(res, 202, { result: 'stored', key, bytes: body.length, missingBlobs: missing });
  }

  async function putBlob(req, res, serverId, sha) {
    if (!/^[0-9a-f]{64}$/.test(sha)) return json(res, 400, { error: 'bad sha256' });
    const ct = (req.headers['content-type'] ?? '').split(';')[0].trim();
    if (ct !== 'image/png') return json(res, 415, { error: 'Content-Type must be image/png' });
    let body;
    try { body = await readBody(req, CAPS.binary); } catch (e) { return json(res, e.code === 413 ? 413 : 400, { error: e.message }); }
    const size = pngSize(body);
    if (!size) return json(res, 422, { error: 'not a PNG (IHDR)' });
    const hash = createHash('sha256').update(body).digest('hex');
    if (hash !== sha) return json(res, 422, { error: 'sha256 mismatch' });
    // A referencing message may have arrived first: check its declared size/length (either order is fine).
    for (const snap of state.snapshots.values()) {
      for (const ref of [snap.message.baseMap, snap.message.atlas]) {
        if (!ref || ref.sha256 !== sha) continue;
        if (ref.byteLength !== body.length) return json(res, 422, { error: `byteLength ${body.length} != referenced ${ref.byteLength}` });
        if (ref.width !== size.width || ref.height !== size.height) return json(res, 422, { error: `IHDR ${size.width}x${size.height} != referenced ${ref.width}x${ref.height}` });
      }
    }
    const existed = state.blobs.has(sha);
    state.blobs.set(sha, { byteLength: body.length, ...size, serverId });
    if (dataDir) writeFileSync(join(dataDir, 'blobs', sha + '.png'), body);
    state.log.push({ kind: 'blob', sha, bytes: body.length, result: existed ? 'exists' : 'stored' });
    return json(res, existed ? 200 : 201, { result: existed ? 'exists' : 'stored', sha256: sha, ...size, byteLength: body.length });
  }

  const server = createServer(async (req, res) => {
    const url = new URL(req.url, 'http://localhost');
    if (req.method === 'GET' && url.pathname === '/health') return json(res, 200, { ok: true });
    if (req.method === 'GET' && url.pathname === '/state') return json(res, 200, { snapshots: [...state.snapshots].map(([k, v]) => ({ key: k, capturedAt: v.capturedAt, bytes: v.bytes, refs: v.refs })), blobs: [...state.blobs].map(([k, v]) => ({ sha256: k, ...v })), log: state.log.slice(-50) });
    const serverId = auth(req, res); if (!serverId) return;
    if (rateLimited(serverId, res)) return;
    if (req.method === 'POST' && url.pathname === messagesPath) return postMessage(req, res, serverId);
    const m = url.pathname.startsWith(blobsPath + '/') ? url.pathname.slice(blobsPath.length + 1) : null;
    if (req.method === 'PUT' && m !== null) return putBlob(req, res, serverId, m);
    json(res, 404, { error: 'no such route', messagesPath, blobsPath });
  });
  return { server, state, messagesPath, blobsPath };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const port = Number(process.env.PORT ?? 8787);
  const { server, messagesPath, blobsPath } = createMock();
  server.listen(port, () => console.log(`mock-ingest listening on http://localhost:${port}  POST ${messagesPath}  PUT ${blobsPath}/<sha256>  (token: $RLVL_MOCK_TOKEN, serverId: $RLVL_MOCK_SERVER_ID)`));
}
