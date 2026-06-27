import json
import datetime

data_path = r"M:\SEARCH\data.json"
try:
    with open(data_path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)
    
    items = data.get("items", [])
    now = datetime.datetime.now()
    
    future_items = []
    for item in items:
        pdate = item.get("purchase_date", "")
        if not pdate or pdate == "N/A":
            continue
        
        # 簡易的に年を取り出してチェック
        # 例: 2026年06月20日 18:00
        try:
            # 2026年08月14日 21:05
            # 正規表現で日付をパース
            import re
            m = re.match(r"(\d{4})年(\d{2})月(\d{2})日\s+(\d{2}):(\d{2})", pdate)
            if m:
                yr, mo, dy, hr, mn = map(int, m.groups())
                dt = datetime.datetime(yr, mo, dy, hr, mn)
                if dt > now:
                    future_items.append((item.get("cid"), pdate, item.get("title")))
        except Exception as e:
            pass
            
    print(f"Future items in json: {len(future_items)}")
    for cid, pdate, title in future_items:
        print(f"CID: {cid} | Date: {pdate} | Title: {title}")
        
except Exception as e:
    print("Error:", e)
