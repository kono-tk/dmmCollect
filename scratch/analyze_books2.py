import json

with open(r'L:\dmmBookStore\data.json', encoding='utf-8') as f:
    raw = json.load(f)

data = raw.get('items', [])

# pagesがnullまたは未存在で、かつ description や genres は存在するアイテムの確認
pages_null = [item for item in data if isinstance(item, dict) and item.get('pages') is None]
print(f'pages が null/未存在: {len(pages_null)} 件')

# そのうち description と genres がある（詳細取得済みのはず）のに pages だけ null のもの
detail_fetched_no_pages = [
    item for item in pages_null
    if item.get('description') is not None
    and item.get('genres') is not None
    and item.get('purchase_date') is not None
]
print(f'  詳細取得済みだが pages だけ null: {len(detail_fetched_no_pages)} 件')

# そのうちの例
for item in detail_fetched_no_pages[:5]:
    subtitle = item.get('subtitle', '')
    pages = item.get('pages')
    genres = item.get('genres')
    desc_len = len(item.get('description',''))
    print(f'  subtitle={repr(subtitle[:50])}')
    print(f'    pages={pages!r}, genres={genres}, desc_len={desc_len}')

# 詳細取得が全くされていない（descriptionもない）もの
completely_missing = [
    item for item in pages_null
    if item.get('description') is None
    and item.get('genres') is None
]
print(f'\n全く詳細未取得（description・genres共にnull）: {len(completely_missing)} 件')

# pagesがある場合のサンプルデータ
pages_exists = [item for item in data if isinstance(item, dict) and item.get('pages') is not None]
print(f'\npages がある: {len(pages_exists)} 件')
for item in pages_exists[:3]:
    print(f'  pages={item.get("pages")!r}')
