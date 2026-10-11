"""Convert a read-only PotatoDBMapper snapshot to the application's compact index.

Usage: python scripts/build-title-index.py path/to/vn_mapper.db
The runtime uses the committed gzip file and does not require Python or SQLite.
"""
import gzip
import hashlib
import json
import sqlite3
import sys
from pathlib import Path

database = Path(sys.argv[1]).resolve()
connection = sqlite3.connect(f'file:{database.as_posix()}?mode=ro', uri=True)
records = {}
for vndb, bgm, bgm_similarity, steam, steam_similarity in connection.execute('SELECT VndbId,BgmId,BgmSimilarity,SteamId,SteamSimilarity FROM map'):
    refs = [f'vndb:v{vndb}']
    if bgm and bgm > 0 and bgm_similarity is not None and bgm_similarity >= .95:
        refs.append(f'bangumi:{bgm}')
    if steam and steam > 0 and steam_similarity is not None and steam_similarity >= .95:
        refs.append(f'steam:{steam}')
    records[vndb] = {'names': [], 'references': refs}
for vndb, title in connection.execute('SELECT VndbId,Title FROM title ORDER BY VndbId,Title'):
    if vndb in records and isinstance(title, str) and title.strip() and '\ufffd' not in title:
        records[vndb]['names'].append(title.strip())
result = {'version': 1, 'source': 'https://github.com/GoldenPotato137/PotatoDBMapper', 'snapshotSha256': hashlib.sha256(database.read_bytes()).hexdigest(), 'records': [row for row in records.values() if row['names']]}
destination = Path(__file__).resolve().parent.parent / 'data/title-index.json.gz'
destination.parent.mkdir(exist_ok=True)
with destination.open('wb') as output:
    with gzip.GzipFile(filename='', fileobj=output, mode='wb', mtime=0) as compressed:
        compressed.write(json.dumps(result, ensure_ascii=False, separators=(',', ':')).encode('utf-8'))
print(json.dumps({'records': len(result['records']), 'titles': sum(len(row['names']) for row in result['records']), 'compressedBytes': destination.stat().st_size}))
