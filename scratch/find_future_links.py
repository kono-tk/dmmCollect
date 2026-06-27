import os
import datetime

flat_dir = r"M:\SEARCH\FLAT"
if not os.path.exists(flat_dir):
    print("FLAT directory does not exist.")
    exit(1)

# 現在のローカル時刻
now = datetime.datetime.now()
print(f"Current local time: {now}")

future_files = []
for filename in os.listdir(flat_dir):
    if filename.endswith(".lnk"):
        filepath = os.path.join(flat_dir, filename)
        try:
            mtime = datetime.datetime.fromtimestamp(os.path.getmtime(filepath))
            # 現在の時刻より未来のもの（あるいは明日の日付以降など）をチェック
            if mtime > now:
                future_files.append((filename, mtime))
        except Exception as e:
            print(f"Error checking {filename}: {e}")

print(f"Future files count: {len(future_files)}")
for name, mtime in future_files[:50]:
    print(f"File: {name} | MTime: {mtime}")
