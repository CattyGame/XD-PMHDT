
import fs from 'node:fs'
import path from 'node:path'
import SwaggerParser from '@apidevtools/swagger-parser'
import Ajv from 'ajv'
import addFormats from 'ajv-formats'

const contractPath = path.resolve(
  '../../../contracts/analysis-openapi.yaml'
)

const fixtureDir = path.resolve('src/mocks/fixtures')

const cases = [
  {
    file: 'queued.json',
    operationId: 'createAnalysis',
    status: '202',
  },
  {
    file: 'processing.json',
    operationId: 'getAnalysis',
    status: '200',
  },
  {
    file: 'completed.json',
    operationId: 'getAnalysis',
    status: '200',
  },
  {
    file: 'failed.json',
    operationId: 'getAnalysis',
    status: '200',
  },
  {
    file: 'quality-reject.json',
    operationId: 'getAnalysis',
    status: '200',
  },
]

const ajv = new Ajv({
  allErrors: true,
  strict: false,
})

addFormats(ajv)

try {
  const api = await SwaggerParser.dereference(contractPath)

  let failed = 0

  for (const testCase of cases) {
    const operation = Object.values(api.paths)
      .flatMap(pathItem => Object.values(pathItem))
      .find(item => item?.operationId === testCase.operationId)

    const schema =
      operation?.responses?.[testCase.status]
        ?.content?.['application/json']?.schema

    if (!schema) {
      console.log(`FAIL: ${testCase.file} - schema not found`)
      failed++
      continue
    }

    const fixture = JSON.parse(
      fs.readFileSync(
        path.join(fixtureDir, testCase.file),
        'utf8'
      )
    )

    const validate = ajv.compile(schema)
    const valid = validate(fixture)

    if (valid) {
      console.log(`PASS: ${testCase.file}`)
    } else {
      console.log(`FAIL: ${testCase.file}`)
      console.log(validate.errors)
      failed++
    }
  }

  if (failed > 0) {
    process.exitCode = 1
  } else {
    console.log('All selected fixtures passed schema validation.')
  }
} catch (error) {
  console.error('Validation error:', error.message)
  process.exitCode = 1
}
