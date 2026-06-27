import os

html_path = r"C:\Users\mocha\work\dmmCollect\error_page.html"
if os.path.exists(html_path):
    with open(html_path, "r", encoding="utf-8") as f:
        content = f.read()
    
    grid_str = 'class="grid grid-cols-'
    idx = content.find(grid_str)
    if idx != -1:
        sub = content[idx:]
        close_idx = sub.find('</ul>')
        if close_idx != -1:
            print("FOUND GRID CLOSE TAG. Printing next 1500 chars:")
            print(sub[close_idx:close_idx+1500])
        else:
            print("</ul> not found after grid")
    else:
        print("grid class not found")
else:
    print("error_page.html not found")
