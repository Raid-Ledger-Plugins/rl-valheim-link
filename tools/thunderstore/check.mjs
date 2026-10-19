#!/usr/bin/env node
// Thunderstore release checks (stdlib only), mirroring the GamerSaloon modpack's check_pack.py:
//   version              print thunderstore.toml versionNumber
//   consistent           versionNumber == Directory.Build.props ModVersion == Plugin.Version const
//   published            exit 0 if namespace-name-version is already on Thunderstore, 1 if not
//   bumped <base.toml>   versionNumber is higher than the base file's
//   changelog            CHANGELOG.md has a "## <version>" section
//   deps                 every pinned dependency exists on Thunderstore at that version
import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const API = 'https://thunderstore.io/api/experimental/package';
const HEADERS = { 'user-agent': 'rl-valheim-link-ci (+https://github.com/Raid-Ledger-Plugins/rl-valheim-link)' };

function loadToml(path) {
  // Minimal reader for the keys we use (flat `key = "value"` under [package] and [package.dependencies]).
  const text = readFileSync(path, 'utf8');
  const out = { package: {}, dependencies: {} };
  let section = '';
  for (const raw of text.split('\n')) {
    const line = raw.replace(/#.*$/, '').trim();
    if (!line) continue;
    const sec = /^\[\[?([^\]]+)\]\]?$/.exec(line);
    if (sec) { section = sec[1]; continue; }
    const kv = /^([A-Za-z0-9_.-]+)\s*=\s*"([^"]*)"/.exec(line);
    if (!kv) continue;
    if (section === 'package') out.package[kv[1]] = kv[2];
    else if (section === 'package.dependencies') out.dependencies[kv[1]] = kv[2];
  }
  return out;
}

const parseVersion = (v) => v.split('.').map((n) => Number.parseInt(n, 10));
const cmp = (a, b) => { const x = parseVersion(a), y = parseVersion(b); for (let i = 0; i < 3; i++) if ((x[i] ?? 0) !== (y[i] ?? 0)) return (x[i] ?? 0) - (y[i] ?? 0); return 0; };

async function exists(ns, name, version) {
  const r = await fetch(`${API}/${ns}/${name}/${version}/`, { headers: HEADERS });
  if (r.ok) return (await r.json()).version_number === version;
  if (r.status !== 404) throw new Error(`Thunderstore ${r.status} for ${ns}-${name}-${version}`);
  // The per-version route lags behind a fresh upload; the package route's `latest` does not.
  const p = await fetch(`${API}/${ns}/${name}/`, { headers: HEADERS });
  if (p.status === 404) return false;
  if (!p.ok) throw new Error(`Thunderstore ${p.status} for ${ns}-${name}`);
  const latest = (await p.json()).latest?.version_number;
  return latest !== undefined && cmp(latest, version) >= 0;
}

const cfg = loadToml(resolve(root, 'thunderstore.toml'));
const { namespace, name, versionNumber } = cfg.package;
const cmd = process.argv[2];

const fail = (msg) => { console.error(msg); process.exit(1); };

switch (cmd) {
  case 'version': console.log(versionNumber); break;
  case 'consistent': {
    const props = /<ModVersion>([^<]+)<\/ModVersion>/.exec(readFileSync(resolve(root, 'Directory.Build.props'), 'utf8'))?.[1];
    const code = /public const string Version = "([^"]+)"/.exec(readFileSync(resolve(root, 'src/RlValheimLink/Plugin.cs'), 'utf8'))?.[1];
    console.log(`thunderstore.toml=${versionNumber} Directory.Build.props=${props} Plugin.cs=${code}`);
    if (versionNumber !== props || versionNumber !== code) fail('version mismatch: bump all three together');
    if (!/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(versionNumber)) fail('Thunderstore needs a plain MAJOR.MINOR.PATCH version');
    if ((cfg.package.description ?? '').length > 250) fail('description must be ≤ 250 characters');
    break;
  }
  case 'published': {
    const yes = await exists(namespace, name, versionNumber);
    console.log(`${namespace}-${name}-${versionNumber}: ${yes ? 'already on Thunderstore' : 'not published yet'}`);
    process.exit(yes ? 0 : 1);
  }
  case 'bumped': {
    const base = loadToml(resolve(process.argv[3])).package.versionNumber;
    console.log(`base=${base} head=${versionNumber}`);
    if (cmp(versionNumber, base) <= 0) fail('versionNumber must be higher than main\'s (shipped files changed)');
    break;
  }
  case 'changelog': {
    const text = readFileSync(resolve(root, 'CHANGELOG.md'), 'utf8');
    if (!new RegExp(`^## ${versionNumber.replace(/\./g, '\\.')}\\b`, 'm').test(text)) fail(`CHANGELOG.md has no "## ${versionNumber}" section`);
    console.log(`CHANGELOG.md has ## ${versionNumber}`);
    break;
  }
  case 'deps': {
    let bad = 0;
    for (const [dep, ver] of Object.entries(cfg.dependencies)) {
      const [ns, ...rest] = dep.split('-');
      const ok = await exists(ns, rest.join('-'), ver);
      console.log(`${ok ? 'ok  ' : 'FAIL'} ${dep}-${ver}`);
      if (!ok) bad++;
    }
    if (bad) fail(`${bad} dependency(ies) missing on Thunderstore`);
    break;
  }
  default: fail('usage: check.mjs version|consistent|published|bumped <base.toml>|changelog|deps');
}
