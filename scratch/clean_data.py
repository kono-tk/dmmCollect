import json
import os
import shutil
from datetime import datetime

data_path = 'M:/SEARCH/data.json'
backup_path = f'M:/SEARCH/data.json.{datetime.now().strftime("%Y%m%d-%H%M%S")}.bak'

if not os.path.exists(data_path):
    print("data.json not found")
    exit(1)

# バックアップ作成
shutil.copyfile(data_path, backup_path)
print(f"Backup created: {backup_path}")

with open(data_path, 'r', encoding='utf-8') as f:
    root = json.load(f)

items = root.get('items', [])
print(f"Original items: {len(items)}")

# 1. 重複した Huge Neo たまき 125cm-Ocup の処理
snyd_entries = [item for item in items if item.get('cid') == 'snyd00051' or item.get('idx') == 'snyd00051']
print(f"Found {len(snyd_entries)} entries for snyd00051:")
for entry in snyd_entries:
    print(f"  Title: {entry.get('title')}, Purchase Date: {entry.get('purchase_date')}, Keys: {list(entry.keys())}")

main_entry = None
duplicate_entry = None

for entry in snyd_entries:
    if 'image_filename' in entry:
        main_entry = entry
    else:
        duplicate_entry = entry

if main_entry and duplicate_entry:
    print("Merging snyd00051 entries...")
    # タイトルをマイナス記号 (U+2212) に統一
    main_entry['title'] = "Huge Neo たまき 125cm−Ocup"
    # 購入日を 2012年11月18日 16:55 に統一
    main_entry['purchase_date'] = "2012年11月18日 16:55"
    
    # 重複エントリをリストから除外
    items.remove(duplicate_entry)
    print("Duplicate entry removed, main entry updated.")
else:
    print("Could not find main or duplicate entry for snyd00051")

# 2. hazuh00006 の誤紐づけのクリーンアップ
real_title = "このオッパイがスゴい。脱いだら鼻血が飛び出る極上恵体。4パコ収録"
hazuh_entries = [item for item in items if item.get('cid') == 'hazuh00006']
print(f"Found {len(hazuh_entries)} entries with CID hazuh00006")

fixed_count = 0
for entry in items:
    if entry.get('cid') == 'hazuh00006':
        title = entry.get('title', '')
        if title != real_title:
            # 本物以外の CID を削除
            entry.pop('cid')
            fixed_count += 1
            print(f"  Cleared CID for: {title}")

print(f"Cleared wrong CID hazuh00006 for {fixed_count} entries.")

root['items'] = items
with open(data_path, 'w', encoding='utf-8') as f:
    json.dump(root, f, ensure_ascii=False, indent=2)

print("data.json clean up finished successfully")
