import json
import re

data_path = r"M:\SEARCH\data.json"
try:
    with open(data_path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)
    
    items = data.get("items", [])
    print(f"Total items: {len(items)}")
    
    # yyyy年MM月dd日 HH:mm または yyyy/MM/dd HH:mm などの正規表現
    # 正しい日付フォーマット: 「2026年06月20日 12:00」のような形式
    correct_pattern = re.compile(r"^\d{4}年\d{2}月\d{2}日\s+\d{2}:\d{2}$")
    
    dirty_items = []
    for item in items:
        pdate = item.get("purchase_date")
        if not pdate:
            continue
        if not correct_pattern.match(pdate):
            dirty_items.append((item.get("cid"), pdate))
            
    print(f"Dirty items count: {len(dirty_items)}")
    for cid, pdate in dirty_items[:20]:
        print(f"CID: {cid} | Date: {repr(pdate)}")
        
except Exception as e:
    print("Error:", e)
