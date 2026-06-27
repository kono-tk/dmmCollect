import json

data_path = r"M:\SEARCH\data.json"
try:
    with open(data_path, "rb") as f:
        content = f.read(5000) # 先頭の一部をバイナリで読み込む
    
    # "purchase_date" 付近を検索してダンプ
    idx = 0
    while True:
        idx = content.find(b"purchase_date", idx)
        if idx == -1:
            break
        # その周辺の 100 バイトをダンプ
        start = max(0, idx - 20)
        end = min(len(content), idx + 80)
        chunk = content[start:end]
        print(f"Index {idx}: {repr(chunk)}")
        idx += 1
        if idx >= len(content):
            break
            
except Exception as e:
    print("Error:", e)
