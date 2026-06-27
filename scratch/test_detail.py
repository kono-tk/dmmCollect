import asyncio
from playwright.async_api import async_playwright

async def main():
    async with async_playwright() as p:
        browser = await p.chromium.launch(headless=True)
        page = await browser.new_page()
        
        # 年齢確認通過
        await page.goto("https://www.dmm.co.jp/")
        try:
            await page.click("a:has-text('R18')")
            await page.wait_for_timeout(1000)
        except:
            pass
        try:
            await page.click("a:has-text('はい')")
            await page.wait_for_timeout(1000)
        except:
            pass

        cid = "h_1472bukgc00005"
        old_url = f"https://www.dmm.co.jp/digital/videoa/-/detail/=/cid={cid}/"
        print(f"Navigating to old URL: {old_url}")
        await page.goto(old_url)
        await page.wait_for_timeout(3000)
        
        print(f"Final URL: {page.url}")
        print(f"Title: {await page.title()}")
        
        # 画面上に「販売終了」などの情報があるか確認
        content = await page.content()
        if "販売" in content:
            print("Found '販売' in page content")
        if "見つかりません" in content:
            print("Found '見つかりません' (404) in page content")
            
        await browser.close()

asyncio.run(main())
