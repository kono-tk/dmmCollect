import json
import re

data_path = r"M:\SEARCH\data.json"
try:
    with open(data_path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)
    
    items = data.get("items", [])
    print(f"Total items: {len(items)}")
    
    weird = []
    for item in items:
        pdate = item.get("purchase_date", "")
        if not pdate:
            continue
        pdate_str = str(pdate).strip()
        # 長さが30文字以上、または改行が入っている、または「購入」や「詳細」や日本語で日付以外の漢字が含まれるものを探す
        # 正常な日付は「年」「月」「日」の漢字しか含まない
        # 漢字・ひらがな・カタカナなどをチェック
        has_other_kanji = False
        # 「年」「月」「日」以外の漢字が含まれているか？
        kanji_only_date = re.sub(r"[0-9\s：:]", "", pdate_str)
        # kanji_only_date に「年」「月」「日」以外の文字（日付関連以外の文字）が含まれていたら怪しい
        non_date_chars = re.sub(r"[年月日/ -]", "", kanji_only_date)
        
        if len(pdate_str) > 25 or "\n" in pdate_str or len(non_date_chars) > 0:
            weird.append((item.get("cid"), pdate, non_date_chars))
            
    print(f"Suspicious dates found: {len(weird)}")
    for cid, pdate, chars in weird[:50]:
        print(f"CID: {cid} | Chars: {repr(chars)} | Date: {repr(pdate)}")
        
except Exception as e:
    print("Error:", e)
