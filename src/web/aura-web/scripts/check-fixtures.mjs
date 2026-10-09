
import fs from 'node:fs'
import path from 'node:path'

const fixtureDir = path.resolve('src/mocks/fixtures')
const contractDir = path.resolve('../../../contracts/examples')

const fixtures = [
  ['queued.json', 'create-analysis-202.json'],
  ['processing.json', 'analysis-processing-200.json'],
  ['completed.json', 'analysis-completed-200.json'],
  ['failed.json', 'analysis-failed-200.json'],
  ['quality-reject.json', 'analysis-low-quality-200.json'],
  ['error-400.json', 'error-400.json'],
  ['error-401.json', 'error-401.json'],
  ['error-403.json', 'error-403.json'],
]

let failed = 0

for (const [fixtureName, contractName] of fixtures) {
  try {
    const fixture = JSON.parse(
      fs.readFileSync(path.join(fixtureDir, fixtureName), 'utf8')
    )

    const contract = JSON.parse(
      fs.readFileSync(path.join(contractDir, contractName), 'utf8')
    )

    if (JSON.stringify(fixture) === JSON.stringify(contract)) {
      console.log(`PASS: ${fixtureName}`)
    } else {
      console.log(`FAIL: ${fixtureName} differs from contract example`)
      failed++
    }
  } catch (error) {
    console.log(`FAIL: ${fixtureName} - ${error.message}`)
    failed++
  }
}

if (failed > 0) {
  process.exitCode = 1
} else {
  console.log('All fixtures match contract examples.')
}
