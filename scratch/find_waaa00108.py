import json

data_path = r"M:\SEARCH\data.json"
try:
    with open(data_path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)
    
    items = data.get("items", [])
    for item in items:
        if item.get("cid") == "waaa00108":
            print(json.dumps(item, indent=2, ensure_ascii=False))
            break
except Exception as e:
    print("Error:", e)
