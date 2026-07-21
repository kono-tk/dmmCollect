"""
ジャンルに EROTOON を含む書籍を books_history.json に「視聴済み」として登録する。
"""
import json
import os
from datetime import datetime, timezone

DATA_JSON = r'L:\dmmBookStore\data.json'
HISTORY_JSON = r'L:\dmmBookStore\books_history.json'

# data.json の読み込み
with open(DATA_JSON, encoding='utf-8') as f:
    raw = json.load(f)
data = raw.get('items', [])

# EROTOON を含むアイテムを抽出
erotoon_items = []
for item in data:
    genres = item.get('genres')
    if not genres:
        continue
    # genres はリスト形式
    if isinstance(genres, list):
        if any('EROTOON' in str(g).upper() for g in genres):
            erotoon_items.append(item)
    elif isinstance(genres, str):
        if 'EROTOON' in genres.upper():
            erotoon_items.append(item)

print(f'EROTOON ジャンルを含む書籍: {len(erotoon_items)} 件')

# 現在時刻（ISO 8601）
now = datetime.now().strftime('%Y-%m-%dT%H:%M:%S')

# 既存の履歴を読み込む
if os.path.exists(HISTORY_JSON):
    with open(HISTORY_JSON, encoding='utf-8') as f:
        history = json.load(f)
    if not isinstance(history, list):
        history = []
else:
    history = []

# 既存の product_id セット
existing_ids = set()
for entry in history:
    pid = entry.get('product_id') or entry.get('cid')
    if pid:
        existing_ids.add(pid)

# 追加・更新
added = 0
updated = 0
for item in erotoon_items:
    product_id = item.get('product_id', '')
    subtitle = item.get('subtitle', item.get('title', ''))

    if not product_id:
        continue

    # 既存エントリを除去（先頭に最新を挿入するため）
    before = len(history)
    history = [e for e in history if (e.get('product_id') or e.get('cid')) != product_id]
    if len(history) < before:
        updated += 1
    else:
        added += 1

    new_entry = {
        "product_id": product_id,
        "title": subtitle,
        "last_viewed_date": now,
        "view_type": "all"
    }
    history.insert(0, new_entry)

print(f'新規追加: {added} 件, 更新: {updated} 件')

# 上限 3000 件
history = history[:3000]

# バックアップ
if os.path.exists(HISTORY_JSON):
    ts = datetime.now().strftime('%Y%m%d_%H%M%S')
    bak = HISTORY_JSON + f'.{ts}.bak'
    import shutil
    shutil.copy2(HISTORY_JSON, bak)
    print(f'バックアップ作成: {bak}')

# 保存
with open(HISTORY_JSON, 'w', encoding='utf-8') as f:
    json.dump(history, f, ensure_ascii=False, indent=2)

print(f'保存完了: {HISTORY_JSON} ({len(history)} 件)')
