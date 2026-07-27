import json, os, glob, collections, statistics, math

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, '..', 'data')
lf = json.load(open(os.path.join(HERE, 'lf_rows.json')))

old = {}
for f in [os.path.join(DATA, 'arm-a', 'runs.jsonl')]:
    for line in open(f, encoding='utf-8'):
        if not line.strip():
            continue
        r = json.loads(line)
        old[(r.get('session_id'), r.get('command'), r.get('ts'))] = r

print("=" * 92)
print("OLD SIDE /ti BY MODEL (billed input = input + cache_read + cache_create, incl. subagents)")
print("=" * 92)
rows = []
for r in old.values():
    if r.get('command') != '/ti' or not r.get('model'):
        continue
    b = sum(r.get(k) or 0 for k in ('input', 'cache_read', 'cache_create',
                                    'subagent_input', 'subagent_cache_read', 'subagent_cache_create'))
    o = (r.get('output') or 0) + (r.get('subagent_output') or 0)
    rows.append((r['model'], b, o, r.get('label'), r.get('ts', '')[:10]))
rows.append(('claude-opus-4-7', 7434 + 2095454 + 172304, 39990, 'SURCC-6', '2026-05-26'))
bm = collections.defaultdict(list)
for m, b, o, lb, ts in sorted(rows):
    bm[m].append((b, o))
print(f"{'model':28}{'n':>3}{'med billed':>13}{'mean billed':>13}{'med out':>10}{'mean out':>10}")
for m in sorted(bm):
    v = bm[m]
    print(f"{m:28}{len(v):>3}{statistics.median(x[0] for x in v):>13,.0f}"
          f"{statistics.mean(x[0] for x in v):>13,.0f}"
          f"{statistics.median(x[1] for x in v):>10,.0f}{statistics.mean(x[1] for x in v):>10,.0f}")
print()
print("  individual rows:")
for m, b, o, lb, ts in sorted(rows, key=lambda x: (x[0], -x[1])):
    print(f"    {ts}  {m:28}{str(lb):22}billed={b:>12,}  out={o:>8,}")
print()

print("=" * 92)
print("NEW SIDE BY MODEL x PHASE (per LLM call)")
print("=" * 92)
g = collections.defaultdict(list)
for r in lf:
    g[(r['model'], r['phase'])].append(r)
print(f"{'model':22}{'phase':11}{'n':>4}{'med billed':>12}{'mean billed':>12}"
      f"{'med out':>9}{'mean out':>9}{'med wall_s':>11}")
for k in sorted(g):
    v = g[k]
    b = [x.get('billed_input', x['inp'] + x['cr'] + x['cc']) for x in v]
    o = [x['out'] for x in v]
    w = [x['ms'] / 1000 for x in v if x['ms']]
    print(f"{k[0]:22}{k[1]:11}{len(v):>4}{statistics.median(b):>12,.0f}{statistics.mean(b):>12,.0f}"
          f"{statistics.median(o):>9,.0f}{statistics.mean(o):>9,.0f}"
          f"{(statistics.median(w) if w else 0):>11,.0f}")
print()

print("=" * 92)
print("NEW SIDE: RUNNER ITERATION SERIES (runs 09-14)")
print("=" * 92)
# These directories are successive runner experiments, not model-only replicates. Runs 09, 10,
# and 11 changed the op-doc guidance. Runs 11-14 share the same op-doc, while runner builds
# continued to change. Keep the per-run identity visible.
EXPERIMENT_SUFFIX = '-experiment'
sst = [r for r in lf if r['repo'].endswith(EXPERIMENT_SUFFIX)]
if not sst:
    raise SystemExit(
        "no experiment runs matched '*%s' - if the run directories under "
        "data/arm-b/events/ were renamed, update EXPERIMENT_SUFFIX to match. "
        "Repos seen: %s" % (EXPERIMENT_SUFFIX, sorted({r['repo'] for r in lf})))
RUN_META = {
    'run-09-experiment': ('exp-1', 'gate-output iteration baseline'),
    'run-10-experiment': ('exp-2', 'read scope tightened; preload added but no-op'),
    'run-11-experiment': ('exp-3/4', 'explicit Preload lists; preload fixed and firing'),
    'run-12-experiment': ('exp-3/4', 'context telemetry/hygiene plus runner changes'),
    'run-13-experiment': ('exp-3/4', 'near-replicate plus sweep/integration changes'),
    'run-14-experiment': ('exp-3/4', 'worker/phase changes plus Fable model'),
}


def run_versions(run):
    versions = set()
    pattern = os.path.join(DATA, 'arm-b', 'events', run, '*.jsonl')
    for path in glob.glob(pattern):
        for line in open(path, encoding='utf-8'):
            if not line.strip():
                continue
            e = json.loads(line)
            if e.get('build_version'):
                versions.add(e['build_version'].replace('0.1.0+', ''))
    return ','.join(sorted(versions))


print(f"{'run':20}{'model':21}{'build':25}{'op-doc':9}{'billed':>13}{'delta':>9}"
      f"{'output':>10}{'wall_m':>9}")
print('-' * 108)
previous = None
for run in sorted(RUN_META):
    rows_for_run = [r for r in sst if r['repo'] == run]
    models = sorted({r['model'] for r in rows_for_run})
    total_billed = sum(r.get('billed_input', r['inp'] + r['cr'] + r['cc'])
                       for r in rows_for_run)
    total_output = sum(r['out'] for r in rows_for_run)
    total_wall = sum(r['ms'] for r in rows_for_run) / 60000
    delta = '' if previous is None else f"{(total_billed / previous - 1) * 100:+.1f}%"
    opdoc, _ = RUN_META[run]
    print(f"{run:20}{'/'.join(models):21}{run_versions(run):25}{opdoc:9}"
          f"{total_billed:>13,}{delta:>9}{total_output:>10,}{total_wall:>9.1f}")
    previous = total_billed
print()
print("Iteration notes:")
for run, (_, note) in RUN_META.items():
    print(f"  {run}: {note}")
print()
print("Deltas are descriptive changes from the immediately preceding run. They do not isolate")
print("a model, prompt, op-doc, or runner-code effect.")
