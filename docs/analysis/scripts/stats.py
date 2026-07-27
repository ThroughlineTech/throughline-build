import json, os, glob, collections, math

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, '..', 'data')

# Pinned 2026-05-25 rate card, per million. Mirrors ../data/pricing.toml (opus-4-7 block).
# Both arms use this card. Token-count ratios are rate-independent; dollar ratios depend on
# the card's relative input/output/cache weights.
PRICE = {
    'input': 15.00, 'output': 75.00, 'cache_read': 1.50, 'cache_create': 18.75,
}


def cost(inp, out, cr, cc):
    return (inp * PRICE['input'] + out * PRICE['output'] +
            cr * PRICE['cache_read'] + cc * PRICE['cache_create']) / 1e6


# ---------- OLD SIDE ----------
old = {}
for f in [os.path.join(DATA, 'arm-a', 'runs.jsonl')]:
    for line in open(f, encoding='utf-8'):
        line = line.strip()
        if not line:
            continue
        r = json.loads(line)
        k = (r.get('session_id'), r.get('command'), r.get('ts'))
        if k in old:
            continue
        old[k] = r

old_rows = []
for r in old.values():
    if not r.get('model'):
        continue
    inp = (r.get('input') or 0) + (r.get('subagent_input') or 0)
    out = (r.get('output') or 0) + (r.get('subagent_output') or 0)
    cr = (r.get('cache_read') or 0) + (r.get('subagent_cache_read') or 0)
    cc = (r.get('cache_create') or 0) + (r.get('subagent_cache_create') or 0)
    old_rows.append(dict(src='runs.jsonl', cmd=r['command'], model=r['model'], label=r.get('label'),
                         inp=inp, out=out, cr=cr, cc=cc))

# the paired SURCC-6 spine (compare/cc)
CCDIR = os.path.join(DATA, 'arm-a', 'matched-pair-ticket-6')
PH = {0: 'Plan', 1: 'Implement', 2: 'Review', 3: 'Ship'}
cc_spine = []
for f in sorted(glob.glob(os.path.join(CCDIR, '*.jsonl'))):
    for line in open(f, encoding='utf-8'):
        e = json.loads(line)
        if e.get('Kind') != 1:
            continue
        d = e['Data']
        cc_spine.append(dict(phase=PH[e['Phase']], model=d.get('model'),
                             inp=d.get('input_tokens', 0), out=d.get('output_tokens', 0),
                             cr=d.get('cache_read_tokens', 0), cc=d.get('cache_create_tokens', 0)))
cc_spine.sort(key=lambda r: ['Plan', 'Implement', 'Review', 'Ship'].index(r['phase']))

LFDIR = os.path.join(DATA, 'arm-b', 'matched-pair-ticket-6')
lf_spine = []
for f in sorted(glob.glob(os.path.join(LFDIR, '*.jsonl'))):
    for line in open(f, encoding='utf-8'):
        e = json.loads(line)
        if e.get('Kind') != 1:
            continue
        d = e['Data']
        lf_spine.append(dict(phase=PH[e['Phase']],
                             inp=d.get('input_tokens', 0), out=d.get('output_tokens', 0),
                             cr=d.get('cache_read_tokens', 0), cc=d.get('cache_create_tokens', 0)))
lf_spine.sort(key=lambda r: ['Plan', 'Implement', 'Review', 'Ship'].index(r['phase']))

print("=" * 78)
print("A. MATCHED FULL-SPINE CASE  SURCC-6 (old) vs SURLF-6 (new) - 2026-05-26")
print("=" * 78)
hdr = f"{'phase':11}{'old $':>9}{'new $':>9}{'x':>7}   {'old billed tok':>15}{'new billed tok':>15}{'x':>7}"
print(hdr)
print('-' * len(hdr))
lfmap = {r['phase']: r for r in lf_spine}
tot = collections.Counter()
for r in cc_spine:
    n = lfmap.get(r['phase'])
    oc = cost(r['inp'], r['out'], r['cr'], r['cc'])
    ob = r['inp'] + r['cr'] + r['cc']
    if n:
        nc = cost(n['inp'], n['out'], n['cr'], n['cc'])
        nb = n['inp'] + n['cr'] + n['cc']
    else:
        nc, nb = 0.0, 0
    rx = f"{oc/nc:.2f}x" if nc else "inf"
    rb = f"{ob/nb:.2f}x" if nb else "inf"
    print(f"{r['phase']:11}{oc:>9.2f}{nc:>9.2f}{rx:>7}   {ob:>15,}{nb:>15,}{rb:>7}")
    tot['oc'] += oc; tot['nc'] += nc; tot['ob'] += ob; tot['nb'] += nb
    tot['oo'] += r['out']; tot['no'] += (n['out'] if n else 0)
print('-' * len(hdr))
print(f"{'TOTAL':11}{tot['oc']:>9.2f}{tot['nc']:>9.2f}{tot['oc']/tot['nc']:>6.2f}x   "
      f"{tot['ob']:>15,}{tot['nb']:>15,}{tot['ob']/tot['nb']:>6.2f}x")
