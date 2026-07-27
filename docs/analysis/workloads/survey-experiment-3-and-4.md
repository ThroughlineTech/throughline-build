# Operation: survey-app-build

Build a self-contained survey-taking web app: React + TypeScript + Vite, localStorage for persistence, no backend. Survey-takers answer multi-question surveys with branching logic; admins define survey templates and view aggregate results with simple visualizations. Eight briefs across two plans, ranging from straightforward scaffolding to one deliberately convoluted feature that requires real design work.

This is the front-loaded revision. Each brief's `Inputs:` is a read-map: it names the exact files and exported symbols the brief consumes, with signatures inlined, so the implement worker conforms to the contract instead of greping the tree to rediscover it. Brief 01 fixes the type and repository contract; every later brief's `Inputs:` points at the precise symbols its predecessors produced. High-risk briefs carry a `Design:` block (so the design isn't re-derived turn-by-turn) and a `Failure modes:` block (which is also the reviewer's rubric). Every brief ends in a `Verify:` block of exact commands - the deterministic check a gate runs, distinct from the prose Acceptance checkboxes.

## Why this exists

A self-contained frontend exercise covering representative feature work: project scaffolding, typed data models, localStorage persistence, multi-page navigation, form handling, list/detail views, aggregate computation, basic data visualization, and one piece of genuinely tricky design work (a conditional logic engine with its own small expression DSL). The app is intentionally constrained: no backend, no auth provider, no external services. Everything runs in the browser against localStorage. The constraint forces design clarity and makes the build hermetic.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | Survey-taking core | - | M |
| B | Admin, visualization, branching logic | A | L |

Plan A delivers a working survey-taking experience: scaffold, data model, take-survey UI, review-my-responses page. Plan B layers admin templates, aggregate results, a chart, and the convoluted brief (conditional logic engine). Plan B's dependency on Plan A's data model rides the plan-level A edge; brief-level Deps below stay within their own plan.

## Plan A: Survey-taking core

### Goal

A user can take a multi-question survey and review their past responses. The data model and storage layer are typed and isolated from the UI so later briefs can extend without rewriting.

### Briefs

| # | Slug | Intent | Deps | Effort | Files |
|---|------|--------|------|--------|-------|
| 01 | vite-scaffold | Initialize the Vite + React + TypeScript project with Vitest, routing, and a working CI build | - | S | package.json, vite.config.ts, tsconfig.json, src/main.tsx, src/App.tsx, src/index.css, .gitignore, README.md, vitest.config.ts |
| 02 | survey-data-model | Define typed survey + question + response records; localStorage repository | 01 | M | src/data/types.ts, src/data/repository.ts, src/data/seed.ts, src/data/__tests__/repository.test.ts |
| 03 | take-survey | Survey-taking page: one question per page, Next/Back, progress, persists on Submit | 02 | M | src/pages/TakeSurvey.tsx, src/pages/__tests__/TakeSurvey.test.tsx, src/components/QuestionRenderer.tsx, src/components/ProgressBar.tsx |
| 04 | my-responses | "My responses" page: lists prior submissions for the current session, shows answers per response | 03 | S | src/pages/MyResponses.tsx, src/pages/ResponseDetail.tsx, src/components/Header.tsx, src/pages/__tests__/MyResponses.test.tsx, src/pages/__tests__/ResponseDetail.test.tsx |

### Briefs - detail

#### Brief 01: vite-scaffold

Goal: Initialize the project with Vite, React 18, TypeScript, React Router, Vitest, and a minimal "Hello survey" homepage. The build must pass `npm run build` cleanly and `npm test` with one trivial passing test.

Design risk: low

Inputs:
- Node 20+ available locally.
- Vite's React-TypeScript template (`npm create vite@latest survey-app -- --template react-ts`). This template's default `build` script is `tsc -b && vite build`, so the build already typechecks.
- Greenfield brief: there is no prior code to read. The front-load here is the exact dependency set and config below - decide nothing that the brief already pins.

