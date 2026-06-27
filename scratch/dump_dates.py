import json

data_path = r"M:\SEARCH\data.json"
try:
    with open(data_path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)
    
    items = data.get("items", [])
    print("=== DUMP DATES ===")
    count = 0
    for item in items:
        pdate = item.get("purchase_date")
        if pdate:
            print(f"CID: {item.get('cid')} -> Date: {repr(pdate)}")
            count += 1
            if count >= 30:
                break
except Exception as e:
    print("Error:", e)
