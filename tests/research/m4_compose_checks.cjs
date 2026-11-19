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
