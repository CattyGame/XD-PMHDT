#!/usr/bin/env node
// Validate actual TestServer responses rather than independently invented UI fixtures.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '..');
const web = createRequire(path.join(root, 'src/web/aura-web/package.json'));
const parser = web('@apidevtools/swagger-parser');
const Ajv = web('ajv');
const formats = web('ajv-formats');
function normalize(schema) {
  if (Array.isArray(schema)) return schema.map(normalize);
  if (!schema || typeof schema !== 'object') return schema;
  const value = Object.fromEntries(Object.entries(schema).filter(([key]) => key !== 'nullable').map(([key, item]) => [key, normalize(item)]));
  return schema.nullable ? { anyOf: [value, { type: 'null' }] } : value;
}
(async () => {
  const directory = process.argv[2];
  if (!directory) throw new Error('Pass AURA_M1_ARTIFACTS with serialized runtime response samples.');
  const required = ['publication', 'orders', 'usage', 'summary', 'templates', 'admin-catalog', 'retirement', 'owner-summary'];
  const contracts = {};
  for (const name of ['billing', 'notification']) contracts[name] = await parser.dereference(path.join(root, `contracts/${name}-openapi.yaml`));
  const ajv = new Ajv({ allErrors: true, strict: false }); formats(ajv);
  for (const name of required) {
    const sample = JSON.parse(fs.readFileSync(path.join(directory, `${name}.json`), 'utf8'));
    const schema = contracts[sample.contract]?.paths[sample.path]?.[sample.method]?.responses[String(sample.status)]?.content?.['application/json']?.schema;
    assert(schema, `Missing response schema for ${name}`);
    const validate = ajv.compile(normalize(schema));
    assert(validate(sample.value), `${name}: ${ajv.errorsText(validate.errors)}`);
    assert(schema.required?.length, `${name}: response must declare required fields`);
    const missing = { ...sample.value }; delete missing[schema.required[0]];
    assert.equal(validate(missing), false, `${name}: validator must reject a required-field deletion`);
  }
  console.log(`PASS: ${required.length} actual Billing/Notification responses match their contracts, including negative controls.`);
})().catch(error => { console.error(error.message); process.exitCode = 1; });
