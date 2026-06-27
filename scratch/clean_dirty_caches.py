import os
import json
import re

search_roots = [
    r"M:\SEARCH",
    r"H:\misc\VIDEO",
    r"I:\VIDEO",
    r"J:\\",
    r"K:\\",
    r"L:\VIDEO",
    r"M:\VIDEO"
]

correct_pattern = re.compile(r"^\d{4}年\d{2}月\d{2}日\s+\d{2}:\d{2}$")

found_caches = 0
dirty_caches = []

for root in search_roots:
    if not os.path.exists(root):
        continue
    print(f"Scanning root: {root}")
    for dirpath, _, filenames in os.walk(root):
        for filename in filenames:
            if filename == ".FileInfo.csharp.json":
                cache_path = os.path.join(dirpath, filename)
                found_caches += 1
                try:
                    with open(cache_path, "r", encoding="utf-8-sig") as f:
                        cache_data = json.load(f)
                    
                    listing = cache_data.get("listing", {})
                    items = listing.get("items", [])
                    is_dirty = False
                    for item in items:
                        pdate = item.get("purchase_date")
                        if pdate and pdate != "N/A" and not correct_pattern.match(pdate):
                            print(f"Dirty cache found: {cache_path}")
                            print(f"  CID: {item.get('cid')} | Date: {repr(pdate)}")
                            is_dirty = True
                            break
                    if is_dirty:
                        dirty_caches.append(cache_path)
                except Exception as e:
                    print(f"Error reading {cache_path}: {e}")

print(f"Total caches scanned: {found_caches}")
print(f"Dirty caches count: {len(dirty_caches)}")

# もし汚いキャッシュがあれば削除する
if dirty_caches:
    print("Deleting dirty cache files...")
    for cache_path in dirty_caches:
        try:
            os.remove(cache_path)
            print(f"Deleted: {cache_path}")
        except Exception as e:
            print(f"Failed to delete {cache_path}: {e}")
