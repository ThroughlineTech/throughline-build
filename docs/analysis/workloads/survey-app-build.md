# Operation: survey-app-build

Build a self-contained survey-taking web app: React + TypeScript + Vite, localStorage for persistence, no backend. Survey-takers answer multi-question surveys with branching logic; admins define survey templates and view aggregate results with simple visualizations. Eight briefs across two plans, ranging from straightforward scaffolding to one deliberately convoluted feature that requires real design work.

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
| 04 | my-responses | "My responses" page: lists prior submissions for the current session, shows answers per response | 03 | S | src/pages/MyResponses.tsx, src/pages/__tests__/MyResponses.test.tsx |

### Briefs - detail

#### Brief 01: vite-scaffold

Goal: Initialize the project with Vite, React 18, TypeScript, React Router, Vitest, and a minimal "Hello survey" homepage. The build must pass `npm run build` cleanly and `npm test` with one trivial passing test.

Inputs:
- Node 20+ available locally
- Vite's React-TypeScript template (`npm create vite@latest survey-app -- --template react-ts`)

Outputs:
- `package.json` with dependencies: `react`, `react-dom`, `react-router-dom`. Dev dependencies: `vite`, `@vitejs/plugin-react`, `typescript`, `@types/react`, `@types/react-dom`, `vitest`, `@testing-library/react`, `@testing-library/jest-dom`, `jsdom`
- `vite.config.ts` with the React plugin and Vitest configuration pointing at jsdom
- `tsconfig.json` with strict mode enabled
- `src/main.tsx` mounting the App component inside a `BrowserRouter`
- `src/App.tsx` with a top-level `<Routes>` block. Initial route `/` renders a placeholder home page reading "Survey app". Add a second route `/take` rendering a "Take a survey" placeholder so routing is exercised.
- `src/index.css` with minimal global styles (system font stack, sensible defaults). No CSS framework.
- `vitest.config.ts` (or vitest config inline in vite.config.ts) wiring jsdom + testing-library setup
- One trivial passing test: `src/App.test.tsx` renders App and asserts "Survey app" text appears
- `README.md` with a short description and the commands (`npm install`, `npm run dev`, `npm run build`, `npm test`)
- `.gitignore` covering node_modules, dist, .vscode (or whatever Vite's default omits)

Acceptance:
- [ ] `npm install` completes without errors
- [ ] `npm run build` produces a `dist/` directory without errors
- [ ] `npm run dev` starts a dev server; navigating to `/` shows "Survey app"; navigating to `/take` shows "Take a survey"
- [ ] `npm test` runs Vitest and reports one passing test
- [ ] TypeScript strict mode is enabled and the build has zero TS errors
- [ ] No CSS framework added (no Tailwind, no MUI, no Bootstrap)
- [ ] No state management library added (no Redux, no Zustand, no Recoil); plain React state and context only

Notes: Keep dependencies minimal. The survey-taking and admin features land in later briefs; this brief is just the scaffold. Resist adding any UI library or styling framework.

OOS:
- Do not add Tailwind, MUI, Bootstrap, or any CSS framework
- Do not add Redux, Zustand, or any state management library
- Do not add a backend, mock API, or service worker
- Do not add authentication or session management
- Do not add a database wrapper or ORM (localStorage will be wrapped in B02)

#### Brief 02: survey-data-model

Goal: Define the typed survey, question, and response shapes. Implement a localStorage-backed repository that loads, saves, lists, and deletes surveys and responses. Seed a starter survey so the app shows something on first load.

Inputs:
- The scaffolded project from B01
- TypeScript's `interface` and `type` syntax

Outputs:
- `src/data/types.ts` defining:
  - `Survey`: `id` (string), `title` (string), `description` (string, optional), `questions` (array of `Question`), `createdAt` (ISO string)
  - `Question`: a discriminated union over `kind`. Variants in this brief: `single_choice` (id, kind, prompt, options as string[]), `multiple_choice` (id, kind, prompt, options as string[]), `short_text` (id, kind, prompt, maxLength as number optional), `long_text` (id, kind, prompt, maxLength as number optional), `scale` (id, kind, prompt, min number, max number, minLabel string optional, maxLabel string optional)
  - `Response`: `id` (string), `surveyId` (string), `answers` (record keyed by question id; value type depends on question kind), `submittedAt` (ISO string)
  - `Answer`: a discriminated union matching Question kinds
- `src/data/repository.ts` exporting:
  - `getAllSurveys(): Survey[]`
  - `getSurvey(id: string): Survey | undefined`
  - `saveSurvey(survey: Survey): void` (upsert by id)
  - `deleteSurvey(id: string): void`
  - `getAllResponses(): Response[]`
  - `getResponsesForSurvey(surveyId: string): Response[]`
  - `saveResponse(response: Response): void` (upsert by id)
  - `deleteResponse(id: string): void`
  - Internal: localStorage keys `survey-app:surveys` and `survey-app:responses` (each storing a JSON-encoded array)
- `src/data/seed.ts` exporting `seedIfEmpty()`: if no surveys exist in localStorage, insert one starter survey with ~5 mixed-type questions (single_choice, scale, short_text, multiple_choice, long_text). Called from App.tsx on mount.
- `src/data/__tests__/repository.test.ts` covering: save-and-read roundtrip for surveys, save-and-read for responses, delete behavior, getResponsesForSurvey filters correctly, empty-localStorage returns empty arrays (not undefined), JSON corruption in localStorage is handled gracefully (return empty + log)

Acceptance:
- [ ] All five question kinds defined as a discriminated union
- [ ] `Answer` type matches Question kinds (single_choice answer is the chosen option string; multiple_choice is string[]; short_text/long_text is string; scale is number)
- [ ] Repository functions all listed and implemented
- [ ] `seedIfEmpty()` inserts the starter survey only when localStorage is empty (idempotent)
- [ ] Corrupted localStorage payload (e.g. invalid JSON) does not crash; repository returns empty arrays
- [ ] Vitest tests pass; coverage includes the corruption path
- [ ] `npm run build` and `npm test` still pass

Notes: localStorage stores strings only; serialize with `JSON.stringify` and parse with `JSON.parse` wrapped in try/catch. UUIDs can come from `crypto.randomUUID()` (available in modern browsers and the Vitest jsdom environment).

OOS:
- Do not add IndexedDB or any other storage layer; localStorage only
- Do not add migration logic for schema changes (future brief; flagged here so it does not creep)
- Do not implement validation logic on save (out of scope; the editor in B05 will validate)
- Do not export the repository as a class; plain functions are sufficient

#### Brief 03: take-survey

Goal: Implement the survey-taking page. URL `/take/:surveyId` loads the survey, shows one question at a time with Next/Back navigation and a progress bar, and persists a Response on Submit.

Inputs:
- Types and repository from B02
- React Router's `useParams` and `useNavigate`

Outputs:
- `src/pages/TakeSurvey.tsx` rendering:
  - Survey title and description at top
  - Progress bar showing "Question N of M"
  - One `QuestionRenderer` for the current question
  - Back button (disabled on first question) and Next button (becomes Submit on last question)
  - Submit creates a `Response` with all answers, calls `saveResponse`, navigates to `/responses/:responseId`
- `src/components/QuestionRenderer.tsx`: switches on Question.kind and renders the appropriate input. `single_choice` to radio group. `multiple_choice` to checkbox group. `short_text` to input. `long_text` to textarea. `scale` to numeric slider or radio of values from min to max with optional labels at endpoints.
- `src/components/ProgressBar.tsx`: simple horizontal bar showing percentage complete
- `src/App.tsx` updated to route `/take/:surveyId` to TakeSurvey
- A "Take the starter survey" link from `/` to `/take/:starterSurveyId`
- `src/pages/__tests__/TakeSurvey.test.tsx` covering: renders first question on load, Next advances, Back retreats, answers persist as user navigates back and forth, Submit creates a Response, Submit navigates to `/responses/:responseId`

Acceptance:
- [ ] All five question kinds render correctly
- [ ] Answers are preserved when navigating Back then Next (don't reset on remount)
- [ ] Progress bar reflects the current question position
- [ ] Submit creates a Response with all answers in the `answers` record keyed by question id
- [ ] Submit navigates to `/responses/:responseId` (page does not need to exist yet; B04 builds it; for this brief, a placeholder route returning "Response saved" is fine)
- [ ] Tests pass

Notes: Use local component state for the in-progress answers; persist only on Submit (no autosave). If the user closes the tab mid-survey, the response is lost. Acceptable for v1.

OOS:
- Do not implement autosave or draft persistence
- Do not implement conditional logic between questions (deferred to B08)
- Do not implement question randomization or shuffle
- Do not implement file upload questions or any media types
- Do not add an "are you sure you want to leave" warning

#### Brief 04: my-responses

Goal: Implement two related pages: `/responses` lists prior responses for the current localStorage; `/responses/:responseId` shows one response with all answers.

Inputs:
- Types and repository from B02
- TakeSurvey navigates to `/responses/:responseId` on Submit

Outputs:
- `src/pages/MyResponses.tsx` (the list): shows all responses with the survey title, submission date, and a link to the detail page. Empty state: "No responses yet. Take a survey to get started."
- `src/pages/ResponseDetail.tsx` (the detail): shows the survey title, submission date, and each question + its answer. Read-only.
- `src/App.tsx` routes `/responses` and `/responses/:responseId`
- Navigation: top-level header with links to "Home", "My responses". Header lives in a small `Header` component in `src/components/Header.tsx` rendered above the Routes in App.tsx
- `src/pages/__tests__/MyResponses.test.tsx` covering: empty state, list with one response, list with multiple responses sorted by date descending
- `src/pages/__tests__/ResponseDetail.test.tsx` covering: renders all question-and-answer pairs, handles missing answers gracefully (the survey was edited after the response was saved), 404-style message when responseId not found

Acceptance:
- [ ] List page shows all responses ordered by `submittedAt` descending
- [ ] Detail page renders every question from the original survey with the user's answer beside it
- [ ] If the survey has been edited and a question no longer exists, the response's answer for that question is still displayed under a "(question removed)" heading
- [ ] If the response id is not found, the detail page shows a friendly message and a link back to the list
- [ ] Header navigation works from any route
- [ ] Tests pass

Notes: This brief uses only types and repository functions from B02. No new data model concepts. The "(question removed)" case is the only design wrinkle; the survey snapshot is not embedded in the response so the response renders against the current survey. That is a deliberate v1 simplification (B08 may revisit if branching logic makes this fragile).

OOS:
- Do not implement editing or re-submitting a response
- Do not implement response deletion from the UI (repository supports it but no button surface yet)
- Do not embed a survey snapshot in the response

## Plan B: Admin, visualization, branching logic

### Goal

Admins can define survey templates, view aggregate results with a basic chart, and attach conditional logic rules to questions. The conditional logic engine is the deliberately convoluted brief - real design work, real edge cases.

### Briefs

| # | Slug | Intent | Deps | Effort | Files |
|---|------|--------|------|--------|-------|
| 05 | admin-surveys | Admin CRUD for survey templates: list page + create/edit form | - | L | src/pages/admin/AdminSurveyList.tsx, src/pages/admin/AdminSurveyEdit.tsx, src/components/admin/QuestionEditor.tsx, src/pages/admin/__tests__/AdminSurveyList.test.tsx, src/pages/admin/__tests__/AdminSurveyEdit.test.tsx |
| 06 | admin-results | Aggregate results page: counts and distributions per question for a chosen survey | 05 | M | src/pages/admin/AdminResults.tsx, src/pages/admin/__tests__/AdminResults.test.tsx, src/data/aggregate.ts, src/data/__tests__/aggregate.test.ts |
| 07 | results-chart | Bar chart visualization for closed-question distributions on the results page | 06 | S | src/components/ResultsChart.tsx, src/pages/admin/AdminResults.tsx, src/components/__tests__/ResultsChart.test.tsx |
| 08 | conditional-logic-engine | Expression DSL, parser, evaluator, admin editor, and runtime integration for question display rules | 05 | L | src/logic/grammar.ts, src/logic/parser.ts, src/logic/evaluator.ts, src/logic/types.ts, src/components/admin/RuleEditor.tsx, src/pages/TakeSurvey.tsx (updated), src/data/types.ts (updated), src/logic/__tests__/parser.test.ts, src/logic/__tests__/evaluator.test.ts, src/logic/__tests__/integration.test.ts |

Plan B depends on Plan A's data model (B02) at the plan level; brief Deps above reference same-plan briefs only.

### Briefs - detail

#### Brief 05: admin-surveys

Goal: An admin can list existing survey templates, edit them, create new ones, and delete them. The editor supports adding/removing/reordering questions and editing each question's prompt and options.

Inputs:
- Types and repository from B02 (delivered by Plan A)

Outputs:
- `src/pages/admin/AdminSurveyList.tsx`: `/admin/surveys` shows all surveys with title, question count, response count, edit/delete buttons, and a "New survey" button
- `src/pages/admin/AdminSurveyEdit.tsx`: `/admin/surveys/:id` shows the survey editor. `:id` of `new` means create-new-survey
- `src/components/admin/QuestionEditor.tsx`: one editor instance per question. Renders the kind selector, prompt input, and kind-specific fields (options for choice questions, min/max/labels for scale)
- Admin link in Header: "Admin" route to `/admin/surveys`
- `src/App.tsx` routes the admin pages
- Validation: survey requires a title; each question requires a prompt; choice questions require at least 2 options; scale questions require min < max
- Save button disabled until validation passes; on save, calls `saveSurvey` and navigates back to list
- Delete confirms with a browser confirm() dialog (no custom modal); on confirm calls `deleteSurvey` and refreshes the list
- Reorder: up/down buttons on each question (no drag-drop required); first/last buttons disabled appropriately
- Tests: list shows surveys, list shows empty state, create-new flow saves a valid survey, edit flow loads and saves, delete flow removes a survey, validation blocks save when invalid

Acceptance:
- [ ] List shows all surveys with question and response counts (response count via `getResponsesForSurvey(survey.id).length`)
- [ ] Editor supports adding, removing, and reordering questions
- [ ] Editor supports all five question kinds from B02
- [ ] Validation prevents saving an invalid survey
- [ ] Delete confirms and removes
- [ ] Tests pass

Notes: The editor UI does not need to be polished; functional clarity over aesthetics. Browser `confirm()` is acceptable for the delete dialog; no custom modal component required.

OOS:
- Do not implement question reordering via drag and drop (up/down buttons only)
- Do not implement question type changes preserving existing options (changing kind resets the kind-specific fields)
- Do not implement undo / redo
- Do not implement survey import / export

#### Brief 06: admin-results

Goal: An admin can pick a survey and see aggregate results: total responses, per-question response distributions for closed questions, sample text answers for open questions.

Inputs:
- Repository from B02 (delivered by Plan A)
- Survey list from B05 (link to results from each survey row)

Outputs:
- `src/pages/admin/AdminResults.tsx`: `/admin/surveys/:id/results` shows the survey title, total response count, per-question section
- For each question:
  - `single_choice`: counts per option, percentages
  - `multiple_choice`: counts per option (each option's count = responses where that option was selected), percentages of total responses
  - `scale`: count per scale value, mean, median, standard deviation
  - `short_text` / `long_text`: total response count, "show first 10 responses" toggle that reveals up to 10 verbatim answers
- "Back to surveys" link
- Empty state: "No responses yet for this survey."
- `src/data/aggregate.ts`: pure functions for computing aggregates from `Response[]`. `aggregateClosedQuestion(question, responses): { option: string, count: number, pct: number }[]`; `aggregateScale(question, responses): { mean, median, stddev, counts: Record<number, number> }`; etc.
- `src/data/__tests__/aggregate.test.ts`: covers each aggregate function with hand-built fixtures, including edge cases (zero responses, all responses skipping a question, all responses choosing the same option, scale with mode at min, scale with mode at max)
- AdminSurveyList row gets a "View results" link to `/admin/surveys/:id/results`

Acceptance:
- [ ] Aggregate functions are pure and unit-tested
- [ ] Page renders correctly for each question kind
- [ ] Mean / median / stddev are computed correctly for scale questions
- [ ] Empty-state renders when no responses exist
- [ ] Tests pass

Notes: Aggregate computation lives in pure functions so they can be reused by future tools (export, dashboard, etc.) and tested independently of the React rendering. Resist mixing rendering with computation.

OOS:
- Do not implement filtering responses by date or any other dimension
- Do not implement comparison between two surveys
- Do not implement export to CSV (future)
- Do not normalize or analyze text-answer content (no sentiment, no clustering)

#### Brief 07: results-chart

Goal: Add a bar chart visualization for closed-question distributions on the results page. SVG-based, no chart library.

Inputs:
- Aggregate functions from B06
- The results page from B06

Outputs:
- `src/components/ResultsChart.tsx`: a React component that takes `{ data: { label: string, value: number }[], maxValue?: number }` and renders a horizontal bar chart in SVG. Each bar labeled with the option name on the left, count and percentage on the right. Height proportional to count.
- Chart is responsive: width fills the container; bar height fixed (e.g., 32px per row); total height scales with row count
- Used in AdminResults for single_choice and multiple_choice question sections (above the existing text-rendering of counts; the chart visualizes, the text gives precise numbers)
- For scale questions, render a small histogram chart (vertical bars across scale values) using the same component or a small variant
- Accessibility: each bar has an `aria-label` describing label and count
- `src/components/__tests__/ResultsChart.test.tsx`: renders correctly for sample data, handles empty data array, handles all-zero values, handles a single-bar dataset

Acceptance:
- [ ] Chart renders as SVG without any external chart library
- [ ] Visualization is readable for typical data (3-10 bars)
- [ ] Accessibility: bars have aria-labels; the chart has a role and label
- [ ] Tests pass

Notes: SVG hand-rolling is faster than introducing a chart library for this scope and avoids a heavyweight dependency. Resist Recharts, Chart.js, D3, etc. unless the design genuinely requires more than horizontal bars and a small histogram.

OOS:
- Do not add a chart library (Recharts, Chart.js, D3, Plotly, Victory)
- Do not implement chart-type switching (only bar / histogram)
- Do not implement chart export to PNG
- Do not implement animation or transitions

#### Brief 08: conditional-logic-engine

Goal: Allow survey designers to attach display rules to questions. A question can declare "show this question only if `q3 == 'yes' AND q5 != 'maybe'`" using a small expression DSL. Implement the grammar, parser, evaluator, admin editor surface, and runtime integration with the take-survey page. This is the convoluted brief: several wrong implementations look right. Plan carefully before writing code.

Inputs:
- Types from B02 (extend in this brief)
- Admin editor from B05 (extend in this brief)
- Take-survey runtime from B03 (extend in this brief)

Grammar: define in `src/logic/grammar.ts` as documentation; informal BNF or prose is fine.

```
expr      := or_expr
or_expr   := and_expr ("OR" and_expr)*
and_expr  := not_expr ("AND" not_expr)*
not_expr  := "NOT" not_expr | comparison
comparison:= primary (("==" | "!=" | "<" | "<=" | ">" | ">=" | "CONTAINS" | "NOT_CONTAINS") primary)?
primary   := question_ref | literal | "(" expr ")" | function_call
question_ref := "q" digit+        // e.g. q3, q12 - references question by index
literal   := string_literal | number_literal | boolean_literal
string_literal := "'" chars "'"
function_call := identifier "(" args ")"   // initial functions: ANSWERED(qN), COUNT_SELECTED(qN), LENGTH(qN)
```

- Comparisons coerce types where sensible: comparing a `scale` answer (number) to a string literal fails loudly; comparing a `single_choice` (string) to a string literal works; `CONTAINS` works on multiple_choice answers (array)
- `ANSWERED(qN)` returns true iff the user has provided an answer for question N (true even for empty-string text answers; the user explicitly submitted them)
- `COUNT_SELECTED(qN)` returns the number of options selected for a multiple_choice question; 0 otherwise
- `LENGTH(qN)` returns the character length for text answers; 0 otherwise
- Errors during evaluation (referencing a non-existent question, type mismatch in comparison) produce an evaluation error that is logged and treated as `false` (question hidden) so a malformed rule never crashes the survey runtime

Outputs:
- `src/logic/types.ts`: AST node types (BinaryOp, UnaryOp, QuestionRef, Literal, FunctionCall), Rule record (id, expression, raw text), EvaluationContext (current responses, survey reference)
- `src/logic/parser.ts`: parses a string into an AST; throws a typed `ParseError` with position and message on failure; tested across the grammar's surface
- `src/logic/evaluator.ts`: walks an AST against an `EvaluationContext` and returns a `boolean` (with safe-fail on errors); pure function
- `src/components/admin/RuleEditor.tsx`: attached to each question in the admin editor. UI shows: a textarea for the rule string, live parse status (green check / red error message), an "Insert question reference" dropdown that helps the admin avoid typos, a "Save" button that only enables when the rule parses
- Survey type updated: `Question` records gain an optional `displayRule?: string` (raw DSL text)
- Take-survey runtime updated: when iterating questions, skip questions whose `displayRule` evaluates to `false` against the current answers; recompute when answers change (e.g., navigating back and changing q3 should re-evaluate q4's rule when navigating forward)
- Cycle detection: at save time in the admin editor, reject rules that create reference cycles (q4 references q7 which references q4). Cycle detection uses graph reachability over the question reference graph; tested.
- Migration handling: if a referenced question is deleted, every rule referring to it is flagged in the admin list with a "broken rule" warning, but surveys still load (the broken rule evaluates to false; the question is hidden)

Tests:
- `src/logic/__tests__/parser.test.ts`: parses each operator type, parses precedence (AND binds tighter than OR), parses parentheses, handles all literal kinds, errors on malformed input with sensible position info, errors on unknown function names, errors on unbalanced parens
- `src/logic/__tests__/evaluator.test.ts`: evaluates each operator correctly, handles short-circuit (AND with false left does not evaluate right; OR with true left does not), handles each function correctly, handles type-mismatch as safe-false, handles missing-answer cases (`ANSWERED(qN)` works correctly when N is not answered), nested expressions evaluate correctly
- `src/logic/__tests__/integration.test.ts`: full survey with conditional questions, navigating back and changing an answer re-hides / re-shows the dependent question, cycle detection rejects q4-q7-q4

Acceptance:
- [ ] DSL grammar implemented as specified (or with documented justified deviations)
- [ ] Parser produces typed AST or typed `ParseError`
- [ ] Evaluator is a pure function returning boolean
- [ ] Type mismatches and missing references safe-fail to false (question hidden), never crash
- [ ] Cycle detection rejects circular rules at save time
- [ ] Take-survey runtime honors rules during navigation (forward AND backward)
- [ ] Admin editor shows live parse status as the admin types
- [ ] Broken-rule warning appears in the admin survey list when a rule references a deleted question
- [ ] All test suites pass (parser, evaluator, integration)

Notes: This is the deliberate stew-on-it brief. Wrong implementations include: parsing with regex (works for simple cases, breaks on nested parens or quoted strings with operators inside); evaluating left-to-right without precedence (`a OR b AND c` should equal `a OR (b AND c)`, not `(a OR b) AND c`); recursing without cycle detection (infinite loop when a rule references its own question); recomputing only on initial render and not on back-navigation. Plan the AST shape and the precedence climbing before coding. The integration test for back-navigation re-evaluation is the load-bearing acceptance criterion. A recursive-descent parser is the right shape; Pratt parsing or operator-precedence parsing both work. Resist a parser library (peg.js, nearley, chevrotain); the grammar is small enough that hand-rolling is faster and the result is auditable.

OOS:
- Do not add a parser combinator library (chevrotain, peg.js, nearley)
- Do not add a math expression evaluator library (math.js)
- Do not implement loops, variables, or assignment in the DSL
- Do not implement arithmetic operators (+, -, *, /); comparison and boolean only
- Do not implement an "else show this question" branch (display rules are show-or-hide, not branching outcomes)
- Do not implement rule templates or snippets in the editor
- Do not implement rules that modify answers (rules read, never write)

## What done looks like

After all eight briefs land, the survey app supports:

- A user visiting `/` sees a link to take the starter survey. They click through five questions with mixed kinds (single choice, scale, short text, multiple choice, long text), advance with Next, can navigate back, submit, see their response detail.
- A user can visit `/responses` to review all their past responses for the current device.
- An admin can visit `/admin/surveys`, create a new survey with arbitrary questions, edit existing surveys, delete surveys, and view results.
- The results page shows aggregate counts, distributions, and a bar chart for closed questions; a small histogram for scale questions; sample text answers for open questions.
- An admin can attach display rules to questions ("only show q4 if q3 == 'yes'"). Rules use a small expression DSL with parser, evaluator, cycle detection, and live editor feedback. Rules respect back-navigation: changing q3's answer re-hides or re-shows q4 immediately when the user navigates forward.

The build passes `npm run build` and `npm test` cleanly. No external services, no backend, no auth, no chart library, no parser library, no state management library, no CSS framework. Plain React + TypeScript + localStorage with hand-rolled primitives where needed.