import sys
from bs4 import BeautifulSoup

sys.stdout.reconfigure(encoding='utf-8')

with open(r"c:\Users\mocha\work\dmmCollect\detail_test.html", "r", encoding="utf-8") as f:
    soup = BeautifulSoup(f.read(), "html.parser")

print("--- Searching for links with '?tag=' or similar ---")
tag_links = soup.find_all('a', href=lambda x: x and ('tag=' in x or 'tag' in x))
print(f"Found {len(tag_links)} tag links")
for a in tag_links[:15]:
    print(f"href: {a.get('href')} | text: {a.text.strip()}")