Outputs:
- `package.json` with dependencies `react`, `react-dom`, `react-router-dom`; dev dependencies `vite`, `@vitejs/plugin-react`, `typescript`, `@types/react`, `@types/react-dom`, `vitest`, `@testing-library/react`, `@testing-library/jest-dom`, `jsdom`. Scripts: `dev`, `build` (`tsc -b && vite build`), `test` (`vitest run`), and add `typecheck` (`tsc -b --noEmit`) - later briefs' Verify blocks call `npm run typecheck`. Use `tsc -b --noEmit`, NOT `tsc --noEmit`: the tsconfig below uses project references, and plain `--noEmit` does not follow references, so it type-checks nothing and the gate is vacuous.
- `vite.config.ts` with the React plugin and Vitest config pointing at jsdom, plus `test: { exclude: ['**/node_modules/**', '**/.worktrees/**'] }` so a stray git worktree left under `.worktrees/` cannot poison a root `npm test`.
- `tsconfig.json` with strict mode enabled.
- `src/main.tsx` mounting `App` inside a `BrowserRouter`.
- `src/App.tsx` with a top-level `<Routes>`: `/` renders a "Survey app" placeholder; `/take` renders a "Take a survey" placeholder so routing is exercised.
- `src/index.css` with minimal global styles (system font stack). No CSS framework.
- `vitest.config.ts` (or inline in vite.config.ts) wiring jsdom + testing-library setup.
- `src/App.test.tsx`: renders App and asserts "Survey app" appears.
- `README.md` describing the app and the commands (`npm install`, `npm run dev`, `npm run build`, `npm test`, `npm run typecheck`).
- `.gitignore` covering node_modules, dist, .vscode.

Acceptance:
- [ ] `npm install` completes without errors
- [ ] `npm run build` produces `dist/` without errors
- [ ] `npm run dev` serves `/` showing "Survey app" and `/take` showing "Take a survey"
- [ ] `npm test` reports one passing test
- [ ] TypeScript strict mode is on and the build has zero TS errors
- [ ] `npm run typecheck` runs `tsc -b --noEmit` and actually type-checks the referenced projects (a `tsc --noEmit` that checks nothing is not acceptable)
- [ ] `npm test` from the repo root excludes `node_modules` and `.worktrees`
- [ ] no CSS framework (no Tailwind, MUI, Bootstrap) and no state library (no Redux, Zustand, Recoil)

Verify:
- `npm install`
- `npm run build`
- `npm test`

Notes: keep dependencies minimal; survey and admin features land later. Resist any UI or styling framework. The `typecheck` script must be `tsc -b --noEmit` (build mode), not `tsc --noEmit` - with a project-references tsconfig the latter follows nothing and silently passes everything.

OOS:
- Do not add Tailwind, MUI, Bootstrap, or any CSS framework
- Do not add Redux, Zustand, or any state management library
- Do not add a backend, mock API, or service worker
- Do not add authentication or session management
- Do not add a database wrapper or ORM (localStorage is wrapped in B02)

#### Brief 02: survey-data-model

Goal: Define the typed survey, question, and response shapes, and a localStorage-backed repository. Seed a starter survey so the app shows something on first load. This brief fixes the contract every later brief reads - get the signatures exact.

Design risk: low

Inputs (read these; do not rediscover them):
- The scaffold from B01: `src/App.tsx` (mounts inside `BrowserRouter`; calls happen on mount), `src/main.tsx`. No existing data layer yet.
- `crypto.randomUUID()` is available in the browser and in the Vitest jsdom environment - use it for ids.
- localStorage stores strings only: `JSON.stringify` on write, `JSON.parse` wrapped in try/catch on read.

Preload:
- src/App.tsx
- src/main.tsx

Outputs:
- `src/data/types.ts` defining the contract (this is what B03-B08 import):
  - `Survey = { id: string; title: string; description?: string; questions: Question[]; createdAt: string }`
  - `Question` = discriminated union over `kind`, each member with `{ id: string; prompt: string }` plus:
    - `single_choice`: `{ kind: 'single_choice'; options: string[] }`
    - `multiple_choice`: `{ kind: 'multiple_choice'; options: string[] }`
    - `short_text`: `{ kind: 'short_text'; maxLength?: number }`
    - `long_text`: `{ kind: 'long_text'; maxLength?: number }`
    - `scale`: `{ kind: 'scale'; min: number; max: number; minLabel?: string; maxLabel?: string }`
  - `Answer` = union matching kinds: single_choice -> `string`; multiple_choice -> `string[]`; short_text/long_text -> `string`; scale -> `number`
  - `Response = { id: string; surveyId: string; answers: Record<string, Answer>; submittedAt: string }` (answers keyed by question id)
- `src/data/repository.ts` exporting exactly: `getAllSurveys(): Survey[]`, `getSurvey(id: string): Survey | undefined`, `saveSurvey(survey: Survey): void` (upsert by id), `deleteSurvey(id: string): void`, `getAllResponses(): Response[]`, `getResponsesForSurvey(surveyId: string): Response[]`, `saveResponse(response: Response): void` (upsert by id), `deleteResponse(id: string): void`. localStorage keys: `survey-app:surveys`, `survey-app:responses` (each a JSON array).
- `src/data/seed.ts` exporting `seedIfEmpty(): void` - if no surveys exist, insert one starter survey with ~5 mixed-kind questions. Call it from `App.tsx` on mount.
- `src/data/__tests__/repository.test.ts`: survey roundtrip, response roundtrip, delete, `getResponsesForSurvey` filtering, empty-localStorage returns `[]` (not undefined), corrupt JSON handled gracefully (return `[]` + log).

