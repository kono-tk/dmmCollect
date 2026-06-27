import json
import re
import os

paths = {
    "books": r"L:\dmmBookStore\data.json",
    "dojin": r"L:\dmmDojin\data.json",
    "dlsite": r"L:\dlsite\data.json",
    "video": r"M:\SEARCH\data.json"
}

correct_pattern = re.compile(r"^\d{4}年\d{2}月\d{2}日\s+\d{2}:\d{2}$")

for mode, path in paths.items():
    if not os.path.exists(path):
        print(f"{mode}: {path} does not exist.")
        continue
    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            data = json.load(f)
        
        items = data.get("items", []) if isinstance(data, dict) else data
        dirty_items = []
        for item in items:
            pdate = item.get("purchase_date")
            if pdate and pdate != "N/A" and not correct_pattern.match(pdate):
                dirty_items.append((item.get("cid") or item.get("product_id"), pdate))
        
        print(f"Mode: {mode} | Total: {len(items)} | Dirty: {len(dirty_items)}")
        for cid, pdate in dirty_items[:10]:
            print(f"  CID: {cid} | Date: {repr(pdate)}")
            
    except Exception as e:
        print(f"Error reading {mode}: {e}")
