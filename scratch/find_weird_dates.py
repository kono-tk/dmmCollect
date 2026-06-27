import json

data_path = r"M:\SEARCH\data.json"
try:
    with open(data_path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)
    
    items = data.get("items", [])
    weird_items = []
    
    for item in items:
        pdate = item.get("purchase_date", "")
        # N/A や 空文字、"20" から始まる正常そうな日付以外のものを探す
        pdate_str = str(pdate).strip()
        if pdate_str and pdate_str != "N/A" and not pdate_str.startswith("20"):
            weird_items.append((item.get("cid"), pdate))
            
    print(f"Weird dates count: {len(weird_items)}")
    for cid, pdate in weird_items:
        print(f"CID: {cid} | Date: {repr(pdate)}")
        
except Exception as e:
    print("Error:", e)
