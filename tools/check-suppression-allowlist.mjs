#!/usr/bin/env node
/**
 * Gate analyzer suppressions under src/ by a CLOSED committed allowlist: an undeclared
 * `#pragma warning disable` or `[SuppressMessage]` fails regardless of its rule id, its file, or the
 * comment beside it.
 *
 * WHY THIS EXISTS, and it is not the TypeScript problem in kind. All five ORBIT descriptors already
 * ship as `DiagnosticSeverity.Error, isEnabledByDefault: true`, so they already fail the build.
 * Nothing here is sitting at `warn`. The hole is the ESCAPE HATCH: one line of
 * `#pragma warning disable` plus a comment turns any of them off, and the comment is the entire
 * requirement.
 *
 * Measured on `main` for thomasluizon/orbit-tickets#228: the 50 `ORBIT0004` sites carried exactly TWO
 * justification strings, character for character, 38 copies of one and 12 of the other, both pointing
 * at a mutable GitHub issue outside the repository that nothing in CI reads. A reviewer looking at the
 * diff saw an identical comment; the only discriminating information was one click away and editable
 * by anyone. Adding a 51st pragma with the same pasted comment passed every check in the repository.
 *
 * The principle, and it belongs in the code rather than in prose:
 *
 *     An escape hatch an agent can write is not an escape hatch. It is the default.
 *
 * That is root CLAUDE.md standard 6 applied to the gates themselves, and it is the call ORB-170
 * already shipped for the repository root: closed set, not open list, so the unlisted case fails by
 * default.
 *
 * GRANULARITY: per file, per rule, with a site count. Line numbers churn on every edit and would make
 * this gate a nuisance that gets weakened; a file plus a count still forces a visible data change the
 * moment a 51st site appears, and it catches the drift case a bare file list would miss.
 *
 * GENERATED CODE IS DECLARED, NOT SKIPPED. The EF Core scaffolder emits
 * `#pragma warning disable 612, 618` around `BuildTargetModel` in every migration designer. Those are
 * exempted by a visible `generatedDirectories` entry carrying its reason, so the exemption is
 * reviewable data in the diff rather than a regex hidden in this file.
 *
 * The per-rule audit verdicts live in the same JSON, under `ruleVerdicts`, for the same reason: a
 * verdict a reviewer has to leave the repository to read is the defect this gate was built to remove.
 */

import { readFileSync, readdirSync, statSync } from "node:fs"
import { dirname, join, relative, resolve, sep } from "node:path"
import { fileURLToPath } from "node:url"

const USAGE = `usage: check-suppression-allowlist.mjs [--root <path>] [--print]

  Fails when an analyzer suppression under src/ is not declared in tools/suppression-allowlist.json.

  --root <path>  repository root (defaults to the parent of this tool's directory)
  --print        print the observed inventory as allowlist-shaped JSON and exit 0, for reseeding
  --help, -h     print this usage and exit 0

  Four ways to fail, all of them a data change away from passing:
    1. a suppression in a file the allowlist does not declare
    2. a rule the allowlist does not declare for that file
    3. a site count that does not match the declared one, in either direction
    4. a declared file or rule that no longer carries any suppression

exit codes: 0 every suppression is declared, 1 the closed set was breached, 2 usage or data error`

if (process.argv.includes("--help") || process.argv.includes("-h")) {
  console.log(USAGE)
  process.exit(0)
}

const fail = (code, message) => {
  console.error(message)
  process.exit(code)
}

let repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..")
let printOnly = false
const positional = process.argv.slice(2)
while (positional.length > 0) {
  const flag = positional.shift()
  if (flag === "--print") {
    printOnly = true
    continue
  }
  if (flag !== "--root" || positional.length === 0) fail(2, `check-suppression-allowlist: invalid arguments: ${process.argv.slice(2).join(" ")}\n\n${USAGE}`)
  repositoryRoot = resolve(positional.shift())
}

const toPosix = (absolutePath) => relative(repositoryRoot, absolutePath).split(sep).join("/")

