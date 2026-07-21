import json
import collections

with open(r'L:\dmmBookStore\data.json', encoding='utf-8') as f:
    raw = json.load(f)

# { "items": [...] } 形式
data = raw.get('items', [])
print(f'総件数: {len(data)}')

if data:
    print(f'最初のアイテムのキー: {list(data[0].keys())}')

fields = ['purchase_date', 'description', 'genres', 'pages', 'maker']
missing_counts = collections.Counter()

for item in data:
    for field in fields:
        if field not in item or item[field] is None:
            missing_counts[field] += 1

print()
print('フィールド欠損数:')
for field in fields:
    print(f'  {field}: {missing_counts[field]} 件欠けている')

# 詳細取得対象の件数（BooksScraper.cs フェーズ3の条件）
items_to_fetch = [
    item for item in data
    if item.get('subtitle') is not None
    and item.get('detail_url') is not None
    and (
        item.get('purchase_date') is None or
        item.get('description') is None or
        item.get('genres') is None or
        item.get('pages') is None or
        item.get('maker') is None
    )
]
print(f'\n詳細取得対象合計: {len(items_to_fetch)} 件')

# 各フィールドが唯一の欠損である件数を調べる
for missing_field in fields:
    other_fields = [f for f in fields if f != missing_field]
    only_this_missing = [
        item for item in items_to_fetch
        if item.get(missing_field) is None
        and all(item.get(f) is not None for f in other_fields)
    ]
    print(f'  {missing_field} のみ欠けている: {len(only_this_missing)} 件')

# subtitle がない、またはdetail_url がないものも確認
no_subtitle = [item for item in data if item.get('subtitle') is None]
no_detail_url = [item for item in data if item.get('detail_url') is None]
print(f'\nsubtitle なし: {len(no_subtitle)} 件')
print(f'detail_url なし: {len(no_detail_url)} 件')

# makerの例を確認
maker_missing = [item for item in items_to_fetch if item.get('maker') is None]
print(f'\nmakerが欠けている詳細取得対象: {len(maker_missing)} 件')
print('例（先頭3件）:')
for item in maker_missing[:3]:
    print(f'  subtitle={repr(item.get("subtitle","")[:50])}')
    print(f'  purchase_date={item.get("purchase_date")} description={item.get("description") is not None} genres={item.get("genres") is not None} pages={item.get("pages")} maker={item.get("maker")}')
