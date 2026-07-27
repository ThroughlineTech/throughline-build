"""Aggregate Arm B (deterministic runner) LLM calls from the vendored event logs.

Reads:  ../data/arm-b/events/<run>/*.jsonl
Writes: lf_rows.json  (consumed by stats.py, sens.py, models.py)

Run this first. All paths are repo-relative - nothing outside the repo is required.
"""
import json, glob, os, collections

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, '..', 'data')

PH = {0: 'Plan', 1: 'Implement', 2: 'Review', 3: 'Ship', 4: 'Chain', 5: 'New'}

# One directory per run; each run appears exactly once. The SessionId dedup below is a
# guard so a duplicated log dropped in later can never double-count.
SOURCES = sorted(glob.glob(os.path.join(DATA, 'arm-b', 'events', '*', '*.jsonl')))
if not SOURCES:
    raise SystemExit("no event logs found under " + os.path.normpath(DATA) + "/arm-b/events/")

rows = []
seen_sessions = set()
dropped_dupes = 0
for f in SOURCES:
    repo = f.replace('\\', '/').split('/')[-2]
    for line in open(f, encoding='utf-8'):
        line = line.strip()
        if not line:
            continue
        try:
            e = json.loads(line)
        except Exception:
            continue
        if e.get('Kind') != 1:
            continue
        sid = e.get('SessionId')
        if sid in seen_sessions:
            dropped_dupes += 1
            continue
        seen_sessions.add(sid)
        d = e.get('Data', {}) or {}
        vendor = d.get('vendor') or ''
        inp = d.get('input_tokens') or 0
        cr = d.get('cache_read_tokens') or 0
        cc = d.get('cache_create_tokens') or 0
        cached_input = d.get('cached_input_tokens') or 0
        # Anthropic reports uncached input, cache read, and cache create as disjoint fields.
        # OpenAI reports total input with cached_input as a subset; adding cache_read again would
        # double-count it. Store a normalized total for cross-vendor descriptive comparisons.
        billed_input = inp + cc if vendor == 'openai' and cached_input else inp + cr + cc
        rows.append(dict(
            repo=repo, runfile=os.path.basename(f), ticket=str(e.get('TicketId')),
            phase=PH.get(e.get('Phase'), str(e.get('Phase'))),
            model=d.get('model') or '',
            vendor=vendor,
            build_version=e.get('build_version') or '',
            inp=inp,
            out=d.get('output_tokens') or 0,
            cr=cr,
            cc=cc,
            cached_input=cached_input,
            billed_input=billed_input,
            ms=d.get('wall_clock_ms') or 0,
        ))

nruns = len(set(f.replace('\\', '/').split('/')[-2] for f in SOURCES))
print("sources: %d log files across %d runs" % (len(SOURCES), nruns))
print("dedup:   dropped %d LlmCall events with already-seen SessionIds" % dropped_dupes)
print("total LlmCall events:", len(rows))
print()

agg = collections.defaultdict(collections.Counter)
tix = collections.defaultdict(set)
for r in rows:
    a = agg[r['repo']]
    a['calls'] += 1
    a['in'] += r['inp']; a['out'] += r['out']; a['cr'] += r['cr']; a['cc'] += r['cc']; a['ms'] += r['ms']
    tix[r['repo']].add(r['ticket'])

hdr = f"{'repo':22}{'calls':>6}{'tix':>5}{'out':>10}{'cache_read':>13}{'cache_cr':>11}{'input':>12}{'wall_min':>10}"
print(hdr)
print('-' * len(hdr))
G = collections.Counter()
for r in sorted(agg):
    a = agg[r]; G.update(a)
    print(f"{r:22}{a['calls']:>6}{len(tix[r]):>5}{a['out']:>10,}{a['cr']:>13,}{a['cc']:>11,}"
          f"{a['in']:>12,}{a['ms']/60000:>10.1f}")
print('-' * len(hdr))
ntix = sum(len(v) for v in tix.values())
print(f"{'TOTAL':22}{G['calls']:>6}{ntix:>5}{G['out']:>10,}{G['cr']:>13,}{G['cc']:>11,}"
      f"{G['in']:>12,}{G['ms']/60000:>10.1f}")
print()
print("models seen:", dict(collections.Counter(r['model'] for r in rows if r['model'])))
print()

pagg = collections.defaultdict(collections.Counter)
for r in rows:
    p = pagg[r['phase']]
    p['calls'] += 1
    p['in'] += r['inp']; p['out'] += r['out']; p['cr'] += r['cr']; p['cc'] += r['cc']
print(f"{'phase':12}{'calls':>7}{'out':>11}{'cache_read':>13}{'cache_cr':>11}{'mean_out':>10}")
for p in ['Plan', 'Implement', 'Review', 'Ship', 'Chain', 'New']:
    if p not in pagg:
        continue
    a = pagg[p]
    print(f"{p:12}{a['calls']:>7}{a['out']:>11,}{a['cr']:>13,}{a['cc']:>11,}{a['out']/a['calls']:>10,.0f}")

out = os.path.join(HERE, 'lf_rows.json')
with open(out, 'w', encoding='utf-8', newline='\r\n') as out_file:
    json.dump(rows, out_file, indent=0)
    out_file.write('\n')
print()
print("wrote", out)
