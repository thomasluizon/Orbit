import { execFileSync } from "node:child_process"
import { cpSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import assert from "node:assert/strict"
import { dirname, join, resolve } from "node:path"
import { after, test } from "node:test"
import { fileURLToPath } from "node:url"

const REPOSITORY_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..")
const TOOL = join(REPOSITORY_ROOT, "tools", "gating-matrix.mjs")
const temporaryRoot = mkdtempSync(join(tmpdir(), "orbit-gating-matrix-"))

after(() => {
  rmSync(temporaryRoot, { recursive: true, force: true })
})

function generate(root = REPOSITORY_ROOT) {
  execFileSync("node", [TOOL, "--root", root], { encoding: "utf8" })
  return readFileSync(join(root, "gating-matrix.json"), "utf8")
}

test("the repository matrix is complete, deterministic, and explicit about runtime configuration", () => {
  const first = generate()
  const firstMatrix = JSON.parse(first)
  const second = generate()
  const secondMatrix = JSON.parse(second)

  assert.equal(second, first)
  assert.equal(secondMatrix.provenance.generatedFrom, firstMatrix.provenance.generatedFrom)
  assert.match(firstMatrix.provenance.generatedFrom, /^[0-9a-f]{12}$/)
  assert.equal(firstMatrix.provenance.baselineRef, null)
  assert.equal(firstMatrix.provenance.baselineSha, null)
  assert.ok(Number.isInteger(firstMatrix.provenance.inputFiles))
  assert.ok(firstMatrix.provenance.inputFiles > 0)

  const interfaceSource = readFileSync(
    join(REPOSITORY_ROOT, "src", "Orbit.Domain", "Interfaces", "IPayGateService.cs"),
    "utf8",
  ).replace(/\/\*[\s\S]*?\*\//g, "").replace(/\/\/.*$/gm, "")
  const interfaceMethods = new Set([...interfaceSource.matchAll(/\bTask(?:<[^;()]+>)?\s+(\w+)\s*\(/g)].map((match) => match[1]))
  assert.equal(firstMatrix.gates.length, interfaceMethods.size)
  assert.deepEqual(new Set(firstMatrix.gates.map((gate) => gate.capability)), interfaceMethods)

  const amendedFlag = firstMatrix.featureFlags.find((flag) => flag.key === "gamification_free_tier")
  assert.deepEqual(amendedFlag, {
    key: "gamification_free_tier",
    enabled: true,
    planRequirement: null,
    declaredInCode: true,
  })

  const configurableGate = firstMatrix.gates.find((gate) => gate.appConfigs.length > 0)
  assert.ok(configurableGate)
  assert.equal(typeof configurableGate.appConfigs[0].key, "string")
  assert.notEqual(configurableGate.appConfigs[0].compiledDefaultExpression, null)
  assert.equal(Object.hasOwn(configurableGate.appConfigs[0], "enforcedValue"), false)
  assert.match(firstMatrix.note, /live AppConfigs row overrides/)

  for (const key of ["RetrospectiveProOnly", "SmartRescheduleProOnly"]) {
    const config = firstMatrix.appConfigs.find((candidate) => candidate.key === key)
    assert.ok(config)
    assert.notEqual(config.compiledDefault, null)
    assert.equal(config.seededInMigrations, false)
  }
})

test("deleting an interface method removes only its generated gate", () => {
  const fixture = join(temporaryRoot, "deletion")
  cpSync(join(REPOSITORY_ROOT, "src"), join(fixture, "src"), {
    recursive: true,
    filter: (source) => statSync(source).isDirectory() || source.endsWith(".cs"),
  })

  const before = JSON.parse(generate(fixture))
  const interfacePath = join(fixture, "src", "Orbit.Domain", "Interfaces", "IPayGateService.cs")
  const interfaceSource = readFileSync(interfacePath, "utf8")
  const declaration = interfaceSource.match(/\s+Task(?:<[^;()]+>)?\s+(\w+)\s*\([^;]+;/)
  assert.ok(declaration)
  writeFileSync(interfacePath, interfaceSource.replace(declaration[0], ""), "utf8")

  const afterMatrix = JSON.parse(generate(fixture))
  assert.equal(afterMatrix.gates.length, before.gates.length - 1)
  assert.deepEqual(
    afterMatrix.gates,
    before.gates.filter((gate) => gate.capability !== declaration[1]),
  )
})

test("the generator carries no sample gate, key, or limit literals", () => {
  const source = readFileSync(TOOL, "utf8")
  assert.equal(source.includes("CanCreateHabits"), false)
  assert.equal(source.includes("FreeMaxHabits"), false)
  assert.equal(source.includes("GoalsProOnly"), false)
  assert.equal(/\b(?:5|10|20|50|500|1000)\b/.test(source), false)
})