Acceptance:
- [ ] all five kinds defined as a discriminated union; `Answer` matches kinds exactly
- [ ] all eight repository functions implemented as plain functions (not a class)
- [ ] `seedIfEmpty()` inserts the starter survey only when localStorage is empty (idempotent)
- [ ] corrupt localStorage payload does not crash; repository returns `[]`
- [ ] tests pass, including the corruption path

Verify:
- `npm run typecheck`
- `npm test -- src/data/__tests__/repository.test.ts`

Notes: this is the brief whose Outputs everything downstream front-loads against, so the exact signatures above are load-bearing. Plain functions, no validation on save (B05's editor validates).

OOS:
- Do not add IndexedDB or any other storage layer; localStorage only
- Do not add schema migration logic (flagged so it does not creep)
- Do not implement validation on save (B05 owns it)
- Do not export the repository as a class

#### Brief 03: take-survey

Goal: Implement the survey-taking page at `/take/:surveyId`: load the survey, show one question at a time with Next/Back and a progress bar, persist a Response on Submit.

Design risk: low

Inputs (read these; do not rediscover them):
- From B02 `src/data/types.ts`: `Survey`, `Question` (union over `kind`), `Answer`, `Response`. The answer value type depends on kind: single_choice -> string, multiple_choice -> string[], text -> string, scale -> number.
- From B02 `src/data/repository.ts`: `getSurvey(id): Survey | undefined`, `saveResponse(response: Response): void`.
- React Router `useParams` (read `:surveyId`) and `useNavigate` (go to `/responses/:responseId` on Submit).
- From B01 `src/App.tsx`: the `<Routes>` block to add the `/take/:surveyId` route into.

Preload:
- src/data/types.ts
- src/data/repository.ts
- src/App.tsx

Outputs:
- `src/pages/TakeSurvey.tsx`: title + description; a `ProgressBar` showing "Question N of M"; one `QuestionRenderer` for the current question; Back (disabled on first) and Next (becomes Submit on last). Submit builds a `Response` (answers keyed by question id), calls `saveResponse`, navigates to `/responses/:responseId`. In-progress answers live in component state; persist only on Submit.
- `src/components/QuestionRenderer.tsx`: switch on `Question.kind` - single_choice -> radio group, multiple_choice -> checkbox group, short_text -> input, long_text -> textarea, scale -> radio/slider over `min..max` with endpoint labels.
- `src/components/ProgressBar.tsx`: horizontal bar showing percent complete.
- `src/App.tsx` updated: add route `/take/:surveyId` -> TakeSurvey; a placeholder `/responses/:responseId` route returning "Response saved" (B04 builds the real page); a "Take the starter survey" link from `/`.
- `src/pages/__tests__/TakeSurvey.test.tsx`: renders first question, Next advances, Back retreats, answers persist across Back/Next, Submit creates a Response, Submit navigates to `/responses/:responseId`.

Acceptance:
- [ ] all five kinds render correctly
- [ ] answers are preserved navigating Back then Next (no reset on remount)
- [ ] progress bar reflects current position
- [ ] Submit creates a `Response` with answers keyed by question id and navigates to `/responses/:responseId`
- [ ] tests pass

Verify:
- `npm run typecheck`
- `npm test -- src/pages/__tests__/TakeSurvey.test.tsx`

Notes: local component state for in-progress answers; persist only on Submit (no autosave). Closing the tab mid-survey loses the response - acceptable for v1.

OOS:
- Do not implement autosave or draft persistence
- Do not implement conditional logic between questions (B08)
- Do not implement question randomization or shuffle
- Do not implement file upload or media question kinds
- Do not add an "are you sure you want to leave" warning

#### Brief 04: my-responses

Goal: Two pages: `/responses` lists prior responses for the current localStorage; `/responses/:responseId` shows one response with all answers, read-only.

Design risk: low

Inputs (read these; do not rediscover them):
- From B02 `src/data/repository.ts`: `getAllResponses(): Response[]`, `getResponsesForSurvey(surveyId)`, `getSurvey(id): Survey | undefined`.
- From B02 `src/data/types.ts`: `Response` (`{ id, surveyId, answers: Record<string, Answer>, submittedAt }`), `Survey`, `Question`, `Answer`.
- From B03: TakeSurvey navigates to `/responses/:responseId` on Submit; B03 left a placeholder route there to replace.
- From B01 `src/App.tsx`: the `<Routes>` block.

Preload:
- src/data/repository.ts
- src/data/types.ts
- src/App.tsx

Outputs:
- `src/pages/MyResponses.tsx` (list): each response shows survey title, submission date, link to detail; sorted by `submittedAt` descending. Empty state: "No responses yet. Take a survey to get started."
- `src/pages/ResponseDetail.tsx` (detail): survey title, submission date, each question + its answer, read-only. A question that no longer exists in the current survey renders its stored answer under a "(question removed)" heading (the response does not embed a survey snapshot - it renders against the current survey).
- `src/components/Header.tsx`: top-level header with "Home" / "My responses" links, rendered above `<Routes>` in App.tsx.
- `src/App.tsx` updated: real `/responses` and `/responses/:responseId` routes (replace B03's placeholder); mount `Header`.
- `src/pages/__tests__/MyResponses.test.tsx`: empty state, single response, multiple sorted by date descending.
- `src/pages/__tests__/ResponseDetail.test.tsx`: all question/answer pairs render, missing answer handled (survey edited after save), friendly not-found message when responseId is unknown.

Acceptance:
- [ ] list shows all responses ordered by `submittedAt` descending
- [ ] detail renders every current-survey question with the user's answer beside it
- [ ] an answer to a now-deleted question still shows under "(question removed)"
- [ ] unknown responseId shows a friendly message with a link back to the list
- [ ] header navigation works from any route
- [ ] tests pass

Verify:
- `npm run typecheck`
- `npm test -- src/pages/__tests__/MyResponses.test.tsx src/pages/__tests__/ResponseDetail.test.tsx`

Notes: only B02 types and repository functions; no new data concepts. The "(question removed)" case is the one wrinkle - the response is rendered against the current survey, a deliberate v1 simplification.

OOS:
- Do not implement editing or re-submitting a response
- Do not implement response deletion from the UI
- Do not embed a survey snapshot in the response

## Plan B: Admin, visualization, branching logic

### Goal

Admins can define survey templates, view aggregate results with a basic chart, and attach conditional logic rules to questions. The conditional logic engine is the deliberately convoluted brief - real design work, real edge cases.

### Briefs

| # | Slug | Intent | Deps | Effort | Files |
|---|------|--------|------|--------|-------|
| 05 | admin-surveys | Admin CRUD for survey templates: list page + create/edit form with add/remove/reorder questions | - | L | src/pages/admin/AdminSurveyList.tsx, src/pages/admin/AdminSurveyEdit.tsx, src/components/admin/QuestionEditor.tsx, src/pages/admin/__tests__/AdminSurveyList.test.tsx, src/pages/admin/__tests__/AdminSurveyEdit.test.tsx |
| 06 | admin-results | Aggregate results page: counts and distributions per question for a chosen survey | 05 | M | src/pages/admin/AdminResults.tsx, src/pages/admin/__tests__/AdminResults.test.tsx, src/data/aggregate.ts, src/data/__tests__/aggregate.test.ts |
| 07 | results-chart | Bar chart visualization for closed-question distributions on the results page | 06 | S | src/components/ResultsChart.tsx, src/pages/admin/AdminResults.tsx, src/components/__tests__/ResultsChart.test.tsx |
| 08 | conditional-logic-engine | Expression DSL, parser, evaluator, admin editor, runtime integration, and delete/reorder reference migration | 05 | L | src/logic/grammar.ts, src/logic/parser.ts, src/logic/evaluator.ts, src/logic/types.ts, src/logic/migrate.ts, src/components/admin/RuleEditor.tsx, src/pages/TakeSurvey.tsx (updated), src/components/admin/QuestionEditor.tsx (updated), src/data/types.ts (updated), src/logic/__tests__/parser.test.ts, src/logic/__tests__/evaluator.test.ts, src/logic/__tests__/migrate.test.ts, src/logic/__tests__/integration.test.ts |

Plan B depends on Plan A's data model (B02) at the plan level; brief Deps above reference same-plan briefs only.

### Briefs - detail

#### Brief 05: admin-surveys

Goal: An admin can list, create, edit, and delete survey templates. The editor adds, removes, and reorders questions and edits each question's prompt and kind-specific fields.

Design risk: low

Inputs (read these; do not rediscover them):
- From B02 `src/data/types.ts`: `Survey`, `Question` (the five kinds and their fields - options for choice kinds; min/max/labels for scale), `Response`.
- From B02 `src/data/repository.ts`: `getAllSurveys()`, `getSurvey(id)`, `saveSurvey(survey)` (upsert), `deleteSurvey(id)`, `getResponsesForSurvey(surveyId)` (for the response count on each row).
- From B04 `src/components/Header.tsx`: add an "Admin" link; from B01 `src/App.tsx`: the `<Routes>` block.
- `crypto.randomUUID()` for new survey and question ids.

Preload:
- src/data/types.ts
- src/data/repository.ts
- src/components/Header.tsx
- src/App.tsx

Outputs:
- `src/pages/admin/AdminSurveyList.tsx` at `/admin/surveys`: each survey row shows title, question count, response count (`getResponsesForSurvey(survey.id).length`), Edit / Delete / View-results buttons, and a "New survey" button. Delete uses `confirm()` then `deleteSurvey` and refreshes.
- `src/pages/admin/AdminSurveyEdit.tsx` at `/admin/surveys/:id` (`:id === 'new'` means create): the editor. Validation - survey needs a title; each question needs a prompt; choice questions need >= 2 options; scale needs min < max. Save disabled until valid; on save calls `saveSurvey` and returns to the list.
- `src/components/admin/QuestionEditor.tsx`: one instance per question - kind selector, prompt input, kind-specific fields, and up/down reorder buttons (first/last disabled appropriately). Changing kind resets the kind-specific fields.
- `src/App.tsx` updated: routes for the admin pages.
- Tests: list shows surveys, list empty state, create-new saves a valid survey, edit loads and saves, delete removes, validation blocks an invalid save.

Acceptance:
- [ ] list shows all surveys with question and response counts
- [ ] editor adds, removes, and reorders questions and supports all five kinds
- [ ] validation prevents saving an invalid survey
- [ ] delete confirms and removes
- [ ] tests pass

Verify:
- `npm run typecheck`
- `npm test -- src/pages/admin/__tests__/AdminSurveyList.test.tsx src/pages/admin/__tests__/AdminSurveyEdit.test.tsx`

Notes: functional clarity over polish; browser `confirm()` is fine for delete. The reorder buttons here are the surface B08's reference-migration hooks into - keep the reorder operation a single move (from index, to index), not a free-form sort, so B08 can derive the permutation.

OOS:
- Do not implement drag-and-drop reordering (up/down buttons only)
- Do not preserve options when changing a question's kind (kind change resets fields)
- Do not implement undo/redo
- Do not implement survey import/export

#### Brief 06: admin-results

Goal: An admin picks a survey and sees aggregate results: total responses, per-question distributions for closed questions, sample text answers for open questions.

Design risk: low

Inputs (read these; do not rediscover them):
- From B02 `src/data/types.ts`: `Question`, `Response` (`answers: Record<string, Answer>`), `Answer` (single_choice -> string, multiple_choice -> string[], scale -> number, text -> string).
- From B02 `src/data/repository.ts`: `getSurvey(id)`, `getResponsesForSurvey(surveyId): Response[]`.
- From B05 `src/pages/admin/AdminSurveyList.tsx`: add a "View results" link per row to `/admin/surveys/:id/results`.

Preload:
- src/data/types.ts
- src/data/repository.ts
- src/pages/admin/AdminSurveyList.tsx

Outputs:
- `src/data/aggregate.ts`: pure functions over `Response[]`, e.g. `aggregateClosedQuestion(question, responses): { option: string; count: number; pct: number }[]` (multiple_choice: each option's count = responses that selected it; pct of total responses) and `aggregateScale(question, responses): { mean: number; median: number; stddev: number; counts: Record<number, number> }`. No React in this file.
- `src/data/__tests__/aggregate.test.ts`: each function with hand-built fixtures incl. zero responses, all responses skipping a question, all choosing the same option, scale mode at min, scale mode at max.
- `src/pages/admin/AdminResults.tsx` at `/admin/surveys/:id/results`: title, total response count, a per-question section - single/multiple_choice: counts + percentages; scale: per-value counts, mean/median/stddev; text: count + a "show first 10 responses" toggle revealing up to 10 verbatim answers. "Back to surveys" link. Empty state: "No responses yet for this survey."

Acceptance:
- [ ] aggregate functions are pure and unit-tested
- [ ] page renders correctly for each question kind
- [ ] mean/median/stddev correct for scale questions
- [ ] empty state renders when no responses exist
- [ ] tests pass

Verify:
- `npm run typecheck`
- `npm test -- src/data/__tests__/aggregate.test.ts src/pages/admin/__tests__/AdminResults.test.tsx`

Notes: keep computation in pure functions, separate from rendering, so B07's chart and any future tool reuse them.

OOS:
- Do not implement filtering responses by date or any dimension
- Do not implement cross-survey comparison
- Do not implement CSV export
- Do not analyze text content (no sentiment, no clustering)

#### Brief 07: results-chart

Goal: A bar chart visualization for closed-question distributions on the results page. SVG-based, no chart library.

Design risk: low

Inputs (read ONLY these; the rest of the tree is out of scope for this brief - do not glob it):
- From B06 `src/data/aggregate.ts`: the aggregate outputs feeding the chart (`{ option, count, pct }[]` for closed questions; `counts: Record<number, number>` for scale).
- From B06 `src/pages/admin/AdminResults.tsx`: the per-question sections to slot the chart into (above the existing numeric text, which stays for precise numbers).
- From B06 `src/pages/admin/__tests__/AdminResults.test.tsx`: mirror this test's setup (render helper, mock pattern) when writing `ResultsChart.test.tsx` - do not re-derive the test idiom.
- Scope: this brief touches ONLY `AdminResults.tsx` and the new `ResultsChart.tsx` + its test. It does not touch routing, `Header`, `ProgressBar`, `TakeSurvey`, config, or the data layer - do not read them.
- Harness/config are the standard B01 setup (vitest + jsdom + testing-library, `src/setupTests.ts`; standard Vite config) and do not change - do not read `setupTests.ts`, `vite.config.ts`, or `package.json`.

Preload:
- src/data/aggregate.ts
- src/pages/admin/AdminResults.tsx
- src/pages/admin/__tests__/AdminResults.test.tsx

Outputs:
- `src/components/ResultsChart.tsx`: takes `{ data: { label: string; value: number }[]; maxValue?: number }`, renders a horizontal SVG bar chart - each bar labeled with the option name on the left and count + percentage on the right; width fills container; bar row height fixed (~32px); total height scales with row count. Each bar has an `aria-label`; the chart has a role and label.
- `src/pages/admin/AdminResults.tsx` updated: use `ResultsChart` for single_choice and multiple_choice sections; a small vertical-bar histogram variant for scale.
- `src/components/__tests__/ResultsChart.test.tsx`: renders for sample data, empty data array, all-zero values, single-bar dataset.

Acceptance:
- [ ] chart renders as SVG with no external chart library
- [ ] readable for 3-10 bars
- [ ] bars have aria-labels; chart has a role and label
- [ ] tests pass

Verify:
- `npm run typecheck`
- `npm test -- src/components/__tests__/ResultsChart.test.tsx`
- `npm run build`

Notes: hand-rolled SVG is faster than a chart library for this scope. Resist Recharts/Chart.js/D3 unless the design needs more than horizontal bars and a small histogram.

OOS:
- Do not add a chart library (Recharts, Chart.js, D3, Plotly, Victory)
- Do not implement chart-type switching (bar / histogram only)
- Do not implement chart export to PNG
- Do not implement animation or transitions

#### Brief 08: conditional-logic-engine

Goal: Survey designers attach display rules to questions ("show q4 only if `q3 == 'yes' AND q5 != 'maybe'`") via a small expression DSL. Implement the grammar, parser, evaluator, admin editor surface, runtime integration with take-survey, and - the part that has historically failed here - correct migration of the positional `qN` references when an admin deletes or reorders questions. This is the deliberate stew-on-it brief; several wrong implementations look right.

Design risk: high

Inputs (read these; do not rediscover them):
- From B02 `src/data/types.ts`: `Survey`, `Question` (extend it here with `displayRule?: string`), `Answer` (single_choice -> string, multiple_choice -> string[], scale -> number, text -> string). The evaluator coerces against these.
- From B03 `src/pages/TakeSurvey.tsx`: the question-iteration site - one question per page, answers in component state, Next/Back. This is where a question is skipped when its rule evaluates false, and where re-evaluation must run on every answer change including back-navigation.
- From B05 `src/components/admin/QuestionEditor.tsx`: where questions are added, deleted, and reordered (reorder is a single from-index/to-index move per B05). The delete and move handlers are where migration fires.
- `parse` and `evaluate` are produced in this brief; `RuleEditor` uses `parse` for live status, the runtime uses both.

Grammar (write into `src/logic/grammar.ts` as a doc comment + exported BNF string):
```
expr        := or_expr
or_expr     := and_expr ("OR" and_expr)*
and_expr    := not_expr ("AND" not_expr)*
not_expr    := "NOT" not_expr | comparison
comparison  := primary (("==" | "!=" | "<" | "<=" | ">" | ">=" | "CONTAINS" | "NOT_CONTAINS") primary)?
primary     := question_ref | literal | "(" expr ")" | function_call
question_ref := "q" digit+          // references the question at that index
literal     := "'" chars "'" | number | "true" | "false"
function_call := ("ANSWERED" | "COUNT_SELECTED" | "LENGTH") "(" question_ref ")"
```

Design (carry this - do not re-derive it):
- AST node types (in `src/logic/types.ts`): `BinaryOp { op: 'AND'|'OR'; left; right }`, `UnaryOp { op: 'NOT'; operand }`, `Comparison { op: '=='|'!='|'<'|'<='|'>'|'>='|'CONTAINS'|'NOT_CONTAINS'; left; right }`, `QuestionRef { index: number }`, `Literal { value: string|number|boolean }`, `FunctionCall { name: 'ANSWERED'|'COUNT_SELECTED'|'LENGTH'; arg: QuestionRef }`; plus `Rule { id; expression: Node; raw: string }`, `ParseError extends Error { position }`, `EvaluationContext { answers: Record<string, Answer>; survey: Survey }`.
- Parser: recursive descent, one function per grammar rule. `or_expr` is the entry and the LOWEST precedence (outermost loop); `and_expr` is consumed whole inside each `or_expr` arm. That is exactly why `a OR b AND c` parses as `a OR (b AND c)`. Tokenize in one left-to-right scan keeping token positions for `ParseError`. Do not parse flat / left-to-right and do not use a regex.
- Evaluator: pure recursive walk; `AND`/`OR` short-circuit; comparisons coerce per `Answer` kind (a scale number vs a string literal is an error). ANY evaluation error (unknown `qN`, type mismatch) is caught and the whole rule returns false (question hidden) - never throw out of the evaluator. `ANSWERED(qN)` true iff an answer exists (true even for empty-string text); `COUNT_SELECTED(qN)` = selected count for multiple_choice else 0; `LENGTH(qN)` = char length for text else 0.
- Reference migration (the trap - implement as pure functions in `src/logic/migrate.ts`, tested without the React tree):
  - Delete of the question at index `D`: rewrite every rule string - each `qN` with `N > D` becomes `q(N-1)`; each `qN` with `N === D` becomes a BROKEN reference (do not silently re-point it at whatever slid into slot D). A rule with a broken reference is KEPT and flagged (broken-rule warning in the admin list); at runtime the broken reference makes the rule evaluate false via the evaluator's safe-fail, so the question is hidden and nothing crashes.
  - Reorder (move from index `F` to index `T`): build the full old-index -> new-index permutation the move induces ONCE, then rewrite every `qN` through that map. Do NOT shift indices one-at-a-time - that double-shifts any reference whose index sits between `F` and `T`.
- Cycle detection (q4 -> q7 -> q4) is rejected at save time in `RuleEditor` via reachability over the `qN` reference graph using a visited set.

Preload:
- src/data/types.ts
- src/pages/TakeSurvey.tsx
- src/components/admin/QuestionEditor.tsx

Outputs:
- `src/data/types.ts` updated: `Question` gains optional `displayRule?: string` (raw DSL text).
- `src/logic/types.ts`: the AST node types, `Rule`, `ParseError`, `EvaluationContext` above.
- `src/logic/grammar.ts`: the grammar as documentation + exported BNF string.
- `src/logic/parser.ts`: `parse(input: string): Node` - throws `ParseError` with a position on malformed input.
- `src/logic/evaluator.ts`: `evaluate(node: Node, ctx: EvaluationContext): boolean` - pure; safe-fails to false.
- `src/logic/migrate.ts`: `migrateOnDelete(survey: Survey, deletedIndex: number): Survey` and `migrateOnReorder(survey: Survey, from: number, to: number): Survey` - pure; rewrite every question's `displayRule`.
- `src/components/admin/RuleEditor.tsx`: textarea, live parse status (green check / red error), an insert-question-reference dropdown, and a Save that enables only when the rule parses and introduces no cycle.
- `src/pages/TakeSurvey.tsx` updated: skip a question when its `displayRule` evaluates false; re-evaluate on every answer change (back then forward re-hides/re-shows correctly).
- `src/components/admin/QuestionEditor.tsx` updated: call `migrateOnDelete` / `migrateOnReorder` from the delete and move handlers.
- `src/logic/__tests__/parser.test.ts`: each operator; precedence (AND tighter than OR); parentheses; every literal kind; malformed-input position errors; unknown function; unbalanced parens.
- `src/logic/__tests__/evaluator.test.ts`: each operator; short-circuit (false AND left does not evaluate right; true OR left does not); each function; type-mismatch safe-fails to false; `ANSWERED(qN)` correct when N unanswered; nested expressions.
- `src/logic/__tests__/migrate.test.ts`: delete re-indexes higher refs; delete of a referenced question yields a broken-and-flagged ref; reorder remaps through the permutation with a reference sitting between `from` and `to` (the double-shift case); no-rules survey is unchanged.
- `src/logic/__tests__/integration.test.ts`: full survey with conditional questions; back-navigation changing an answer re-hides/re-shows the dependent question; cycle detection rejects q4-q7-q4.

Acceptance:
- [ ] parser produces a typed AST or a typed `ParseError` with a position
- [ ] precedence holds (`a OR b AND c` === `a OR (b AND c)`); parentheses override
- [ ] evaluator is pure, returns boolean, short-circuits AND/OR, and safe-fails to false on every error path (never throws)
- [ ] the three function semantics are correct
- [ ] deleting a question re-indexes higher `qN` refs; a ref to the deleted question becomes broken-and-flagged and the survey still loads
- [ ] reordering remaps every `qN` through the move permutation with no double-shift
- [ ] take-survey honors rules on forward AND back navigation
- [ ] cycle detection rejects q4-q7-q4 at save time
- [ ] the admin list shows a broken-rule warning for a rule referencing a deleted question
- [ ] parser, evaluator, migrate, and integration suites pass

Verify:
- `npm run typecheck`
- `npm test -- src/logic/__tests__/parser.test.ts src/logic/__tests__/evaluator.test.ts src/logic/__tests__/migrate.test.ts src/logic/__tests__/integration.test.ts`
- `npm run build`

Failure modes (the reviewer checks each of these against the diff):
- Parsing with a regex: passes simple cases, breaks on nested parens and on quoted strings containing operator words. Reject.
- Flat left-to-right evaluation with no precedence: makes `a OR b AND c` behave as `(a OR b) AND c`. The precedence test catches it.
- Evaluator that throws on a missing `qN` or type mismatch instead of safe-failing to false: crashes the survey runtime. The safe-fail test catches it.
- Non-short-circuiting AND/OR: evaluates the right side of a false AND. The short-circuit test catches it.
- Delete that re-indexes higher refs but forgets the rule referencing the DELETED index, leaving a `qN` silently pointing at whatever slid into slot D. It must become a broken reference, not a re-point.
- Reorder that shifts indices one moved-item-at-a-time instead of building the full permutation: double-shifts references between `from` and `to`. The in-between-reference reorder test catches it.
- Recomputing rules only on initial render, not on back-navigation answer changes: a dependent question stays hidden after the user goes back and changes its controlling answer. The back-navigation integration test is the load-bearing criterion.
- Cycle detection that recurses without a visited set: infinite loop on q4 -> q7 -> q4.

Notes: recursive descent or Pratt both work; the grammar is small, hand-roll it. Do the migration index math as pure functions in `migrate.ts` and test it exhaustively before wiring it into the React tree - the editor and runtime are thin over it. The back-navigation re-evaluation and the reorder permutation are the two criteria most likely to be gotten wrong.

OOS:
- Do not add a parser-combinator or expression-eval library (chevrotain, peg.js, nearley, math.js)
- Do not implement loops, variables, or assignment in the DSL
- Do not implement arithmetic operators (comparison and boolean only)
- Do not implement an "else show this question" branch (show/hide only)
- Do not implement rule templates or snippets in the editor
- Do not implement rules that modify answers (rules read, never write)

## What done looks like

After all eight briefs land, the survey app supports:

- A user visiting `/` sees a link to take the starter survey, clicks through five mixed-kind questions, advances with Next, navigates back, submits, and sees their response detail.
- A user can visit `/responses` to review all past responses for the current device.
- An admin can visit `/admin/surveys` to create, edit, and delete surveys and view results.
- The results page shows aggregate counts and distributions plus a bar chart for closed questions and a small histogram for scale questions, with sample text answers for open questions.
- An admin can attach display rules to questions; rules use a small DSL with parser, evaluator, cycle detection, and live editor feedback. Rules respect back-navigation, and deleting or reordering questions rewrites the positional references in every rule with no double-shift and no dangling re-point - a rule that loses its referent is flagged but never breaks the survey.

The build passes `npm run build`, `npm run typecheck`, and `npm test` cleanly. No external services, no backend, no auth, no chart library, no parser library, no state management library, no CSS framework - plain React + TypeScript + localStorage with hand-rolled primitives where needed.