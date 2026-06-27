import json
import re

data_path = r"M:\SEARCH\data.json"
try:
    with open(data_path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)
    
    items = data.get("items", [])
    updated_count = 0
    
    # 2026年06月19日 12:27 や 2026/06/19 12:27 などのパターンを抽出
    date_pattern = re.compile(r"(\d{4}[年/-]\d{1,2}[月/-]\d{1,2}日?\s+\d{1,2}:\d{2})")
    
    for item in items:
        purchase_date = item.get("purchase_date")
        if purchase_date and ("\n" in purchase_date or len(purchase_date) > 30):
            match = date_pattern.search(purchase_date)
            if match:
                clean_date = match.group(1).strip()
                print(f"Fixing CID {item.get('cid')}: {repr(purchase_date[:30])}... -> {repr(clean_date)}")
                item["purchase_date"] = clean_date
                updated_count += 1
            else:
                lines = [l.strip() for l in purchase_date.split("\n") if l.strip()]
                if lines:
                    # 最初の行を日付とみなす
                    clean_date = lines[0]
                    print(f"Fixing (fallback) CID {item.get('cid')}: {repr(purchase_date[:30])}... -> {repr(clean_date)}")
                    item["purchase_date"] = clean_date
                    updated_count += 1
                    
    if updated_count > 0:
        with open(data_path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2, ensure_ascii=False)
        print(f"Successfully repaired {updated_count} items in data.json.")
    else:
        print("No items needed repair.")
except Exception as e:
    print("Error:", e)
