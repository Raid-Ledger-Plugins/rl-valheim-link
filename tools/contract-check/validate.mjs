#!/usr/bin/env node
// Validate JSON files against contract/v1/schema.json (draft 2020-12) with ajv.
// Usage: node validate.mjs [--expect-invalid] <file-or-dir>...
// Exit 1 when a file fails (or, with --expect-invalid, when a file PASSES).
// This is the JSON-Schema half of CI; the C# validator (dotnet test) covers the §6 refinements.
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, resolve, dirname, basename } from 'node:path';
import { fileURLToPath } from 'node:url';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const here = dirname(fileURLToPath(import.meta.url));
const schemaPath = resolve(here, '../../contract/v1/schema.json');
const schema = JSON.parse(readFileSync(schemaPath, 'utf8'));

const ajv = new Ajv2020({ allErrors: true, strict: false });
addFormats(ajv);
const validate = ajv.compile(schema);

const args = process.argv.slice(2);
const expectInvalid = args.includes('--expect-invalid');
// CONTRACT.md §6: refinements (code "custom" in EXPECTED.json) are not expressible in schema.json;
// those fixtures must be rejected by the C# pre-send validator (dotnet test), not by ajv.
const expectedPath = resolve(here, '../../contract/v1/fixtures/invalid/EXPECTED.json');
const expected = expectInvalid ? JSON.parse(readFileSync(expectedPath, 'utf8')) : {};
const targets = args.filter((a) => !a.startsWith('--'));
if (targets.length === 0) { console.error('usage: validate.mjs [--expect-invalid] <file-or-dir>...'); process.exit(2); }

const files = [];
for (const t of targets) {
  const p = resolve(t);
  if (statSync(p).isDirectory()) {
    for (const f of readdirSync(p).sort()) if (f.endsWith('.json') && f !== 'EXPECTED.json') files.push(join(p, f));
  } else files.push(p);
}

let failed = 0;
for (const f of files) {
  const data = JSON.parse(readFileSync(f, 'utf8'));
  const ok = validate(data);
  const bytes = statSync(f).size;
  if (expectInvalid) {
    const exp = expected[basename(f, '.json')];
    if (ok && exp && exp.code === 'custom') console.log(`skip ${basename(f)}: §6 refinement at ${exp.path} (C# validator covers it)`);
    else if (ok) { failed++; console.log(`FAIL ${basename(f)}: schema ACCEPTED a fixture that must be rejected`); }
    else console.log(`ok   ${basename(f)} rejected: ${validate.errors[0].instancePath || '/'} ${validate.errors[0].message}`);
  } else if (ok) {
    console.log(`ok   ${basename(f)} (${data.kind}, ${bytes} bytes)`);
  } else {
    failed++;
    console.log(`FAIL ${basename(f)}:`);
    for (const e of validate.errors.slice(0, 10)) console.log(`     ${e.instancePath || '/'} ${e.message} ${JSON.stringify(e.params)}`);
  }
}
console.log(failed ? `${failed} file(s) failed` : `${files.length} file(s) ${expectInvalid ? 'rejected as required' : 'valid'}`);
process.exit(failed ? 1 : 0);