const csharpFiles = (directory, found = []) => {
  let entries
  try {
    entries = readdirSync(directory, { withFileTypes: true })
  } catch {
    return found
  }
  for (const entry of entries.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0))) {
    const absolute = join(directory, entry.name)
    if (entry.isDirectory()) csharpFiles(absolute, found)
    else if (entry.name.endsWith(".cs")) found.push(absolute)
  }
  return found
}

/**
 * A pragma line can disable SEVERAL rules at once (`#pragma warning disable CS0649, S3459`), so each
 * id is counted separately. Only `disable` is counted: the matching `restore` closes a block that the
 * `disable` already declared, and counting both would double every site.
 *
 * The id list is optional in C#, and that is the bypass this pattern has to see. A bare
 * `#pragma warning disable`, with no ids at all, disables EVERY warning from that point on, which is
 * the broadest suppression the language offers. An earlier version of this regex required at least one
 * id and therefore walked straight past it. It now matches the bare form and reports it under the
 * synthetic id below, so it fails as an undeclared suppression like anything else.
 *
 * Whitespace is legal after the `#`, so `# pragma warning disable` is the same directive. That was a
 * second bypass, verified against a real `net10.0` build with `TreatWarningsAsErrors=true`: the spaced
 * form suppressed the diagnostic while this checker reported zero sites and exited 0. Hence
 * `#[^\S\n]*pragma` rather than `#pragma`, and the internal gaps are whitespace classes for the same
 * reason.
 */
const PRAGMA = /^[^\S\n]*#[^\S\n]*pragma[^\S\n]+warning[^\S\n]+disable\b([^\r\n/]*)/gm
/**
 * The id a bare `#pragma warning disable` is counted under. It is deliberately not a legal C# rule id,
 * so no allowlist entry can ever declare it and the gate always refuses: disabling every warning at
 * once is not a site anybody should be able to justify per-site.
 */
const ALL_WARNINGS = "(all warnings)"
/**
 * DETECTION and EXTRACTION are separate, and that separation is the whole design.
 *
 * Canonical-form matching cannot establish a closed set: any form the extractor fails to recognise
 * reads as "no suppression here" and passes. So the broad pattern below finds every use of the
 * attribute, the strict pattern extracts the rule id from the ones it can prove, and anything found
 * but not extractable FAILS CLOSED with the file named. A form this tool cannot inventory is a form it
 * refuses, never one it ignores.
 *
 * `[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", ...)]` is
 * the shape all seven live attributes take. `Attribute` is optional in C# attribute syntax, so
 * `[SuppressMessageAttribute(...)]` is the same attribute.
 */
