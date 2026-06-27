import json
import re

data_path = r"M:\SEARCH\data.json"
with open(data_path, "r", encoding="utf-8") as f:
    data = json.load(f)

# data.json から waaa00108 を探す
target_item = None
for item in data.get("items", []):
    if item.get("cid") == "waaa00108":
        target_item = item
        break

if not target_item:
    print("waaa00108 not found in data.json")
    exit()

print("TARGET TITLE:", target_item["title"])

# FLAT内の実際のファイル名を取得
# (PowerShellの出力から得られたもの)
lnk_name = "「奥さんはこんなしゃぶり方してくれないでしょ？」 チンしゃぶ大好き後輩のこねくり追撃お掃除で何度も何度も射精させられた僕 浮気フェラ逆NTR 月乃ルナ_link"
print("LNK STEM:", lnk_name)

# サニタイズ関数
def sanitize_filename(name):
    # C# [\\/:*?""<>|] -> _
    san = re.sub(r'[\\/:*?"<>|]', '_', name)
    return san.strip().rstrip('.')

def sanitize_remove_colon(name):
    san = re.sub(r'[：:]', '', name)
    san = re.sub(r'[\\/*?"<>|]', '_', san)
    return san.strip().rstrip('.')

def normalize_separators(text):
    return re.sub(r'[_\-\s]+', ' ', text).strip()

exactKey = sanitize_filename(target_item["title"])
searchKey = sanitize_filename(lnk_name)

print("exactKey :", exactKey)
print("searchKey:", searchKey)

# 1. exactMatchCache
print("1. Exact Match:", exactKey == searchKey)

# 2. colonRemovedMatchCache
colonKey = sanitize_remove_colon(target_item["title"])
searchColonKey = sanitize_remove_colon(lnk_name)
print("2. Colon Removed Match:", colonKey == searchColonKey)

# 3. flexibleMatchCache
normExact = normalize_separators(exactKey)
normSearch = normalize_separators(searchKey)
print("3. Flexible Match:", normExact == normSearch)

# 4. partialMatches (san.startswith(searchKey))
print("4. Partial (san.startswith(searchKey)):", exactKey.startswith(searchKey))

# 5. reverseMatches (searchKey.startswith(san))
print("5. Reverse (searchKey.startswith(san)):", searchKey.startswith(exactKey))

# 6. wordMatches (first 3 words)
words_search = [w for w in re.split(r'[_\-\s]+', searchKey) if w]
words_exact = [w for w in re.split(r'[_\-\s]+', exactKey) if w]

print("words_search:", words_search[:4])
print("words_exact:", words_exact[:4])

first3_search = "_".join(words_search[:3]) if len(words_search) >= 3 else None
first3_exact = "_".join(words_exact[:3]) if len(words_exact) >= 3 else None
print("6. Word Match (first 3):", first3_search == first3_exact and first3_search is not None)
