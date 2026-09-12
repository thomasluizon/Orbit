#!/usr/bin/env node
import { createHash } from "node:crypto"
import { readdirSync, readFileSync, statSync, writeFileSync } from "node:fs"
import { dirname, join, relative, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const GENERATOR_VERSION = 1
const TOOL_DIRECTORY = dirname(fileURLToPath(import.meta.url))
const DEFAULT_ROOT = resolve(TOOL_DIRECTORY, "..")
const USAGE = `usage: node tools/gating-matrix.mjs [--root <repository>]

Writes gating-matrix.json at the repository root. The file is generated, not committed.

Options:
  --root <repository>  read another repository-shaped tree
  --help, -h           print this help
`

function parseArguments(args) {
  if (args.includes("--help") || args.includes("-h")) {
    process.stdout.write(USAGE)
    process.exit(0)
  }

  if (args.length === 0) return DEFAULT_ROOT
  if (args.length === 2 && args[0] === "--root" && args[1]) return resolve(args[1])
  throw new Error(`invalid arguments\n\n${USAGE}`)
}

function byCode(a, b) {
  return a < b ? -1 : a > b ? 1 : 0
}

function toPosix(root, path) {
  return relative(root, path).split("\\").join("/")
}

function walkCs(directory, found = []) {
  for (const entry of readdirSync(directory, { withFileTypes: true }).sort((a, b) => byCode(a.name, b.name))) {
    if (entry.name === "bin" || entry.name === "obj") continue
    const path = join(directory, entry.name)
    if (entry.isDirectory()) walkCs(path, found)
    else if (entry.name.endsWith(".cs")) found.push(path)
  }
  return found
}

function stripComments(source) {
  let result = ""
  let index = 0
  let mode = "code"
  while (index < source.length) {
    const pair = source.slice(index, index + 2)
    const character = source[index]
    if (mode === "code") {
      if (pair === "//") mode = "line"
      else if (pair === "/*") mode = "block"
      else {
        result += character
        if (character === '"') mode = "string"
        else if (character === "'") mode = "character"
      }
    } else if (mode === "line") {
      if (character === "\n") {
        result += character
        mode = "code"
      }
    } else if (mode === "block") {
      if (pair === "*/") {
        mode = "code"
        index += 1
      } else if (character === "\n") result += character
    } else {
      result += character
      if (character === "\\") {
        result += source[index + 1] ?? ""
        index += 1
      } else if ((mode === "string" && character === '"') || (mode === "character" && character === "'")) {
        mode = "code"
      }
    }
    index += 1
  }
  return result
}

function matchBalanced(text, openIndex, open, close) {
  let depth = 0
  let quote = null
  for (let index = openIndex; index < text.length; index += 1) {
    const character = text[index]
    if (quote) {
      if (character === "\\") index += 1
      else if (character === quote) quote = null
      continue
    }
    if (character === '"' || character === "'") {
      quote = character
      continue
    }
    if (character === open) depth += 1
    else if (character === close) {
      depth -= 1
      if (depth === 0) return index
    }
  }
  return -1
}

function splitTopLevel(text) {
  const parts = []
  let current = ""
  let quote = null
  const depths = { "(": 0, "[": 0, "{": 0, "<": 0 }
  const closing = { ")": "(", "]": "[", "}": "{", ">": "<" }
  for (let index = 0; index < text.length; index += 1) {
    const character = text[index]
    if (quote) {
      current += character
      if (character === "\\") {
        current += text[index + 1] ?? ""
        index += 1
      } else if (character === quote) quote = null
      continue
    }
    if (character === '"' || character === "'") {
      quote = character
      current += character
      continue
    }
    if (Object.hasOwn(depths, character)) depths[character] += 1
    else if (Object.hasOwn(closing, character)) depths[closing[character]] -= 1
    if (character === "," && Object.values(depths).every((depth) => depth === 0)) {
      parts.push(current.trim())
      current = ""
    } else current += character
  }
  if (current.trim()) parts.push(current.trim())
  return parts
}

function parseNamedArguments(text) {
  const result = {}
  for (const part of splitTopLevel(text)) {
    const match = part.match(/^(\w+)\s*:\s*([\s\S]*)$/)
    if (match) result[match[1]] = match[2].trim()
  }
  return result
}

function stringLiterals(text) {
  return [...text.matchAll(/"((?:\\.|[^"\\])*)"/g)].map((match) => JSON.parse(`"${match[1]}"`))
}

