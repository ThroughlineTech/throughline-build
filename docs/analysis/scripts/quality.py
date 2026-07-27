import json, glob, os, collections

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, '..', 'data')
KIND = {0: 'StateChange', 1: 'LlmCall', 2: 'WorkerSpawn', 3: 'Verdict',
        4: 'Unknown4', 5: 'SideEffect', 6: 'ChainStart', 7: 'K7', 8: 'K8', 9: 'K9'}
PH = {0: 'Plan', 1: 'Implement', 2: 'Review', 3: 'Ship', 4: 'Chain', 5: 'New'}

# Same corpus as agg_lf.py: one directory per run under data/arm-b/events/. Dedup key is the
# full event identity, so a duplicated log can never inflate verdict or state-change counts.
SOURCES = sorted(glob.glob(os.path.join(DATA, 'arm-b', 'events', '*', '*.jsonl')))
if not SOURCES:
    raise SystemExit("no event logs found under " + os.path.normpath(DATA) + "/arm-b/events/")

ev = []
seen = set()
dropped = 0
for f in SOURCES:
    repo = f.replace('\\', '/').split('/')[-2]
    for line in open(f, encoding='utf-8'):
        if not line.strip():
            continue
        try:
            e = json.loads(line)
        except Exception:
            continue
        key = (e.get('SessionId'), e.get('Timestamp'), e.get('Kind'),
               str(e.get('TicketId')), e.get('Phase'))
        if key in seen:
            dropped += 1
            continue
        seen.add(key)
        e['_repo'] = repo
        ev.append(e)

print(f"dedup: dropped {dropped} duplicate events")

print("total events:", len(ev))
print("kind mix:", {KIND.get(k, k): v for k, v in
                   sorted(collections.Counter(e.get('Kind') for e in ev).items())})
print()

print("=== Verdicts (Kind=3) by phase ===")
vs = [e for e in ev if e.get('Kind') == 3]
c = collections.Counter()
for e in vs:
    d = e.get('Data', {}) or {}
    c[(PH.get(e.get('Phase')), d.get('kind') or d.get('status'))] += 1
for k in sorted(c, key=lambda x: (str(x[0]), str(x[1]))):
    print(f"  {str(k[0]):11}{str(k[1]):14}{c[k]:>5}")
print()

print("=== Review verdicts per repo (quality proxy) ===")
rv = collections.defaultdict(collections.Counter)
for e in vs:
    if PH.get(e.get('Phase')) != 'Review':
        continue
    d = e.get('Data', {}) or {}
    rv[e['_repo']][d.get('kind') or d.get('status') or '?'] += 1
print(f"{'repo':22}{'verdicts':>10}  breakdown")
for r in sorted(rv):
    tot = sum(rv[r].values())
    print(f"{r:22}{tot:>10}  {dict(rv[r])}")
print()

print("=== First review verdict per ticket ===")
first_review = {}
for e in sorted((x for x in vs if PH.get(x.get('Phase')) == 'Review'),
                key=lambda x: x.get('Timestamp') or ''):
    key = (e['_repo'], str(e.get('TicketId')))
    if key in first_review:
        continue
    d = e.get('Data', {}) or {}
    first_review[key] = d.get('kind') or d.get('status') or '?'
first_counts = collections.Counter(first_review.values())
print(f"  reviewed tickets: {len(first_review)}")
print(f"  first verdict Pass: {first_counts.get('Pass', 0)}")
print(f"  first verdict Rework: {first_counts.get('Rework', 0)}")
print()

print("=== rework rounds: Implement LlmCalls per (repo,ticket) beyond the first ===")
imp = collections.Counter()
for e in ev:
    if e.get('Kind') == 1 and PH.get(e.get('Phase')) == 'Implement':
        imp[(e['_repo'], str(e.get('TicketId')))] += 1
rew = {k: v - 1 for k, v in imp.items() if v > 1}
print(f"  tickets with >1 implement call: {len(rew)} / {len(imp)}")
for k in sorted(rew):
    print(f"    {k[0]:20} tkt {k[1]:>4}  extra rounds: {rew[k]}")
print()

print("=== checks_failed_count distribution (Review) ===")
cf = collections.Counter()
for e in vs:
    if PH.get(e.get('Phase')) != 'Review':
        continue
    d = e.get('Data', {}) or {}
    if 'checks_failed_count' in d:
        cf[d['checks_failed_count']] += 1
print("  ", dict(sorted(cf.items())))
print()

print("=== SideEffect actions (determinism evidence) ===")
se = collections.Counter()
for e in ev:
    if e.get('Kind') == 5:
        se[(e.get('Data', {}) or {}).get('action')] += 1
for k, v in se.most_common(30):
    print(f"  {str(k):26}{v:>6}")
print()

print("=== Ship-phase events (all kinds) ===")
sp = collections.Counter()
for e in ev:
    if PH.get(e.get('Phase')) == 'Ship':
        d = e.get('Data', {}) or {}
        sp[(KIND.get(e.get('Kind')), d.get('action') or d.get('to') or '')] += 1
for k, v in sp.most_common(30):
    print(f"  {str(k):40}{v:>6}")
