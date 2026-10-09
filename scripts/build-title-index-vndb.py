"""Build the v1 title index from a VNDB database dump (candidate replacement for the
PotatoDBMapper snapshot). Same record format: {"names": [...], "references": [...]}.

Usage: python -I scripts/build-title-index-vndb.py <dump-root> <existing-index.json.gz> <out.json.gz>

<dump-root> is an extracted https://dl.vndb.org/dump/vndb-db-latest.tar.zst (contains db/ and
TIMESTAMP). The existing index only contributes vndb->bangumi links. Reads no user lists, votes
or descriptions. VNDB data is ODbL 1.0 / DbCL 1.0: a shipped index derived from it must carry that
license and attribution (see the integration handoff).
"""
import gzip, hashlib, json, os, sys, unicodedata

root, existing, out = sys.argv[1], sys.argv[2], sys.argv[3]
db = os.path.join(root, 'db')
BS = chr(92)


def unescape(v):
    if v == BS + 'N':
        return None
    if BS not in v:
        return v
    o, i = [], 0
    while i < len(v):
        if v[i] == BS and i + 1 < len(v):
            o.append({'t': '\t', 'n': '\n', 'r': '\r', BS: BS}.get(v[i + 1], v[i + 1])); i += 2
        else:
            o.append(v[i]); i += 1
    return ''.join(o)


def rows(name):
    cols = open(os.path.join(db, name + '.header'), encoding='utf-8').read().strip().split('\t')
    with open(os.path.join(db, name), encoding='utf-8') as f:
        for line in f:
            yield dict(zip(cols, (unescape(x) for x in line.rstrip('\n').split('\t'))))


def key(s):
    return ''.join(ch for ch in unicodedata.normalize('NFKC', s or '').lower() if ch.isalnum())


names, refs = {}, {}
def add(vid, value):
    if value and 0 < len(value) < 500 and key(value):
        lst = names.setdefault(vid, [])
        if value not in lst:
            lst.append(value)

for r in rows('vn'):
    names.setdefault(r['id'], []); refs[r['id']] = {'vndb:' + r['id']}
# Official titles first (original language first), then unofficial titles, then aliases.
titles = sorted(rows('vn_titles'), key=lambda t: (t['id'], t['official'] != 't'))
for t in titles:
    if t['id'] in names:
        add(t['id'], t['title']); add(t['id'], t['latin'])
for r in rows('vn'):
    for a in (r['alias'] or '').split('\n'):
        add(r['id'], a.strip())

release_vns, release_ok = {}, {}
for r in rows('releases'):
    release_ok[r['id']] = r['official'] == 't' or r['patch'] == 't'
for r in rows('releases_vn'):
    release_vns.setdefault(r['id'], []).append(r['vid'])
for t in rows('releases_titles'):
    vns = release_vns.get(t['id'], [])
    # Bundle releases name several works and would only add noise to a per-work index.
    if len(vns) == 1 and vns[0] in names and t['mtl'] != 't' and release_ok.get(t['id']):
        work = names[vns[0]]
        if key(t['title']) not in {key(n) for n in work}:
            add(vns[0], t['title'])
ext = {r['id']: (r['site'], r['value']) for r in rows('extlinks')}
for r in rows('releases_extlinks'):
    site = ext.get(r['link'])
    vns = release_vns.get(r['id'], [])
    if site and site[0] == 'steam' and len(vns) == 1 and vns[0] in refs and site[1].isdigit():
        refs[vns[0]].add('steam:' + site[1])

for rec in json.load(gzip.open(existing))['records']:
    v = next((x[5:] for x in rec['references'] if x.startswith('vndb:')), None)
    if v and v in refs:
        refs[v].update(x for x in rec['references'] if x.startswith('bangumi:'))

def order(r):
    return (0 if r.startswith('vndb:') else 1 if r.startswith('bangumi:') else 2, r)

records = [{'names': names[v][:100], 'references': sorted(refs[v], key=order)[:100]} for v in sorted(names, key=lambda v: int(v[1:])) if names[v]]
stamp = open(os.path.join(root, 'TIMESTAMP')).read().strip()
result = {'version': 1, 'source': 'https://vndb.org/d14 (ODbL 1.0 / DbCL 1.0); bangumi links from PotatoDBMapper',
          'snapshot': stamp, 'records': records}
payload = json.dumps(result, ensure_ascii=False, separators=(',', ':')).encode('utf-8')
with open(out, 'wb') as f:
    f.write(gzip.compress(payload, compresslevel=9, mtime=0))
print(stamp, len(records), sum(len(r['names']) for r in records), len(payload), os.path.getsize(out),
      hashlib.sha256(open(out, 'rb').read()).hexdigest())
