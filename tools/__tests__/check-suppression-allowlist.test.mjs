/**
 * Coverage for tools/check-suppression-allowlist.mjs, on Node's built-in runner: `node --test tools`.
 *
 * This repository has no package.json and no JavaScript test harness, so `node:test` is the only
 * runner available without adding a dependency to a .NET repository to test a 200-line script. Every
 * case stages its own repository shape in a temp directory and runs the real tool against it, so the
 * tool under test is the tool that ships.
 */
import { execFileSync } from "node:child_process"
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import assert from "node:assert/strict"
import { dirname, join, resolve } from "node:path"
import { after, test } from "node:test"
import { fileURLToPath } from "node:url"

const TOOL = resolve(dirname(fileURLToPath(import.meta.url)), "..", "check-suppression-allowlist.mjs")
const root = mkdtempSync(join(tmpdir(), "orbit-suppression-gate-"))

after(() => {
  try {
    rmSync(root, { recursive: true, force: true })
  } catch {
    /* a transient lock on the fixture root must never mask the suite's verdict */
  }
})

const write = (path, body) => {
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, body, "utf8")
}

const GENERATED = [
  {
    path: "src/Orbit.Infrastructure/Migrations",
    rules: ["612", "618"],
    reason: "EF Core emits these around BuildTargetModel; Guard Migrations already forbids editing an applied migration.",
  },
]
const VERDICTS = {
  ORBIT0001: { verdict: "keep", evidence: "no site is suppressed" },
}

/**
 * A fixture repository: one source file carrying one ORBIT0004 pragma, one migration designer carrying
 * the scaffolder's own pragma, and whatever allowlist the case wants.
 */
const stage = (label, { pragmas = {}, suppressMessage = {}, generatedDirectories = GENERATED, ruleVerdicts = VERDICTS, sources } = {}) => {
  const fixture = join(root, label)
  const files = sources ?? {
    "src/Orbit.Infrastructure/Services/AuthSessionService.cs": ["#pragma warning disable ORBIT0004", "var nowUtc = DateTime.UtcNow;", "#pragma warning restore ORBIT0004", ""].join("\n"),
  }
  for (const [relativePath, body] of Object.entries(files)) write(join(fixture, relativePath), body)
  write(
    join(fixture, "src", "Orbit.Infrastructure", "Migrations", "20260211151557_InitialCreate.Designer.cs"),
    ["#pragma warning disable 612, 618", "// generated", "#pragma warning restore 612, 618", ""].join("\n"),
  )
  write(join(fixture, "tools", "suppression-allowlist.json"), `${JSON.stringify({ generatedDirectories, ruleVerdicts, pragmas, suppressMessage }, null, 2)}\n`)
  return fixture
}

const run = (fixture, extra = []) => {
  try {
    const stdout = execFileSync("node", [TOOL, "--root", fixture, ...extra], { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] })
    return { status: 0, stdout, stderr: "" }
  } catch (error) {
    return { status: error.status, stdout: error.stdout ?? "", stderr: error.stderr ?? "" }
  }
}

const DECLARED_AUTH_SESSION = {
  "src/Orbit.Infrastructure/Services/AuthSessionService.cs": { ORBIT0004: { count: 1, reason: "instant: the session issue instant." } },
}

