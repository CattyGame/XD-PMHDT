// Container configuration must not inherit host-only Development key paths.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const yaml = createRequire(path.join(root, 'src/web/aura-web/package.json'))('js-yaml');
const compose = yaml.load(fs.readFileSync(path.join(root, 'compose.yaml'), 'utf8'));
const development = JSON.parse(fs.readFileSync(path.join(root, 'src/services/AURA.Billing.Api/appsettings.Development.json'), 'utf8'));
const environment = compose.services['billing-api'].environment;
const key = environment.ServiceAuth__PublicKeyPath ?? development.ServiceAuth?.PublicKeyPath ?? '';
assert.ok(!/^[a-z]:[\\/]/i.test(key), 'Billing container inherits a Windows public-key path');
assert.ok(key === '' || key.startsWith('/'), 'Container public-key path must be empty (Identity lookup) or absolute Linux path');
assert.equal(environment.Services__Identity__BaseUrl, 'http://identity-api:8080');
console.log('PASS: Billing container uses a portable trusted Identity public-key configuration.');
const dependency = fs.readFileSync(path.join(root, 'src/common/AURA.Common.Cache/AURA.Common.Cache.csproj'), 'utf8');
const frameworks = dependency.match(/<TargetFrameworks>([^<]+)<\/TargetFrameworks>/)[1].split(';');
const requiredSdk = Math.max(...frameworks.map(value => Number(value.match(/^net(\d+)\./)[1])));
const workerDockerfile = fs.readFileSync(path.join(root, 'src/services/Aura.Worker.Analysis/Dockerfile'), 'utf8');
const buildSdk = Number(workerDockerfile.match(/^FROM mcr\.microsoft\.com\/dotnet\/sdk:(\d+)\./m)[1]);
assert.ok(buildSdk >= requiredSdk, 'Worker build SDK cannot restore every Common.Cache target framework');
console.log('PASS: Worker build SDK supports its referenced multi-target dependency.');

const manifestFile = path.join(root, 'src/ai-core/config/artifact-manifests.json');
const catalog = JSON.parse(fs.readFileSync(manifestFile, 'utf8'));
assert.ok(Array.isArray(catalog.manifests) && catalog.manifests.length >= 3, 'Expected verified server-side AI manifests.');
const manifestIds = new Set();
for (const entry of catalog.manifests) {
  assert.ok(!manifestIds.has(entry.artifactManifestId), `Duplicate manifest ID: ${entry.artifactManifestId}`);
  manifestIds.add(entry.artifactManifestId);
  const resolved = path.resolve(root, 'src/ai-core/config', entry.configFile);
  assert.ok(resolved.startsWith(path.resolve(root, 'src/ai-core/config') + path.sep), 'Manifest config traversal rejected.');
  const canonicalBytes = Buffer.from(fs.readFileSync(resolved, 'utf8').replace(/\r\n/g, '\n'), 'utf8');
  const actualSha = crypto.createHash('sha256').update(canonicalBytes).digest('hex');
  assert.equal(actualSha, entry.thresholdConfigSha256, `SHA-256 mismatch for ${entry.configFile}`);
}
assert.ok(manifestIds.has(catalog.defaultManifestId), 'defaultManifestId missing from verified manifests.');
console.log(`PASS: ${catalog.manifests.length} AI artifact manifests match config SHA-256 checksums on disk.`);

const gatewayRaw = fs.readFileSync(path.join(root, 'src/gateway/AURA.ApiGateway/appsettings.json'), 'utf8');
const gateway = JSON.parse(gatewayRaw);
const serializedClusters = JSON.stringify(gateway.ReverseProxy.Clusters).toLowerCase();
const serializedRoutes = JSON.stringify(gateway.ReverseProxy.Routes).toLowerCase();
assert.ok(!serializedClusters.includes('ai-core') && !serializedClusters.includes(':8000'), 'API Gateway must not expose AI Core cluster.');
assert.ok(!serializedRoutes.includes('ai-core') && !serializedRoutes.includes('/reload'), 'API Gateway must not expose AI Core reload endpoints.');
assert.ok(!JSON.stringify(compose.services['ai-core'] || {}).includes('reload'), 'Compose AI Core profile must stay internal without public reload.');
console.log('PASS: API Gateway and Compose keep AI Core internal without public reload exposure.');