function parseLiteral(expression, constants = new Map()) {
  const value = expression.trim().replace(/^\([^)]+\)\s*/, "")
  if (value === "null") return null
  if (value === "true") return true
  if (value === "false") return false
  if (/^-?[\d_]+$/.test(value)) return Number(value.replaceAll("_", ""))
  if (/^"(?:\\.|[^"\\])*"$/.test(value)) return JSON.parse(value)
  const member = value.match(/^(\w+)\.(\w+)$/)
  if (member && constants.has(`${member[1]}.${member[2]}`)) return constants.get(`${member[1]}.${member[2]}`)
  return null
}

function parseConstants(source, className) {
  const constants = new Map()
  const pattern = /public\s+const\s+[\w.?<>\[\]]+\s+(\w+)\s*=\s*([^;]+);/g
  let match
  while ((match = pattern.exec(stripComments(source))) !== null) {
    constants.set(`${className}.${match[1]}`, parseLiteral(match[2], constants))
  }
  return constants
}

function parseStringConstants(source) {
  const constants = new Map()
  const pattern = /public\s+const\s+string\s+(\w+)\s*=\s*"([^"]+)"\s*;/g
  let match
  while ((match = pattern.exec(stripComments(source))) !== null) constants.set(match[1], match[2])
  return constants
}