print(f"  output tokens: old {tot['oo']:,}  new {tot['no']:,}  = {tot['oo']/tot['no']:.2f}x")
print()

# ---------- B. PLAN-PHASE TWO-SAMPLE ----------
lf_rows = json.load(open(os.path.join(HERE, 'lf_rows.json')))

old_plan = [r for r in old_rows if r['cmd'] == '/ti']
old_plan.append(dict(src='compare/cc', cmd='/ti', model=cc_spine[0]['model'], label='SURCC-6',
                     inp=cc_spine[0]['inp'], out=cc_spine[0]['out'],
                     cr=cc_spine[0]['cr'], cc=cc_spine[0]['cc']))
new_plan = [r for r in lf_rows if r['phase'] == 'Plan']
new_plan.append(dict(repo='survey-lf', ticket='6', model='claude-opus-4-7',
                     inp=lf_spine[0]['inp'], out=lf_spine[0]['out'],
                     cr=lf_spine[0]['cr'], cc=lf_spine[0]['cc']))


def billed(r):
    return r.get('billed_input', r['inp'] + r['cr'] + r['cc'])


def desc(vals):
    v = sorted(vals); n = len(v)
    mean = sum(v) / n
    med = v[n // 2] if n % 2 else (v[n // 2 - 1] + v[n // 2]) / 2
    gm = math.exp(sum(math.log(x) for x in v if x > 0) / len([x for x in v if x > 0]))
    return n, mean, med, gm, v[0], v[-1]


print("=" * 78)
print("B. PLAN PHASE - descriptive call-level comparison")
print("=" * 78)
ob = [billed(r) for r in old_plan]
nb = [billed(r) for r in new_plan]
for lbl, v in [('OLD (/ti)', ob), ('NEW (Plan)', nb)]:
    n, mean, med, gm, lo, hi = desc(v)
    print(f"{lbl:12} n={n:<3} mean={mean:>12,.0f}  median={med:>11,.0f}  geomean={gm:>11,.0f}  "
          f"range=[{lo:,} .. {hi:,}]")
print(f"  ratio of means    = {(sum(ob)/len(ob))/(sum(nb)/len(nb)):.2f}x")
print(f"  ratio of medians  = {desc(ob)[2]/desc(nb)[2]:.2f}x")
print(f"  ratio of geomeans = {desc(ob)[3]/desc(nb)[3]:.2f}x")
print(f"  overlap: min(old)={min(ob):,}  max(new)={max(nb):,}  "
      f"{'NO OVERLAP - complete separation' if min(ob) > max(nb) else 'overlapping'}")
new_clusters = collections.Counter(r.get('repo', r.get('src', '?')) for r in new_plan)
old_contexts = collections.Counter(r.get('label') or r.get('src', '?') for r in old_plan)
print(f"  clustering: old observations span {len(old_contexts)} recorded contexts; "
      f"new calls span {len(new_clusters)} runs/cases")
print("  inference: not reported; calls within a run are not independent experimental units")
print()

print("  output tokens:")
oo = [r['out'] for r in old_plan]
no = [r['out'] for r in new_plan]
for lbl, v in [('OLD (/ti)', oo), ('NEW (Plan)', no)]:
    n, mean, med, gm, lo, hi = desc(v)
    print(f"  {lbl:12} n={n:<3} mean={mean:>10,.0f}  median={med:>9,.0f}  range=[{lo:,} .. {hi:,}]")
print(f"    ratio of medians = {desc(oo)[2]/desc(no)[2]:.2f}x   ratio of means = "
      f"{(sum(oo)/len(oo))/(sum(no)/len(no)):.2f}x")
print()

# ---------- C. SHIP ----------
print("=" * 78)
print("C. SHIP PHASE - observed corpus")
print("=" * 78)
ship = [r for r in lf_rows if r['phase'] == 'Ship']
tickets = set((r['repo'], r['ticket']) for r in lf_rows)
print(f"  tickets observed on new side : {len(tickets)}")
print(f"  Ship-phase LLM calls         : {len(ship)}")
print(f"  old-side Ship cost (SURCC-6) : ${cost(*[cc_spine[3][k] for k in ('inp','out','cr','cc')]):.2f}"
      f"   ({cc_spine[3]['inp']+cc_spine[3]['cr']+cc_spine[3]['cc']:,} billed tokens)")
print()

# ---------- D. old-side chain rows for context ----------
print("=" * 78)
print("D. OLD-SIDE corpus inventory (all recoverable measured command regions)")
print("=" * 78)
print(f"{'cmd':7}{'model':26}{'label':22}{'billed tok':>13}{'output':>10}{'$ @opus':>10}")
for r in sorted(old_rows + [old_plan[-1]], key=lambda r: (r['cmd'], -billed(r))):
    print(f"{r['cmd']:7}{r['model']:26}{str(r.get('label')):22}{billed(r):>13,}{r['out']:>10,}"
          f"{cost(r['inp'],r['out'],r['cr'],r['cc']):>10.2f}")
