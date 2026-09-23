---
name: unity-lint
description: Run JetBrains InspectCode and Unity Project Auditor against a Unity project, then synthesize the results into a prioritized code-quality and performance report. Use for static analysis, Unity-specific performance checks, style/code-smell review, or before/after validation of code changes.
---

# Unity Lint

Run both JetBrains InspectCode and Unity Project Auditor, then review their outputs as one report.

This skill is **analysis-only by default**. Do not change production code, project settings, packages, `.editorconfig`, `.DotSettings`, or ignore rules unless the user explicitly asks you to fix issues.

## Goals

Prioritize findings in this order:

1. Correctness bugs and likely runtime failures.
2. Unity-specific performance problems, especially code on hot/per-frame paths.
3. Managed allocations / GC pressure.
4. Expensive Unity API usage.
5. Resource, asset, shader, GameObject, and project-setting performance issues reported by Project Auditor.
6. Maintainability problems and code smells.
7. Style and consistency issues.

Do not present every analyzer message as equally important. Deduplicate overlapping findings and prioritize actionable problems.

## Inputs

No arguments are required.  Additional instruction may be included.

## Execution Model

Run the analyzers directly. **Do not spawn subagents merely to run InspectCode, run Project Auditor, parse reports, or produce the primary summary.**

Subagents may be used only in explicit `fix` mode when:

- there are many independent fixes,
- work can be partitioned into non-overlapping files or systems,
- each subagent receives a clearly bounded set of findings, and
- the parent agent performs the final analyzer rerun and validates the combined result.

Never allow multiple subagents to modify the same file concurrently.


## 1. Preflight
All files you created, add a datestamp in the form of YYYY_MM_DD_HH:MM (use the same stamp for all files)

### JetBrains InspectCode

The sln is `2birds.sln`

### Unity Editor

The Unity version is 6000.6.2f1


## 3. Run JetBrains InspectCode

Use the project's existing `.editorconfig` and `.DotSettings` files. Do not override them unless the user specifically requests different rules.

Run solution-wide analysis and emit SARIF:

```text
jb inspectcode "<SOLUTION>" \
  --output="<PROJECT_ROOT>/.lint/<datestamp>/inspectcode_<datestampt>.sarif" \
  --swea \
  --severity=SUGGESTION
```

Use the shell-appropriate line continuation or run it as one line.

If command-line exclusion is useful, exclude only clearly generated/transient output from the report, such as:

```text
**/Library/**
**/Temp/**
**/obj/**
```

Do not broadly exclude `Assets/`, tests, editor code, or first-party packages.

Do not suppress a finding just because it is inconvenient. Existing project analyzer configuration is authoritative.

## 4. Ensure the Project Auditor CLI Bridge Exists

Unity Project Auditor command-line execution requires an Editor method.

The existing project-specific Project Auditor CLI entry point lives at:
```text
Assets/Editor/AgentTools/ProjectAuditorCli.cs
```

This bridge is project tooling, not gameplay code.

## 5. Run Unity Project Auditor

Unity CLI exe info: Unity CLI 1.0.0-beta.9 (lives at `C:\Users\spenc\AppData\Local\Unity\bin\unity.exe`)

Run the matching Unity Editor in batch mode:

```text
"<UNITY_EXE>" \
  -batchmode \
  -quit \
  -projectPath "<PROJECT_ROOT>" \
  -executeMethod AgentTools.ProjectAuditorCli.Run \
  -logFile "<PROJECT_ROOT>/.lint/<datestamp>/project-auditor-<datestamp>.log"
```

Do not use `-ignorecompilererrors`.

### If the Unity project is already open

Unity cannot open the same project in another Editor process in batch mode.

If Project Auditor fails because the project is already open:

1. Do **not** kill or close the user's Unity Editor.
2. Keep the InspectCode results.
3. Mark Project Auditor as `BLOCKED: project open in another Unity Editor`.
4. Tell the user to close that Editor and rerun the skill for the Project Auditor portion.

Do not claim Project Auditor passed if it did not run.

### Other failures

Inspect:

```text
.lint/<datestamp>/project-auditor-<datestamp>.log
```

Distinguish between:

- compile errors,
- missing Project Auditor support/rules,
- incorrect Unity version/path,
- license/startup failure,
- project already open,
- bridge compilation/API mismatch,
- analyzer execution failure.

Report the actual failure rather than treating missing output as zero issues.

## 6. Parse the Results

Read:

```text
.lint/<datestamp>/inspectcode.sarif
.lint/<datestamp>/project-auditor.projectauditor
.lint/<datestamp>/project-auditor.log
```

