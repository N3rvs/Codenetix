#!/usr/bin/env node
// Generador determinista de tipos TypeScript desde /openapi/v1.json (AD-14).
// Uso:
//   node ./scripts/generate-api.mjs                                 (lee panel/openapi/v1.json)
//   node ./scripts/generate-api.mjs --check                         (verifica que generated.ts está al día)
//   node ./scripts/generate-api.mjs http://127.0.0.1:5000/openapi/v1.json

import { readFile, writeFile, mkdir } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = dirname(fileURLToPath(import.meta.url))
const defaultInput = resolve(__dirname, '../openapi/v1.json')
const outputPath = resolve(__dirname, '../src/api/generated.ts')

const cliArgs = process.argv.slice(2)
const checkMode = cliArgs.includes('--check')
const positionalSource = cliArgs.find((arg) => !arg.startsWith('--'))
const source = positionalSource ?? process.env.CODENETIX_OPENAPI_SOURCE ?? defaultInput

async function loadOpenApiDocument(input) {
  if (input.startsWith('http://') || input.startsWith('https://')) {
    const headers = { Accept: 'application/json' }
    if (process.env.CODENETIX_ADMIN_TOKEN) {
      headers.Authorization = `Bearer ${process.env.CODENETIX_ADMIN_TOKEN}`
    }
    const response = await fetch(input, { headers })
    if (!response.ok) {
      throw new Error(`No se pudo descargar OpenAPI desde ${input}: HTTP ${response.status}`)
    }
    return await response.json()
  }

  const raw = await readFile(resolve(input), 'utf8')
  return JSON.parse(raw)
}

function refName(ref) {
  const prefix = '#/components/schemas/'
  if (!ref.startsWith(prefix)) {
    throw new Error(`Referencia OpenAPI no soportada: ${ref}`)
  }
  return ref.slice(prefix.length)
}

function quoteLiteral(value) {
  return JSON.stringify(value)
}

function inlineObjectToTs(schema, schemas) {
  const requiredSet = new Set(Array.isArray(schema.required) ? schema.required : [])
  const entries = Object.entries(schema.properties)
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([propName, propSchema]) => {
      const propType = schemaToTs(propSchema, schemas)
      const optional = requiredSet.has(propName) ? '' : '?'
      return `${propName}${optional}: ${propType}`
    })
  return `{ ${entries.join('; ')} }`
}

function schemaToTs(schema, schemas) {
  if (!schema || typeof schema !== 'object') {
    return 'unknown'
  }

  if (schema.$ref) {
    return refName(schema.$ref)
  }

  if (Array.isArray(schema.anyOf) || Array.isArray(schema.oneOf)) {
    const branches = (schema.anyOf ?? schema.oneOf).map((b) => schemaToTs(b, schemas))
    return [...new Set(branches)].join(' | ')
  }

  if (Array.isArray(schema.allOf)) {
    const parts = schema.allOf.map((b) => schemaToTs(b, schemas))
    if (schema.properties) {
      parts.push(inlineObjectToTs(schema, schemas))
    }
    return parts.join(' & ')
  }

  if (Array.isArray(schema.enum)) {
    const literals = schema.enum.map((v) => (v === null ? 'null' : quoteLiteral(v)))
    return literals.join(' | ')
  }

  if (schema.const !== undefined) {
    return schema.const === null ? 'null' : quoteLiteral(schema.const)
  }

  const types = Array.isArray(schema.type) ? schema.type : schema.type ? [schema.type] : []
  const allowsNull = types.includes('null') || schema.nullable === true
  const nonNullTypes = types.filter((t) => t !== 'null')

  let baseType = 'unknown'

  // Microsoft.AspNetCore.OpenApi emite ["integer", "string"] con format int32/int64 por AllowReadingFromString.
  if (nonNullTypes.includes('integer') || nonNullTypes.includes('number')) {
    baseType = 'number'
  } else if (nonNullTypes.includes('boolean')) {
    baseType = 'boolean'
  } else if (nonNullTypes.includes('string')) {
    baseType = 'string'
  } else if (nonNullTypes.includes('array')) {
    const itemTs = schemaToTs(schema.items, schemas)
    const wrapped = itemTs.includes(' | ') ? `(${itemTs})` : itemTs
    baseType = `${wrapped}[]`
  } else if (nonNullTypes.includes('object') || schema.properties) {
    baseType = schema.properties ? inlineObjectToTs(schema, schemas) : 'Record<string, unknown>'
  } else if (allowsNull && nonNullTypes.length === 0) {
    return 'null'
  }

  if (allowsNull && !baseType.split(' | ').includes('null')) {
    return `${baseType} | null`
  }

  return baseType
}