const composeDev = yaml.load(fs.readFileSync(path.join(root, 'compose.dev.yaml'), 'utf8'));
const composeAdr005 = yaml.load(fs.readFileSync(path.join(root, 'compose.adr005.yaml'), 'utf8'));
const composeIntegrated = yaml.load(fs.readFileSync(path.join(root, 'compose.research-integrated.yaml'), 'utf8'));
assert.equal(composeDev.services['analysis-api'].environment.Services__Ai__BaseUrl, 'http://ai-core:8000', 'compose.dev.yaml must wire Services__Ai__BaseUrl.');
assert.equal(composeAdr005.services['analysis-api'].environment.Services__Ai__BaseUrl, 'http://ai-core:8000', 'compose.adr005.yaml must wire Services__Ai__BaseUrl.');
assert.ok(!('AURA_FRANGI_CONFIG_PATH' in (composeAdr005.services['ai-core']?.environment || {})), 'compose.adr005.yaml must use AURA_BOOTSTRAP_MANIFEST_ID instead of AURA_FRANGI_CONFIG_PATH.');
assert.ok(!('AURA_FRANGI_CONFIG_PATH' in (composeIntegrated.services['ai-core']?.environment || {})), 'compose.research-integrated.yaml must use AURA_BOOTSTRAP_MANIFEST_ID instead of AURA_FRANGI_CONFIG_PATH.');
assert.ok(String(compose.services['ai-core']?.environment?.AURA_RUNTIME_STATE_PATH || '').startsWith('/var/lib/aura-ai-state/'), 'compose.yaml ai-core must configure persistent AURA_RUNTIME_STATE_PATH.');
assert.ok((compose.services['ai-core']?.volumes || []).some(v => String(v).includes('ai_core_state:/var/lib/aura-ai-state')), 'compose.yaml ai-core must mount ai_core_state volume.');
assert.ok('ai_core_state' in (compose.volumes || {}), 'compose.yaml must declare ai_core_state volume.');
const serviceClients = JSON.parse(fs.readFileSync(path.join(root, 'infra/service-auth/clients.example.json'), 'utf8'));
const analysisApiClient = (serviceClients.clients || []).find(c => c.clientId === 'analysis-api');
assert.deepEqual(analysisApiClient?.allowedTargets?.['aura-ai'], ['ai:config'], 'clients.example.json must grant analysis-api ai:config scope on aura-ai.');
console.log('PASS: Compose AI state persistence, bootstrap policy, and analysis-api ai:config scope verified.');

const aiCore = compose.services['ai-core'] || {};
const aiCoreCmd = Array.isArray(aiCore.command) ? aiCore.command.join(' ') : String(aiCore.command || '');
assert.ok(Number(aiCore.cpus) > 0, 'ai-core must pin CPU limit in compose.yaml');
assert.ok(String(aiCore.mem_limit || '').trim().length > 0, 'ai-core must pin mem_limit in compose.yaml');
assert.ok(Number(aiCore.pids_limit) > 0, 'ai-core must pin pids_limit in compose.yaml');
assert.ok(aiCoreCmd.includes('--workers 1'), 'ai-core must pin single worker (--workers 1) for bounded memory.');
console.log(`PASS: AI Core compose limits pinned (cpus=${aiCore.cpus}, mem_limit=${aiCore.mem_limit}, pids_limit=${aiCore.pids_limit}, --workers 1).`);

const v2EvalReport = JSON.parse(fs.readFileSync(path.join(root, 'docs/benchmarks/v2_evaluation_report_s7.json'), 'utf8'));
assert.equal(v2EvalReport.clinicalValidation, false, 'V2 report must keep clinicalValidation=false.');
assert.equal(v2EvalReport.scientificGateScrum373?.status, 'OPEN_NOT_PASSED', 'SCRUM-373 must stay OPEN_NOT_PASSED.');
assert.equal(v2EvalReport.scientificGateScrum373?.autoCloseAllowed, false, 'SCRUM-373 autoCloseAllowed must be false.');

const w7PerfProfile = JSON.parse(fs.readFileSync(path.join(root, 'docs/benchmarks/w7_latency_ram_profile_v1.0.json'), 'utf8'));
assert.equal(w7PerfProfile.frangiApiProfile?.srsHardSla?.maxP95Ms, 20000, 'SRS hard SLA must stay 20000 ms.');
assert.equal(w7PerfProfile.frangiApiProfile?.srsHardSla?.passed, true, 'SRS hard SLA must pass.');
assert.equal(w7PerfProfile.goldenParityGate?.passed, true, 'Golden parity gate must pass.');
assert.equal(w7PerfProfile.goldenParityGate?.mismatchedPixels, 0, 'Golden parity gate must have 0 mismatched pixels.');
console.log('PASS: Sprint 7 V2 evaluation report and latency/RAM/parity profile invariants verified.');

const w8Registry = JSON.parse(fs.readFileSync(path.join(root, 'docs/benchmarks/w8_artifact_registry_v1.0.json'), 'utf8'));
assert.equal(w8Registry.clinicalValidation, false, 'W8 artifact registry must keep clinicalValidation=false.');
assert.equal(w8Registry.iowaRedistributedInGit, false, 'W8 registry must forbid Iowa in Git.');
assert.equal(w8Registry.iowaRedistributedInZip, false, 'W8 registry must forbid Iowa in ZIP.');
assert.equal(w8Registry.scientificGateScrum373?.status, 'OPEN_NOT_PASSED', 'W8 registry must keep SCRUM-373 OPEN_NOT_PASSED.');

const w8Acceptance = JSON.parse(fs.readFileSync(path.join(root, 'docs/benchmarks/w8_v2_acceptance_summary.json'), 'utf8'));
assert.equal(w8Acceptance.functionalSoftwareHandoff?.expertConfirmedGeneratedFromAuto, false, 'W8 acceptance must never fabricate expert_confirmed from AUTO.');
assert.equal(w8Acceptance.scientificValidation?.clinicalValidation, false, 'W8 acceptance must keep clinicalValidation=false.');
assert.equal(w8Acceptance.scientificValidation?.scientificGateScrum373?.status, 'OPEN_NOT_PASSED', 'W8 acceptance must keep SCRUM-373 OPEN_NOT_PASSED.');
assert.ok(fs.existsSync(path.join(root, 'docs/ai/w8_m4_v2_software_and_scientific_acceptance.md')), 'Missing W8-M4-02 acceptance markdown.');
assert.ok(fs.existsSync(path.join(root, 'docs/ai/w8_m4_3min_defense_script_and_cases.md')), 'Missing W8-M4-03 defense script markdown.');
console.log('PASS: Sprint 8 artifact registry, V2 acceptance invariants, and defense docs verified.');