Project Auditor report files are JSON-backed reports and should be inspected directly.

### InspectCode

Capture at minimum:

- rule/inspection ID,
- severity,
- message,
- file,
- line,
- count of occurrences.

Group related instances rather than flooding the report with duplicates.

Treat first-party code as the primary remediation scope. Clearly separate findings originating in third-party/vendor/generated code when identifiable.

### Project Auditor

Prioritize actual issues rather than informational inventory entries.

Pay particular attention to:

- Critical and Major findings,
- Code issues,
- CPU impact,
- Memory impact,
- allocations / GC,
- APIs used in hot/per-frame code paths,
- asset import problems that affect runtime memory/performance,
- GameObject/prefab performance findings,
- project settings affecting runtime or iteration performance,
- compiler/shader errors and warnings.

Remember that Project Auditor can report false positives or conservative warnings. Validate the call site and usage before asserting that a finding is a real bottleneck.

## 7. Synthesize, Don't Dump

Create:

```text
.lint/<datestamp>/Unity_Lint_Report_<datestamp>.md
```

Use this structure:

```markdown
# Unity Lint Report

## Status
- InspectCode: PASS / ISSUES / BLOCKED / FAILED
- Project Auditor: PASS / ISSUES / BLOCKED / FAILED
- Scope: full / changed
- Fix mode: yes / no

## Summary
- Critical:
- High:
- Medium:
- Low:
- Style-only:

## Highest-Priority Findings

### 1. <short finding>
- Source: InspectCode / Project Auditor / Both
- Severity:
- Location:
- Why it matters:
- Recommended change:

## Performance Findings

## Correctness / Reliability Findings

## Maintainability / Style Findings

## Third-Party or Generated-Code Findings

## Analyzer Notes
- Suppressions/configuration already present
- Tool failures or incomplete coverage
- Potential false positives that require runtime profiling

## Validation
- Commands run
- Analyzer exit/result status
- Output files
```

### Priority mapping

Use judgment rather than mechanically translating analyzer severities.

Default interpretation:

- **Critical** — compile/runtime correctness failure, severe Project Auditor issue, or extremely likely serious runtime impact.
- **High** — Project Auditor Major issue or strong evidence of meaningful performance/correctness impact.
- **Medium** — legitimate but non-urgent performance, maintainability, or correctness concern.
- **Low** — minor cleanup, weak optimization opportunity, or localized code smell.
- **Style-only** — naming, formatting, syntax preference, or other non-behavioral consistency issue.

Never label a stylistic warning as a performance problem.

## 8. Changed Mode

If invoked with `changed`:

1. Determine changed files using Git.
2. Still run both analyzers normally.
3. Put findings touching changed files at the top.
4. Include pre-existing project-wide Critical/High findings separately if they are significant.
5. Do not imply that an issue was introduced by the current change unless the diff supports that conclusion.

## 9. Fix Mode

Only enter fix mode if the invocation explicitly contains `fix`.

Before changing anything, produce the initial report.

Then:

1. Fix Critical/High findings that are clearly valid and within first-party code.
2. Fix Medium findings when the change is small and low-risk.
3. Do not perform broad style churn across unrelated files.
4. Do not change public APIs, serialized field names, prefab/scene contracts, networking behavior, or gameplay semantics solely to satisfy a linter unless necessary and explicitly justified.
5. Do not add suppressions merely to make the report clean.
6. Do not modify third-party/vendor code unless explicitly requested.
7. Rerun InspectCode after changes.
8. Rerun Project Auditor after changes when possible.
9. Update `Unity_Lint_Report.md` with before/after counts and any remaining issues.

If a finding is better validated with runtime profiling, say so rather than inventing a static-analysis fix.

## 10. Strict Mode

If invoked with `strict`, validation fails when any of these remain in first-party code:

- InspectCode `ERROR`,
- InspectCode `WARNING`,
- Project Auditor `Critical`,
- Project Auditor `Major`.

Suggestions/style findings do not fail strict validation unless project configuration already elevates them to warning/error.

Without `strict`, report findings but do not turn ordinary analyzer warnings into an artificial pass/fail gate.

## Final Response

Keep the chat response concise.

Report:

- whether both analyzers successfully ran,
- counts of the highest-priority findings,
- the 3–5 most important issues,
- whether any analyzer was blocked/failed,
- path to `/.lint/<datestamp>/Unity_Lint_Report_<datestamp>).md`.

Do not paste the raw SARIF or entire Project Auditor report into chat.