function isRunEventVariant(name, schema) {
  return (
    name.startsWith('RunEvent') &&
    name !== 'RunEvent' &&
    schema?.properties?.type &&
    Array.isArray(schema.properties.type.enum) &&
    schema.properties.type.enum.length === 1
  )
}

function emitSchema(name, schema, schemas) {
  if (Array.isArray(schema.enum)) {
    // Preservar los valores literales del enum y, si el esquema OpenAPI incluye null, añadir `| null`.
    const values = schema.enum.filter((v) => v !== null)
    const hasNull = schema.enum.includes(null)
    const union = values.map(quoteLiteral).join(' | ')
    return hasNull ? `export type ${name} = ${union} | null` : `export type ${name} = ${union}`
  }

  if (Array.isArray(schema.anyOf) || Array.isArray(schema.oneOf)) {
    const variants = (schema.anyOf ?? schema.oneOf).map((v) => schemaToTs(v, schemas))
    const lines = [`export type ${name} =`]
    for (const v of variants) {
      lines.push(`  | ${v}`)
    }
    return lines.join('\n')
  }

  if (Array.isArray(schema.allOf)) {
    return `export type ${name} = ${schemaToTs(schema, schemas)}`
  }

  if (schema.properties) {
    const requiredSet = new Set(Array.isArray(schema.required) ? schema.required : [])
    const variant = isRunEventVariant(name, schema)
    if (variant) {
      requiredSet.add('type')
      requiredSet.add('seq')
      requiredSet.add('runId')
      requiredSet.add('at')
    }

    const lines = [`export interface ${name} {`]
    for (const [propName, propSchema] of Object.entries(schema.properties)) {
      const tsType = schemaToTs(propSchema, schemas)
      const isOptional = !requiredSet.has(propName)
      lines.push(`  ${propName}${isOptional ? '?' : ''}: ${tsType}`)
    }
    lines.push('}')
    return lines.join('\n')
  }

  return `export type ${name} = ${schemaToTs(schema, schemas)}`
}

async function main() {
  const doc = await loadOpenApiDocument(source)
  const schemas = doc?.components?.schemas
  if (!schemas || typeof schemas !== 'object') {
    throw new Error('El documento OpenAPI no contiene components.schemas.')
  }

  const orderedNames = Object.keys(schemas).sort((a, b) => a.localeCompare(b))
  const chunks = [
    '// Archivo generado automáticamente desde /openapi/v1.json (AD-14). No editar a mano.',
    '// Regenerar con: npm run generate:api',
    '',
  ]

  for (const name of orderedNames) {
    chunks.push(emitSchema(name, schemas[name], schemas))
    chunks.push('')
  }

  if (schemas.RunEvent) {
    chunks.push("export type RunEventType = RunEvent['type']")
    chunks.push('')
  }

  const generated = chunks.join('\n')

  if (checkMode) {
    let existing = ''
    try {
      existing = await readFile(outputPath, 'utf8')
    } catch {
      console.error(`No existe ${outputPath}. Ejecuta 'npm run generate:api'.`)
      process.exit(1)
    }

    const normalize = (s) => s.replace(/\r\n/g, '\n')
    if (normalize(existing) !== normalize(generated)) {
      console.error(
        `Desfase detectado en ${outputPath} respecto a ${source}. Ejecuta 'npm run generate:api' para regenerarlo.`,
      )
      process.exit(1)
    }

    console.log(`Contrato TS al día: ${outputPath} (${orderedNames.length} esquemas)`)
    return
  }

  await mkdir(dirname(outputPath), { recursive: true })
  await writeFile(outputPath, generated, 'utf8')
  console.log(`Generado ${outputPath} (${orderedNames.length} esquemas)`)
}

main().catch((err) => {
  console.error(err)
  process.exit(1)
})