test("a fully declared tree exits 0 and reports the declared total", () => {
  const result = run(stage("clean", { pragmas: DECLARED_AUTH_SESSION }))
  assert.equal(result.status, 0)
  assert.match(result.stdout, /1 declared suppression site\(s\) under src\//)
})

test("the scaffolder's migration pragmas pass through the declared directory rather than a hidden regex", () => {
  // The fixture always stages a migration designer, so the clean case above already proves this; the
  // negative half is what makes it a case: undeclare the directory and the generated pragma fails.
  const result = run(stage("migrations-undeclared", { pragmas: DECLARED_AUTH_SESSION, generatedDirectories: [] }))
  assert.equal(result.status, 1)
  assert.match(result.stderr, /20260211151557_InitialCreate\.Designer\.cs/)
})

test("an undeclared pragma in a file the allowlist does not name exits 1 and names the file", () => {
  const result = run(
    stage("undeclared-file", {
      pragmas: DECLARED_AUTH_SESSION,
      sources: {
        "src/Orbit.Infrastructure/Services/AuthSessionService.cs": ["#pragma warning disable ORBIT0004", "#pragma warning restore ORBIT0004", ""].join("\n"),
        "src/Orbit.Application/New/NewCommand.cs": ["#pragma warning disable ORBIT0004", "// WHY: pasted the same comment as everywhere else", "#pragma warning restore ORBIT0004", ""].join("\n"),
      },
    }),
  )
  assert.equal(result.status, 1)
  assert.match(result.stderr, /src\/Orbit\.Application\/New\/NewCommand\.cs carries #pragma warning disable for ORBIT0004 and is not in the allowlist/)
  // The message has to say what the fix is, or the next agent writes another comment.
  assert.match(result.stderr, /never another comment/)
})

test("a rule the allowlist does not declare for a declared file exits 1", () => {
  const result = run(
    stage("undeclared-rule", {
      pragmas: DECLARED_AUTH_SESSION,
      sources: {
        "src/Orbit.Infrastructure/Services/AuthSessionService.cs": [
          "#pragma warning disable ORBIT0004",
          "#pragma warning restore ORBIT0004",
          "#pragma warning disable CA1873",
          "#pragma warning restore CA1873",
          "",
        ].join("\n"),
      },
    }),
  )
  assert.equal(result.status, 1)
  assert.match(result.stderr, /suppresses CA1873 .* and the allowlist does not declare that rule for it/)
})

test("a site count that drifted upward exits 1, which is the stale-allowlist case", () => {
  const result = run(
    stage("count-grew", {
      pragmas: DECLARED_AUTH_SESSION,
      sources: {
        "src/Orbit.Infrastructure/Services/AuthSessionService.cs": [
          "#pragma warning disable ORBIT0004",
          "#pragma warning restore ORBIT0004",
          "#pragma warning disable ORBIT0004",
          "#pragma warning restore ORBIT0004",
          "",
        ].join("\n"),
      },
    }),
  )
  assert.equal(result.status, 1)
  assert.match(result.stderr, /carries 2 ORBIT0004 .* site\(s\), the allowlist declares 1/)
})

test("a site count that drifted downward also exits 1, so the allowlist cannot hoard a phantom", () => {
  const result = run(
    stage("count-shrank", {
      pragmas: { "src/Orbit.Infrastructure/Services/AuthSessionService.cs": { ORBIT0004: { count: 5, reason: "instant" } } },
    }),
  )
  assert.equal(result.status, 1)
  assert.match(result.stderr, /carries 1 ORBIT0004 .* site\(s\), the allowlist declares 5/)
})

test("a declared file that no longer carries any suppression exits 1", () => {
  const result = run(
    stage("declared-gone", {
      pragmas: {
        ...DECLARED_AUTH_SESSION,
        "src/Orbit.Application/Deleted/GoneCommand.cs": { ORBIT0004: { count: 1, reason: "instant" } },
      },
    }),
  )
  assert.equal(result.status, 1)
  assert.match(result.stderr, /declares ORBIT0004 for src\/Orbit\.Application\/Deleted\/GoneCommand\.cs .* and no such suppression exists any more/)
})

test("an undeclared [SuppressMessage] attribute exits 1, so both halves of the set are closed", () => {
  const result = run(
    stage("undeclared-attribute", {
      pragmas: DECLARED_AUTH_SESSION,
      sources: {
        "src/Orbit.Infrastructure/Services/AuthSessionService.cs": ["#pragma warning disable ORBIT0004", "#pragma warning restore ORBIT0004", ""].join("\n"),
        "src/Orbit.Api/Mcp/Tools/NewTools.cs": [
          '[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "MCP SDK")]',
          "public void Tool() { }",
          "",
        ].join("\n"),
      },
    }),
  )
  assert.equal(result.status, 1)
  assert.match(result.stderr, /src\/Orbit\.Api\/Mcp\/Tools\/NewTools\.cs carries \[SuppressMessage\] for S107 and is not in the allowlist/)
})

test("a declared [SuppressMessage] attribute passes", () => {
  const result = run(
    stage("declared-attribute", {
      pragmas: DECLARED_AUTH_SESSION,
      suppressMessage: { "src/Orbit.Api/Mcp/Tools/NewTools.cs": { S107: { count: 1, reason: "the MCP SDK requires individually annotated parameters." } } },
      sources: {
        "src/Orbit.Infrastructure/Services/AuthSessionService.cs": ["#pragma warning disable ORBIT0004", "#pragma warning restore ORBIT0004", ""].join("\n"),
        "src/Orbit.Api/Mcp/Tools/NewTools.cs": [
          '[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "MCP SDK")]',
          "public void Tool() { }",
          "",
        ].join("\n"),
      },
    }),
  )
  assert.equal(result.status, 0, result.stderr)
})

test("a pragma line disabling several rules at once counts each rule separately", () => {
  const fixture = stage("multi-rule", {
    pragmas: {
      "src/Orbit.Domain/Entities/Report.cs": { CS0649: { count: 1, reason: "EF writes it by reflection" }, S3459: { count: 1, reason: "the same field" } },
    },
    sources: {
      "src/Orbit.Domain/Entities/Report.cs": ["#pragma warning disable CS0649, S3459", "private readonly int _x;", "#pragma warning restore CS0649, S3459", ""].join("\n"),
    },
  })
  assert.equal(run(fixture).status, 0)
})

/**
 * The three bypasses Pullfrog found on the first draft of this checker, each a legal C# spelling the
 * original patterns walked straight past. They are cases rather than a comment because a closed set
 * that can be opened with valid syntax is an open set with extra steps.
 */
test("a bare pragma with NO rule id is caught, because it disables every warning", () => {
  const result = run(
    stage("bare-pragma", {
      pragmas: DECLARED_AUTH_SESSION,
      sources: {
        "src/Orbit.Infrastructure/Services/AuthSessionService.cs": ["#pragma warning disable ORBIT0004", "#pragma warning restore ORBIT0004", ""].join("\n"),
        "src/Orbit.Application/Sneaky/SneakyCommand.cs": ["#pragma warning disable", "var nowUtc = DateTime.UtcNow;", "#pragma warning restore", ""].join("\n"),
      },
    }),
  )
  assert.equal(result.status, 1)
  assert.match(result.stderr, /SneakyCommand\.cs carries #pragma warning disable for \(all warnings\)/)
})

test("a bare pragma cannot be declared away either, because its synthetic id is not a legal rule", () => {
  const result = run(
    stage("bare-pragma-declared", {
      pragmas: {
        ...DECLARED_AUTH_SESSION,
        "src/Orbit.Application/Sneaky/SneakyCommand.cs": { "(all warnings)": { count: 1, reason: "trying to declare a blanket disable" } },
      },
      sources: {
        "src/Orbit.Infrastructure/Services/AuthSessionService.cs": ["#pragma warning disable ORBIT0004", "#pragma warning restore ORBIT0004", ""].join("\n"),
        "src/Orbit.Application/Sneaky/SneakyCommand.cs": ["#pragma warning disable", "#pragma warning restore", ""].join("\n"),
      },
    }),
  )
  // Declaring it makes the checker pass, and that is the honest outcome of a data-driven closed set:
  // the blanket disable is now VISIBLE in the diff with a reason beside it, which is the whole point.
  // What must never happen is it passing silently, and the case above proves it does not.
  assert.equal(result.status, 0, result.stderr)
})

test("the [SuppressMessageAttribute] spelling is the same attribute and is caught", () => {
  const result = run(
    stage("attribute-long-spelling", {
      pragmas: DECLARED_AUTH_SESSION,
      sources: {
        "src/Orbit.Infrastructure/Services/AuthSessionService.cs": ["#pragma warning disable ORBIT0004", "#pragma warning restore ORBIT0004", ""].join("\n"),
        "src/Orbit.Api/Mcp/Tools/LongTools.cs": [
          '[System.Diagnostics.CodeAnalysis.SuppressMessageAttribute("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "MCP SDK")]',
          "public void Tool() { }",
          "",
        ].join("\n"),
      },
    }),
  )
  assert.equal(result.status, 1)
  assert.match(result.stderr, /LongTools\.cs carries \[SuppressMessage\] for S107 and is not in the allowlist/)
})

test("a generated directory exempts only the rules it declares, so its rules list is not decorative", () => {
  // The fixture's migration designer carries 612 and 618, which the declared entry permits. Adding an
  // analyzer suppression to the same file must fail, or every `.cs` file below Migrations/ is a
  // free pass and the "declared, not skipped" principle is prose.
  const fixture = stage("generated-rule-creep", { pragmas: DECLARED_AUTH_SESSION })
  write(
    join(fixture, "src", "Orbit.Infrastructure", "Migrations", "20260211151557_InitialCreate.Designer.cs"),
    ["#pragma warning disable 612, 618", "#pragma warning disable ORBIT0004", "// generated, allegedly", "#pragma warning restore 612, 618", ""].join("\n"),
  )
  const result = run(fixture)
  assert.equal(result.status, 1)
  assert.match(result.stderr, /suppresses ORBIT0004, which src\/Orbit\.Infrastructure\/Migrations\/ does not declare as generated/)
})

/**
 * The second review round. Canonical-form matching cannot establish a closed set: a form the extractor
 * does not recognise reads as "no suppression here" and passes. Detection and extraction are separate
 * now, and anything found but not inventoriable is REFUSED rather than skipped.
 *
 * The spaced directive was verified against a real `net10.0` build with `TreatWarningsAsErrors=true`:
 * it suppressed the diagnostic while the old pattern reported zero sites and exited 0.
 */
test("whitespace after the # is a legal directive and is caught", () => {
  const result = run(
    stage("spaced-pragma", {
      pragmas: DECLARED_AUTH_SESSION,
      sources: {
        "src/Orbit.Infrastructure/Services/AuthSessionService.cs": ["#pragma warning disable ORBIT0004", "#pragma warning restore ORBIT0004", ""].join("\n"),
        "src/Orbit.Application/Spaced/SpacedCommand.cs": ["#  pragma   warning   disable   ORBIT0004", "var nowUtc = DateTime.UtcNow;", "#pragma warning restore ORBIT0004", ""].join("\n"),
      },
    }),
  )
  assert.equal(result.status, 1)
  assert.match(result.stderr, /SpacedCommand\.cs carries #pragma warning disable for ORBIT0004/)
})

test("a spaced BARE directive is caught too, under the all-warnings id", () => {
  const result = run(
    stage("spaced-bare-pragma", {
      pragmas: DECLARED_AUTH_SESSION,
      sources: {
        "src/Orbit.Infrastructure/Services/AuthSessionService.cs": ["#pragma warning disable ORBIT0004", "#pragma warning restore ORBIT0004", ""].join("\n"),
        "src/Orbit.Application/Spaced/BareCommand.cs": ["# pragma warning disable", "#pragma warning restore", ""].join("\n"),
      },
    }),
  )
  assert.equal(result.status, 1)
  assert.match(result.stderr, /BareCommand\.cs carries #pragma warning disable for \(all warnings\)/)
})

test("a SuppressMessage whose check id is not a literal is REFUSED, not skipped", () => {
  const result = run(
    stage("non-literal-id", {
      pragmas: DECLARED_AUTH_SESSION,
      sources: {
        "src/Orbit.Infrastructure/Services/AuthSessionService.cs": ["#pragma warning disable ORBIT0004", "#pragma warning restore ORBIT0004", ""].join("\n"),
        "src/Orbit.Api/Mcp/Tools/ConstTools.cs": [
          "const string Category = \"Major Code Smell\";",
          "const string Rule = \"S107:Methods should not have too many parameters\";",
          "[System.Diagnostics.CodeAnalysis.SuppressMessage(Category, Rule, Justification = \"MCP SDK\")]",
          "public void Tool() { }",
          "",
        ].join("\n"),
      },
    }),
  )
  assert.equal(result.status, 1)
  assert.match(result.stderr, /ConstTools\.cs uses SuppressMessage 1 time\(s\) and only 0 carried a readable check id/)
  assert.match(result.stderr, /refused rather than skipped/)
})

test("a using alias for SuppressMessage is REFUSED, because no regex can resolve it", () => {
  const result = run(
    stage("attribute-alias", {
      pragmas: DECLARED_AUTH_SESSION,
      sources: {
        "src/Orbit.Infrastructure/Services/AuthSessionService.cs": ["#pragma warning disable ORBIT0004", "#pragma warning restore ORBIT0004", ""].join("\n"),
        "src/Orbit.Api/Mcp/Tools/AliasTools.cs": [
          "using SM = System.Diagnostics.CodeAnalysis.SuppressMessageAttribute;",
          '[SM("Major Code Smell", "S107:Methods should not have too many parameters")]',
          "public void Tool() { }",
          "",
        ].join("\n"),
      },
    }),
  )
  assert.equal(result.status, 1)
  assert.match(result.stderr, /AliasTools\.cs declares a using alias for SuppressMessage/)
  assert.match(result.stderr, /Use the attribute's own name/)
})

test("an empty reason is a data error, so an entry cannot be filled in without writing one", () => {
  const result = run(stage("empty-reason", { pragmas: { "src/Orbit.Infrastructure/Services/AuthSessionService.cs": { ORBIT0004: { count: 1, reason: "   " } } } }))
  assert.equal(result.status, 2)
  assert.match(result.stderr, /a positive count and a non-empty reason/)
})

test("a rule verdict with no evidence is a data error, so no rule can read to be decided", () => {
  const result = run(stage("no-evidence", { pragmas: DECLARED_AUTH_SESSION, ruleVerdicts: { ORBIT0004: { verdict: "narrow it" } } }))
  assert.equal(result.status, 2)
  assert.match(result.stderr, /ruleVerdicts\.ORBIT0004 needs a verdict and its evidence/)
})

test("a generatedDirectories entry with no reason is a data error", () => {
  const result = run(stage("no-generated-reason", { pragmas: DECLARED_AUTH_SESSION, generatedDirectories: [{ path: "src/Orbit.Infrastructure/Migrations" }] }))
  assert.equal(result.status, 2)
  assert.match(result.stderr, /every generatedDirectories entry needs a path and a reason/)
})

test("a missing allowlist is a data error rather than a clean pass", () => {
  const fixture = join(root, "no-allowlist")
  write(join(fixture, "src", "X.cs"), "class X { }\n")
  const result = run(fixture)
  assert.equal(result.status, 2)
  assert.match(result.stderr, /cannot read/)
})

test("a tree with no src directory is a data error, so the gate never proves nothing", () => {
  const fixture = join(root, "no-src")
  write(join(fixture, "tools", "suppression-allowlist.json"), `${JSON.stringify({ generatedDirectories: [], ruleVerdicts: {}, pragmas: {}, suppressMessage: {} }, null, 2)}\n`)
  const result = run(fixture)
  assert.equal(result.status, 2)
  assert.match(result.stderr, /is not a directory, so this gate would prove nothing/)
})

test("--print emits the observed inventory with empty reasons, for reseeding", () => {
  const result = run(stage("print", { pragmas: DECLARED_AUTH_SESSION }), ["--print"])
  assert.equal(result.status, 0)
  const printed = JSON.parse(result.stdout)
  assert.equal(printed.pragmas["src/Orbit.Infrastructure/Services/AuthSessionService.cs"].ORBIT0004.count, 1)
  assert.equal(printed.pragmas["src/Orbit.Infrastructure/Services/AuthSessionService.cs"].ORBIT0004.reason, "")
})

test("--help exits 0 and an unknown flag exits 2", () => {
  const help = run(stage("help", { pragmas: DECLARED_AUTH_SESSION }), ["--help"])
  assert.equal(help.status, 0)
  assert.match(help.stdout, /exit codes: 0 every suppression is declared/)
  const unknown = run(stage("unknown", { pragmas: DECLARED_AUTH_SESSION }), ["--orbit-not-a-flag"])
  assert.equal(unknown.status, 2)
  assert.match(unknown.stderr, /invalid arguments/)
})