function extractMethod(source, name) {
  const text = stripComments(source)
  const declaration = new RegExp(`\\b(?:public|private|protected)\\s+(?:(?:static|override|virtual|async|sealed|new)\\s+)*[\\w<>.?]+\\s+${name}\\s*\\(`)
  const match = declaration.exec(text)
  if (!match) return null
  const parametersOpen = text.indexOf("(", match.index)
  const parametersClose = matchBalanced(text, parametersOpen, "(", ")")
  if (parametersClose === -1) return null
  const remainder = text.slice(parametersClose + 1)
  const bodyOffset = remainder.search(/[={]/)
  if (bodyOffset === -1) return null
  const bodyStart = parametersClose + 1 + bodyOffset
  if (text[bodyStart] === "{") {
    const bodyEnd = matchBalanced(text, bodyStart, "{", "}")
    return bodyEnd === -1 ? null : text.slice(bodyStart + 1, bodyEnd)
  }
  const arrow = text.indexOf("=>", parametersClose)
  const semicolon = text.indexOf(";", arrow)
  return arrow === -1 || semicolon === -1 ? null : text.slice(arrow + 2, semicolon)
}

function parseImplementationMethods(source) {
  const names = new Set()
  const pattern = /\b(?:public|private|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>.?]+\s+(\w+)\s*\(/g
  let match
  const text = stripComments(source)
  while ((match = pattern.exec(text)) !== null) names.add(match[1])
  return new Map([...names].map((name) => [name, extractMethod(source, name)]).filter(([, body]) => body !== null))
}

function parseInterfaceMethods(source) {
  const methods = []
  const pattern = /\bTask(?:<[^;()]+>)?\s+(\w+)\s*\(/g
  let match
  const text = stripComments(source)
  while ((match = pattern.exec(text)) !== null) methods.push(match[1])
  return [...new Set(methods)].sort(byCode)
}

function reachableBodies(methodName, methods) {
  const queue = [methodName]
  const visited = new Set()
  const bodies = []
  while (queue.length > 0) {
    const current = queue.shift()
    if (visited.has(current) || !methods.has(current)) continue
    visited.add(current)
    const body = methods.get(current)
    bodies.push(body)
    for (const called of body.matchAll(/\b(\w+)\s*\(/g)) {
      if (methods.has(called[1]) && !visited.has(called[1])) queue.push(called[1])
    }
  }
  return bodies
}

function configCalls(text, keyConstants, compiledConstants) {
  const calls = []
  const pattern = /\.GetAsync(?:<[^>]+>)?\s*\(/g
  let match
  while ((match = pattern.exec(text)) !== null) {
    const open = text.indexOf("(", match.index)
    const close = matchBalanced(text, open, "(", ")")
    if (close === -1) continue
    const args = splitTopLevel(text.slice(open + 1, close))
    const keyMember = args[0]?.match(/^AppConfigKeys\.(\w+)$/)?.[1]
    if (!keyMember || args.length < 2 || !keyConstants.has(keyMember)) continue
    calls.push({
      member: keyMember,
      key: keyConstants.get(keyMember),
      compiledDefault: parseLiteral(args[1], compiledConstants),
      compiledDefaultExpression: args[1].trim(),
    })
  }
  return calls
}

function uniqueBy(items, key) {
  return [...new Map(items.map((item) => [key(item), item])).values()]
}

function accessRequirement(bodies) {
  for (const body of bodies) {
    const match = body.match(/return\s+\w+\.Has([A-Z]\w*)Access\s*\?\s*Result\.Success\([^)]*\)\s*:\s*Result\.PayGateFailure\(/s)
    if (match) return match[1]
  }
  return null
}

function extractInvocations(source, callName) {
  const text = stripComments(source)
  const invocations = []
  let index = 0
  while ((index = text.indexOf(callName, index)) !== -1) {
    const open = text.indexOf("(", index + callName.length)
    if (open === -1) break
    const close = matchBalanced(text, open, "(", ")")
    if (close === -1) break
    invocations.push(parseNamedArguments(text.slice(open + 1, close)))
    index = close + 1
  }
  return invocations
}

function upBody(source) {
  return extractMethod(source, "Up") ?? ""
}

function parseArrayRows(expression) {
  const open = expression.indexOf("{")
  if (open === -1) return []
  const close = matchBalanced(expression, open, "{", "}")
  if (close === -1) return []
  const parts = splitTopLevel(expression.slice(open + 1, close))
  if (parts.length > 0 && parts.every((part) => part.trim().startsWith("{"))) {
    return parts.map((part) => {
      const rowOpen = part.indexOf("{")
      const rowClose = matchBalanced(part, rowOpen, "{", "}")
      return splitTopLevel(part.slice(rowOpen + 1, rowClose))
    })
  }
  return [parts]
}

function insertedRows(args, constants) {
  const columns = stringLiterals(args.columns ?? "")
  return parseArrayRows(args.values ?? "").map((values) =>
    Object.fromEntries(columns.map((column, index) => [column, parseLiteral(values[index] ?? "null", constants)])),
  )
}

function applyTableMigrations(files, table, initialRows, constants) {
  const rows = new Map(initialRows.map((row) => [row.Key, { ...row }]))
  for (const source of files) {
    const body = upBody(source)
    for (const args of extractInvocations(body, "migrationBuilder.InsertData")) {
      if (parseLiteral(args.table ?? "") !== table) continue
      for (const row of insertedRows(args, constants)) if (row.Key !== null && row.Key !== undefined) rows.set(row.Key, row)
    }
    for (const args of extractInvocations(body, "migrationBuilder.UpdateData")) {
      if (parseLiteral(args.table ?? "") !== table) continue
      const key = parseLiteral(args.keyValue ?? "", constants)
      if (!rows.has(key)) continue
      const row = rows.get(key)
      if (args.column && args.value) row[parseLiteral(args.column)] = parseLiteral(args.value, constants)
      const columns = stringLiterals(args.columns ?? "")
      const values = parseArrayRows(args.values ?? "")[0] ?? []
      columns.forEach((column, index) => {
        row[column] = parseLiteral(values[index] ?? "null", constants)
      })
    }
    for (const args of extractInvocations(body, "migrationBuilder.DeleteData")) {
      if (parseLiteral(args.table ?? "") !== table) continue
      const key = parseLiteral(args.keyValue ?? "", constants)
      if (key !== null) rows.delete(key)
    }
  }
  return rows
}

function featureSeed(source) {
  const body = extractMethod(source, "ConfigureAppFeatureFlagEntity") ?? ""
  const rows = []
  for (const match of body.matchAll(/new\s*\{([\s\S]*?)\}/g)) {
    const properties = {}
    for (const assignment of splitTopLevel(match[1])) {
      const property = assignment.match(/^(\w+)\s*=\s*([\s\S]+)$/)
      if (property) properties[property[1]] = parseLiteral(property[2])
    }
    if (typeof properties.Key === "string") rows.push(properties)
  }
  return rows
}

function provenance(inputFiles) {
  const hash = createHash("sha256")
  for (const [path, body] of [...inputFiles.entries()].sort(([a], [b]) => byCode(a, b))) {
    hash.update(path, "utf8")
    hash.update("\0")
    hash.update(body, "utf8")
    hash.update("\0")
  }
  const digest = hash.digest("hex")
  return {
    generatedFrom: digest.slice(0, 12),
    baselineRef: null,
    baselineSha: null,
    inputFiles: inputFiles.size,
    generatorVersion: GENERATOR_VERSION,
  }
}

function buildMatrix(root) {
  const sourceRoot = join(root, "src")
  if (!statSync(sourceRoot).isDirectory()) throw new Error(`missing source directory: ${sourceRoot}`)
  const inputFiles = new Map()
  for (const path of walkCs(sourceRoot)) inputFiles.set(toPosix(root, path), readFileSync(path, "utf8"))

  const read = (path) => {
    const relativePath = path.split("\\").join("/")
    if (!inputFiles.has(relativePath)) throw new Error(`missing input: ${relativePath}`)
    return inputFiles.get(relativePath)
  }

  const interfaceSource = read("src/Orbit.Domain/Interfaces/IPayGateService.cs")
  const implementationSource = read("src/Orbit.Application/Common/PayGateService.cs")
  const configKeySource = read("src/Orbit.Application/Common/AppConfigKeys.cs")
  const featureKeySource = read("src/Orbit.Application/Common/FeatureFlagKeys.cs")
  const constantsSource = read("src/Orbit.Application/Common/AppConstants.cs")
  const contextSource = read("src/Orbit.Infrastructure/Persistence/OrbitDbContext.cs")

  const configKeys = parseStringConstants(configKeySource)
  const featureKeys = parseStringConstants(featureKeySource)
  const compiledConstants = parseConstants(constantsSource, "AppConstants")
  const methods = parseImplementationMethods(implementationSource)
  const interfaceMethods = parseInterfaceMethods(interfaceSource)
  const allConfigCalls = []
  for (const [path, source] of inputFiles) {
    if (path.includes("/Migrations/") || !source.includes("AppConfigKeys.")) continue
    allConfigCalls.push(...configCalls(stripComments(source), configKeys, compiledConstants))
  }

  const migrationSources = [...inputFiles.entries()]
    .filter(([path]) => path.startsWith("src/Orbit.Infrastructure/Migrations/") && !path.endsWith(".Designer.cs") && !path.endsWith("ModelSnapshot.cs"))
    .sort(([a], [b]) => byCode(a, b))
    .map(([, source]) => source)

  const appConfigRows = applyTableMigrations(migrationSources, "AppConfigs", [], compiledConstants)
  const featureRows = applyTableMigrations(migrationSources, "AppFeatureFlags", featureSeed(contextSource), compiledConstants)

  const gates = interfaceMethods.map((method) => {
    if (!methods.has(method)) throw new Error(`interface method has no implementation: ${method}`)
    const bodies = reachableBodies(method, methods)
    const configs = uniqueBy(
      bodies.flatMap((body) => configCalls(body, configKeys, compiledConstants)),
      (config) => config.key,
    )
      .map(({ member, ...config }) => config)
      .sort((a, b) => byCode(a.key, b.key))
    const referencedFlags = uniqueBy(
      bodies.flatMap((body) => [...body.matchAll(/FeatureFlagKeys\.(\w+)/g)].map((match) => match[1])),
      (member) => member,
    )
      .map((member) => featureKeys.get(member))
      .filter(Boolean)
      .sort(byCode)
    return {
      capability: method,
      enforcingMethod: `PayGateService.${method}`,
      planRequirement: accessRequirement(bodies),
      appConfigs: configs,
      featureFlags: referencedFlags,
    }
  })

  const defaultsByKey = new Map()
  for (const call of allConfigCalls) {
    const existing = defaultsByKey.get(call.key)
    if (existing && (existing.compiledDefault !== call.compiledDefault || existing.compiledDefaultExpression !== call.compiledDefaultExpression)) {
      throw new Error(`conflicting compiled defaults for ${call.key}`)
    }
    defaultsByKey.set(call.key, call)
  }

  const appConfigs = [...configKeys.values()].sort(byCode).map((key) => ({
    key,
    compiledDefault: defaultsByKey.get(key)?.compiledDefault ?? null,
    compiledDefaultExpression: defaultsByKey.get(key)?.compiledDefaultExpression ?? null,
    seededInMigrations: appConfigRows.has(key),
  }))

  const declaredFeatureKeys = new Set(featureKeys.values())
  const featureFlags = [...featureRows.values()]
    .map((row) => ({
      key: row.Key,
      enabled: row.Enabled,
      planRequirement: row.PlanRequirement ?? null,
      declaredInCode: declaredFeatureKeys.has(row.Key),
    }))
    .sort((a, b) => byCode(a.key, b.key))

  return {
    provenance: provenance(inputFiles),
    note: "A live AppConfigs row overrides its compiled default at runtime. This offline artifact does not claim live values.",
    gates,
    appConfigs,
    featureFlags,
  }
}

try {
  const root = parseArguments(process.argv.slice(2))
  const matrix = buildMatrix(root)
  writeFileSync(join(root, "gating-matrix.json"), `${JSON.stringify(matrix, null, 2)}\n`, "utf8")
  process.stdout.write(`Generated ${matrix.gates.length} gates, ${matrix.appConfigs.length} config keys, and ${matrix.featureFlags.length} feature flags.\n`)
} catch (error) {
  process.stderr.write(`gating-matrix: ${error.message}\n`)
  process.exitCode = 1
}
