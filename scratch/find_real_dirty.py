import json
import re
import os

paths = {
    "books": r"L:\dmmBookStore\data.json",
    "dojin": r"L:\dmmDojin\data.json",
    "dlsite": r"L:\dlsite\data.json",
    "video": r"M:\SEARCH\data.json"
}

# booksの標準フォーマット: yyyy/MM/dd HH:mm
pattern_books = re.compile(r"^\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}$")
# dojin/dlsiteの標準フォーマット: yyyy-MM-dd
pattern_dojin = re.compile(r"^\d{4}-\d{2}-\d{2}$")
# videoの標準フォーマット: yyyy年MM月dd日 HH:mm
pattern_video = re.compile(r"^\d{4}年\d{2}月\d{2}日\s+\d{2}:\d{2}$")

for mode, path in paths.items():
    if not os.path.exists(path):
        continue
    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            data = json.load(f)
        
        items = data.get("items", []) if isinstance(data, dict) else data
        real_dirty = []
        for item in items:
            pdate = item.get("purchase_date")
            if not pdate or pdate == "N/A":
                continue
            
            pdate_str = str(pdate).strip()
            
            # 各モードごとの判定
            is_ok = False
            if mode == "video" and pattern_video.match(pdate_str):
                is_ok = True
            elif mode == "books" and pattern_books.match(pdate_str):
                is_ok = True
            elif (mode == "dojin" or mode == "dlsite") and pattern_dojin.match(pdate_str):
                is_ok = True
                
            if not is_ok:
                real_dirty.append((item.get("cid") or item.get("product_id"), pdate))
                
        print(f"Mode: {mode} | Real Dirty: {len(real_dirty)}")
        for cid, pdate in real_dirty[:20]:
            print(f"  CID: {cid} | Date: {repr(pdate)}")
            
    except Exception as e:
        print(f"Error reading {mode}: {e}")
