import json, os

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, '..', 'data')
lf_rows = json.load(open(os.path.join(HERE, 'lf_rows.json')))

old = {}
for f in [os.path.join(DATA, 'arm-a', 'runs.jsonl')]:
    for line in open(f, encoding='utf-8'):
        if not line.strip():
            continue
        r = json.loads(line)
        old[(r.get('session_id'), r.get('command'), r.get('ts'))] = r

old_ti = []
for r in old.values():
    if r.get('command') != '/ti' or not r.get('model'):
        continue
    old_ti.append(dict(model=r['model'],
                       b=(r.get('input') or 0) + (r.get('cache_read') or 0) + (r.get('cache_create') or 0)
                         + (r.get('subagent_input') or 0) + (r.get('subagent_cache_read') or 0)
                         + (r.get('subagent_cache_create') or 0),
                       out=(r.get('output') or 0) + (r.get('subagent_output') or 0)))
old_ti.append(dict(model='claude-opus-4-7', b=7434 + 2095454 + 172304, out=39990))  # SURCC-6

new_plan = [dict(model=r['model'], b=r.get('billed_input', r['inp'] + r['cr'] + r['cc']),
                 out=r['out'])
            for r in lf_rows if r['phase'] == 'Plan']
new_plan.append(dict(model='claude-opus-4-7', b=14 + 250066 + 26060, out=14490))  # SURLF-6


def med(v):
    v = sorted(v); n = len(v)
    return v[n // 2] if n % 2 else (v[n // 2 - 1] + v[n // 2]) / 2


SCEN = [
    ("all data (baseline)", lambda r: True, lambda r: True),
    ("new: Claude models only (drop gpt-5.5)", lambda r: True, lambda r: not r['model'].startswith('gpt')),
    ("old: drop max outlier (12.3M)", lambda r: r['b'] < 12_000_000, lambda r: True),
    ("old: opus-4-7 only", lambda r: r['model'] == 'claude-opus-4-7', lambda r: True),
    ("both trimmed: old opus-only, new Claude-only", lambda r: r['model'] == 'claude-opus-4-7',
     lambda r: not r['model'].startswith('gpt')),
    ("new: sonnet-4-6 only", lambda r: True, lambda r: r['model'] == 'claude-sonnet-4-6'),
]

print("DESCRIPTIVE SENSITIVITY - Plan phase, billed input tokens")
print()
h = f"{'scenario':46}{'n_old':>6}{'n_new':>6}{'med ratio':>11}{'mean ratio':>11}{'sep':>5}"
print(h); print('-' * len(h))
for name, fo, fn in SCEN:
    a = [r['b'] for r in old_ti if fo(r)]
    b = [r['b'] for r in new_plan if fn(r)]
    sep = 'yes' if min(a) > max(b) else 'no'
    print(f"{name:46}{len(a):>6}{len(b):>6}{med(a)/med(b):>10.2f}x"
          f"{(sum(a)/len(a))/(sum(b)/len(b)):>10.2f}x{sep:>5}")
print()

print("DESCRIPTIVE SENSITIVITY - Plan phase, OUTPUT tokens")
print(h); print('-' * len(h))
for name, fo, fn in SCEN:
    a = [r['out'] for r in old_ti if fo(r)]
    b = [r['out'] for r in new_plan if fn(r)]
    sep = 'yes' if min(a) > max(b) else 'no'
    print(f"{name:46}{len(a):>6}{len(b):>6}{med(a)/med(b):>10.2f}x"
          f"{(sum(a)/len(a))/(sum(b)/len(b)):>10.2f}x{sep:>5}")
print()

print("Inference is not reported: most new-side calls are clustered within four runs.")
print()

# gpt-5.5 accounting note
g = [r for r in lf_rows if r['model'].startswith('gpt')]
print(f"gpt-5.5 rows: n={len(g)}  cache_create sum={sum(r['cc'] for r in g):,} "
      f"(OpenAI reports no cache-create; its cached input lands in 'input')")
print(f"   gpt input sum={sum(r['inp'] for r in g):,}  cache_read sum={sum(r['cr'] for r in g):,}")