const SUPPRESS_MESSAGE = /\bSuppressMessage(?:Attribute)?\s*\(\s*"[^"]*"\s*,\s*"([A-Za-z]+\d+)[:"]/g
/**
 * EVERY mention of the identifier, and this is an INVARIANT rather than a list of spellings.
 *
 * Three review rounds were spent adding one more alias or attribute form each time: the long
 * `Attribute` spelling, a `const string` check id, an ordinary `using` alias, then
 * `global using SM = ...` and `using SM = global::...`. That is an open set defended by guesswork,
 * which is the exact antipattern this whole gate exists to reject, and enumerating C#'s using-alias
 * grammar was never going to terminate: the alias can carry a `global` modifier, a `global::`
 * qualifier, arbitrary whitespace, and can span lines.
 *
 * So the rule is inverted. Every mention of `SuppressMessage` or `SuppressMessageAttribute` under
 * `src/` must be accounted for by an attribute use the strict pattern above could read into a declared
 * site. A surplus mention is REFUSED, whatever produced it: an alias directive in any spelling, a
 * check id that is not a literal, a form nobody has thought of yet. Nothing has to be predicted.
 *
 * Measured on the live tree before adopting it: 7 mentions in 3 files, 7 extracted, so the invariant
 * holds today with no false positive. A mention inside a comment or a string would fail it, which is a
 * fail-CLOSED false positive with a visible remedy rather than a silent pass.
 */
const SUPPRESS_MESSAGE_TOKEN = /\bSuppressMessage(?:Attribute)?\b/g

const pragmaRules = (match) => {
  const ids = match[1]
    .split(",")
    .map((rule) => rule.trim())
    .filter(Boolean)
  return ids.length === 0 ? [ALL_WARNINGS] : ids
}

const countsOf = (body, pattern, extract) => {
  const counts = new Map()
  for (const match of body.matchAll(pattern)) {
    for (const rule of extract(match)) {
      counts.set(rule, (counts.get(rule) ?? 0) + 1)
    }
  }
  return counts
}

const allowlistPath = join(repositoryRoot, "tools", "suppression-allowlist.json")
let allowlist
try {
  allowlist = JSON.parse(readFileSync(allowlistPath, "utf8"))
} catch (error) {
  fail(2, `check-suppression-allowlist: cannot read ${allowlistPath}: ${error.message}`)
}

const isDeclarationMap = (value) =>
  value !== null &&
  typeof value === "object" &&
  !Array.isArray(value) &&
  Object.values(value).every(
    (rules) =>
      rules !== null &&
      typeof rules === "object" &&
      !Array.isArray(rules) &&
      Object.values(rules).every((entry) => Number.isInteger(entry?.count) && entry.count > 0 && typeof entry?.reason === "string" && entry.reason.trim().length > 0),
  )

if (!Array.isArray(allowlist?.generatedDirectories) || !isDeclarationMap(allowlist?.pragmas) || !isDeclarationMap(allowlist?.suppressMessage)) {
  fail(
    2,
    "check-suppression-allowlist: the allowlist must carry generatedDirectories (an array), plus pragmas and suppressMessage, each an object of { <path>: { <rule>: { count, reason } } } with a positive count and a non-empty reason",
  )
}
for (const entry of allowlist.generatedDirectories) {
  if (typeof entry?.path !== "string" || entry.path === "" || typeof entry?.reason !== "string" || entry.reason.trim() === "") {
    fail(2, `check-suppression-allowlist: every generatedDirectories entry needs a path and a reason, got ${JSON.stringify(entry)}`)
  }
}
if (allowlist.ruleVerdicts === null || typeof allowlist.ruleVerdicts !== "object" || Array.isArray(allowlist.ruleVerdicts)) {
  fail(2, "check-suppression-allowlist: ruleVerdicts must be an object keyed by ORBIT rule id")
}
for (const [rule, verdict] of Object.entries(allowlist.ruleVerdicts)) {
  if (typeof verdict?.verdict !== "string" || verdict.verdict.trim() === "" || typeof verdict?.evidence !== "string" || verdict.evidence.trim() === "") {
    fail(2, `check-suppression-allowlist: ruleVerdicts.${rule} needs a verdict and its evidence, so no rule can read "to be decided"`)
  }
}

const generatedPrefixes = allowlist.generatedDirectories.map((entry) => `${entry.path.replace(/\/+$/, "")}/`)
const isGenerated = (path) => generatedPrefixes.some((prefix) => path.startsWith(prefix))

const observed = { pragmas: {}, suppressMessage: {} }
const sourceRoot = join(repositoryRoot, "src")
try {
  statSync(sourceRoot)
} catch {
  fail(2, `check-suppression-allowlist: ${sourceRoot} is not a directory, so this gate would prove nothing`)
}
/**
 * A generated directory is EXEMPTED for the rules it declares, not skipped wholesale. Skipping it
 * would make the `rules` list decorative: every `.cs` file under `Migrations/` would pass, so a new
 * analyzer suppression added inside a migration would need no visible change anywhere. The scaffolder
 * emits `612, 618` and nothing else, so anything else in there is a decision somebody made and it
 * fails like any other undeclared suppression.
 */
const generatedRules = new Map(
  allowlist.generatedDirectories.map((entry) => [`${entry.path.replace(/\/+$/, "")}/`, new Set(Array.isArray(entry.rules) ? entry.rules : [])]),
)
const undeclaredInGenerated = []
/** Forms found but not inventoriable. The gate refuses these rather than reporting zero sites. */
const uninventoriable = []

for (const absolute of csharpFiles(sourceRoot)) {
  const path = toPosix(absolute)
  const body = readFileSync(absolute, "utf8")
  const pragmas = countsOf(body, PRAGMA, pragmaRules)
  const attributes = countsOf(body, SUPPRESS_MESSAGE, (match) => [match[1]])

  const mentions = [...body.matchAll(SUPPRESS_MESSAGE_TOKEN)].length
  const extracted = [...attributes.values()].reduce((total, count) => total + count, 0)
  if (mentions > extracted) {
    uninventoriable.push(
      `${path} mentions SuppressMessage ${mentions} time(s) and only ${extracted} of them read as an attribute with a literal check id. ` +
        "The surplus cannot be inventoried, so it is refused rather than skipped. A using alias in any spelling, a check id that is not a string literal, " +
        "and a mention in a comment or a string all land here. Use the attribute's own name with literal arguments, or remove the mention.",
    )
  }
  const generatedPrefix = [...generatedRules.keys()].find((prefix) => path.startsWith(prefix))
  if (generatedPrefix) {
    const permitted = generatedRules.get(generatedPrefix)
    for (const rule of [...pragmas.keys(), ...attributes.keys()]) {
      if (!permitted.has(rule)) undeclaredInGenerated.push(`${path} suppresses ${rule}, which ${generatedPrefix} does not declare as generated`)
    }
    continue
  }
  if (pragmas.size > 0) observed.pragmas[path] = Object.fromEntries([...pragmas].sort(([left], [right]) => (left < right ? -1 : 1)))
  if (attributes.size > 0) observed.suppressMessage[path] = Object.fromEntries([...attributes].sort(([left], [right]) => (left < right ? -1 : 1)))
}

if (printOnly) {
  const shaped = (counts) =>
    Object.fromEntries(
      Object.entries(counts).map(([path, rules]) => [path, Object.fromEntries(Object.entries(rules).map(([rule, count]) => [rule, { count, reason: "" }]))]),
    )
  console.log(JSON.stringify({ pragmas: shaped(observed.pragmas), suppressMessage: shaped(observed.suppressMessage) }, null, 2))
  process.exit(0)
}

const problems = [...uninventoriable, ...undeclaredInGenerated]
let declaredSites = 0

for (const kind of ["pragmas", "suppressMessage"]) {
  const label = kind === "pragmas" ? "#pragma warning disable" : "[SuppressMessage]"
  for (const [path, rules] of Object.entries(observed[kind])) {
    const declaredRules = allowlist[kind][path]
    if (!declaredRules) {
      problems.push(`${path} carries ${label} for ${Object.keys(rules).join(", ")} and is not in the allowlist. The fix is an allowlist entry with a reason for that site, never another comment.`)
      continue
    }
    for (const [rule, count] of Object.entries(rules)) {
      const declared = declaredRules[rule]
      if (!declared) {
        problems.push(`${path} suppresses ${rule} via ${label} and the allowlist does not declare that rule for it`)
        continue
      }
      if (declared.count !== count) {
        problems.push(`${path} carries ${count} ${rule} ${label} site(s), the allowlist declares ${declared.count}`)
      }
    }
  }
  for (const [path, declaredRules] of Object.entries(allowlist[kind])) {
    const rules = observed[kind][path]
    for (const rule of Object.keys(declaredRules)) {
      declaredSites += declaredRules[rule].count
      if (!rules?.[rule]) problems.push(`the allowlist declares ${rule} for ${path} via ${label}, and no such suppression exists any more; delete the entry`)
    }
  }
}

if (problems.length > 0) {
  console.error(`Suppression allowlist breached: ${problems.length} problem(s).`)
  for (const problem of problems) console.error(`  ${problem}`)
  console.error("\nThe closed set is the point: an unlisted suppression fails whatever its rule id or its comment says.")
  console.error("Reseed the shape with `node tools/check-suppression-allowlist.mjs --print`, then write the reason for each site by hand.")
  process.exit(1)
}

const generatedNote = allowlist.generatedDirectories.map((entry) => entry.path).join(", ")
console.log(`check-suppression-allowlist: ${declaredSites} declared suppression site(s) under src/, all accounted for. Generated code exempted by declaration: ${generatedNote || "none"}.`)
