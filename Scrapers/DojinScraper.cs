using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using AngleSharp.Html.Parser;
using AngleSharp.Dom;

namespace dmmCollect.Scrapers
{
    public class DojinScraper : BaseScraper
    {
        private static readonly Regex ProductIdPattern = new(@"product_id=([^/]+)", RegexOptions.Compiled);
        private readonly HttpClient _httpClient = new();

        public DojinScraper(Dictionary<string, string> urls, bool headless, int slowMo, bool isDebugMode, bool verbose = false)
            : base(urls, headless, slowMo, isDebugMode, verbose)
        {
        }

        public async Task ScrapeDojinAsync(DataManager dataManager, string imageDir)
        {
            Console.WriteLine("--- Dojinモード開始 ---");

            if (Page == null) throw new InvalidOperationException("Page is not initialized.");

            Console.WriteLine("[フェーズ1/3] ライブラリからメタ情報を抽出します...");
            string baseUrl = Urls["library_url"];
            await Page.GotoAsync(baseUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

            await CloseAdPopupIfPresentAsync();

            Console.WriteLine($"ライブラリページにアクセスしました: {Page.Url}");
            Console.WriteLine("無限スクロールで全コンテンツの読み込みを開始します...");

            string scrollableElementSelector = "div.purchasedListAreayWTly";
            try
            {
                await Page.WaitForSelectorAsync(scrollableElementSelector, new() { Timeout = 5000 });
                var scrollableElement = Page.Locator(scrollableElementSelector);

                int lastItemCount = await scrollableElement.Locator("div.localListProductzKID2").CountAsync();

                while (true)
                {
                    await scrollableElement.EvaluateAsync("el => el.scrollTop = el.scrollHeight");
                    await Task.Delay(500);
                    int currentItemCount = await scrollableElement.Locator("div.localListProductzKID2").CountAsync();

                    if (currentItemCount == lastItemCount)
                    {
                        Console.WriteLine($"全{currentItemCount}件のコンテンツを読み込みました。スクロールを完了します。");
                        break;
                    }

                    lastItemCount = currentItemCount;
                    Console.WriteLine($"コンテンツを読み込み中... (現在 {lastItemCount}件)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"無限スクロール中にエラーが発生しました: {ex.Message}");
                await SaveDebugArtifactsAsync(Page);
                return;
            }

            Console.WriteLine("全コンテンツのHTMLから情報を抽出します...");
            var dateGroups = await Page.Locator($"{scrollableElementSelector} > ul > li").AllAsync();

            int updatedCount = 0;
            for (int i = 0; i < dateGroups.Count; i++)
            {
                var dateGroup = dateGroups[i];
                string purchaseDate = "不明な日付";
                try
                {
                    string purchaseDateText = await dateGroup.Locator("p.purchasedListTitleiBWYR").InnerTextAsync();
                    purchaseDate = purchaseDateText.Replace("年", "-").Replace("月", "-").Replace("日", "");
                }
                catch { }

                var productLocators = await dateGroup.Locator("div.localListProductzKID2").AllAsync();
                foreach (var productLoc in productLocators)
                {
                    try
                      {
                        string title = await productLoc.Locator(".productTitleCMVya").InnerTextAsync();
                        var existing = dataManager.FindBookEntry(title);
                        if (existing != null && existing.ContainsKey("image_url") && existing["image_url"] != null)
                        {
                            continue;
                        }

                        var itemData = new JsonObject
                        {
                            ["purchase_date"] = purchaseDate,
                            ["title"] = title,
                            ["subtitle"] = title
                        };

                        var aTag = productLoc.Locator("a").First;
                        string? href = await aTag.GetAttributeAsync("href");
                        if (!string.IsNullOrEmpty(href))
                        {
                            string detailUrl = new Uri(new Uri(Page.Url), href).ToString();
                            itemData["detail_url"] = detailUrl;

                            var match = ProductIdPattern.Match(href);
                            if (match.Success)
                            {
                                // URL の product_id= が作品の識別子。data.json には cid として書く
                                ItemIdentity.Set(itemData, match.Groups[1].Value);
                            }
                        }

                        string? imageUrl = await productLoc.Locator(".listLeftme0sH img").First.GetAttributeAsync("src");
                        if (!string.IsNullOrEmpty(imageUrl))
                        {
                            itemData["image_url"] = new Uri(new Uri(Page.Url), imageUrl).ToString();
                        }

                        string performers = await productLoc.Locator(".circleNameGWNom").InnerTextAsync();
                        itemData["performers"] = new JsonArray(performers);

                        string genres = await productLoc.Locator(".defaultClassmE6be").InnerTextAsync();
                        itemData["genres"] = new JsonArray(genres);

                        if (dataManager.UpdateOrAddEntry(itemData, isBook: true))
                        {
                            updatedCount++;
                        }
                    }
                    catch { }
                }
            }

            if (updatedCount > 0)
            {
                Console.WriteLine($"[フェーズ1完了] {updatedCount}件の新規メタ情報をdata.jsonに保存しました。");
                dataManager.SaveData();
            }
            else
            {
                Console.WriteLine("[フェーズ1完了] 新しいメタ情報はありませんでした。");
            }

            // 画像ダウンロード
            Console.WriteLine("[フェーズ2/3] 不足している画像をダウンロードします...");
            var itemsToDownloadImg = dataManager.Data.Where(item =>
                item.ContainsKey("image_url") && item["image_url"] != null &&
                (!item.ContainsKey("image_filename") || item["image_filename"] == null)
            ).ToList();

            if (itemsToDownloadImg.Count > 0)
            {
                bool imgUpdated = false;
                Directory.CreateDirectory(imageDir);

                foreach (var item in itemsToDownloadImg)
                {
                    try
                    {
                        string imageUrl = item["image_url"]!.ToString();
                        string filename = Uri.UnescapeDataString(Path.GetFileName(imageUrl));
                        string savePath = Path.Combine(imageDir, filename);

                        if (!File.Exists(savePath))
                        {
                            byte[] bytes = await _httpClient.GetByteArrayAsync(imageUrl);
                            await File.WriteAllBytesAsync(savePath, bytes);
                        }

                        item["image_filename"] = filename;
                        if (dataManager.UpdateOrAddEntry(item, isBook: true))
                        {
                            imgUpdated = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"画像ダウンロード失敗: {item["title"]} - {ex.Message}");
                    }
                }

                if (imgUpdated)
                {
                    dataManager.SaveData();
                }
            }
            else
            {
                Console.WriteLine("ダウンロードする新しい画像はありませんでした。");
            }

            // 詳細情報収集
            Console.WriteLine("[フェーズ3/3] 未取得作品の詳細情報を収集します...");
            var itemsToFetchDetails = dataManager.Data.Where(item =>
                item.ContainsKey("detail_url") && item["detail_url"] != null &&
                (!item.ContainsKey(AppConstants.NO_DETAIL_PAGE_FLAG) || item[AppConstants.NO_DETAIL_PAGE_FLAG] == null) &&
                (!item.ContainsKey("genres") || item["genres"] == null || item["genres"]!.AsArray().Count < 2 ||
                 !item.ContainsKey("description") || item["description"] == null)
            ).ToList();

            if (itemsToFetchDetails.Count > 0)
            {
                bool detailsUpdated = false;
                Console.WriteLine($"{itemsToFetchDetails.Count}件の詳細情報を収集します。");

                foreach (var item in itemsToFetchDetails)
                {
                    string title = item["title"]!.ToString();
                    string detailUrl = item["detail_url"]!.ToString();

                    Console.WriteLine($"  [DETAIL] 取得中: {title}");
                    var details = await ScrapeDojinDetailPageAsync(detailUrl);
                    await Task.Delay(1000); // 負荷軽減

                    if (details != null)
                    {
                        var updatedData = new JsonObject();
                        foreach (var pair in item)
                        {
                            updatedData[pair.Key] = pair.Value?.DeepClone();
                        }
                        foreach (var pair in details)
                        {
                            updatedData[pair.Key] = JsonNode.Parse(JsonSerializer.Serialize(pair.Value));
                        }

                        if (dataManager.UpdateOrAddEntry(updatedData, isBook: true))
                        {
                            Console.WriteLine($"  [DETAIL] 詳細情報を更新/上書き: {title}");
                            detailsUpdated = true;
                        }
                    }
                    else
                    {
                        var noDetailItem = new JsonObject();
                        foreach (var pair in item)
                        {
                            noDetailItem[pair.Key] = pair.Value?.DeepClone();
                        }
                        noDetailItem[AppConstants.NO_DETAIL_PAGE_FLAG] = "詳細情報ページへのリンクを持たないコンテンツ";
                        if (dataManager.UpdateOrAddEntry(noDetailItem, isBook: true))
                        {
                            Console.WriteLine($"  [DETAIL] 詳細ページなしフラグを設定: {title}");
                            detailsUpdated = true;
                        }
                    }
                }

                if (detailsUpdated)
                {
                    dataManager.SaveData();
                }
            }
            else
            {
                Console.WriteLine("詳細情報を収集する新しい作品はありませんでした。");
            }

            Console.WriteLine("--- Dojinモード完了 ---");
        }

        private async Task<Dictionary<string, object>?> ScrapeDojinDetailPageAsync(string detailUrl)
        {
            try
            {
                await using var page = await Context!.NewPageAsync();
                await page.GotoAsync(detailUrl, new() { Timeout = 30000, WaitUntil = WaitUntilState.DOMContentLoaded });

                await CloseAdPopupIfPresentAsync(page);

                var linkLocator = page.Locator("a[href*=\"/dc/doujin/-/detail/\"]").First;
                try
                {
                    await linkLocator.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
                    string? productPageUrl = await linkLocator.GetAttributeAsync("href");
                    if (!string.IsNullOrEmpty(productPageUrl))
                    {
                        return await ScrapeDojinProductPageAsync(productPageUrl);
                    }
                }
                catch
                {
                    Console.WriteLine($"  - 作品紹介ページへのリンクが見つかりませんでした: {detailUrl}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"同人作品中間ページの取得エラー (URL: {detailUrl}): {ex.Message}");
            }
            return null;
        }

        private async Task<Dictionary<string, object>?> ScrapeDojinProductPageAsync(string productPageUrl)
        {
            var details = new Dictionary<string, object>();
            try
            {
                await using var page = await Context!.NewPageAsync();
                await page.GotoAsync(productPageUrl, new() { Timeout = 30000, WaitUntil = WaitUntilState.DOMContentLoaded });

                await CloseAdPopupIfPresentAsync(page);

                string pageContent = await page.ContentAsync();
                var parser = new HtmlParser();
                var soup = await parser.ParseDocumentAsync(pageContent);

                var twitterDesc = soup.QuerySelector("meta[name='twitter:description']");
                string? descContent = twitterDesc?.GetAttribute("content");
                if (!string.IsNullOrEmpty(descContent))
                {
                    details["description"] = descContent.Trim();
                }

                // 新しいテーブル
                var table = soup.QuerySelector("table.text-xs");
                if (table != null)
                {
                    var rows = table.QuerySelectorAll("tr");
                    foreach (var row in rows)
                    {
                        var th = row.QuerySelector("th");
                        var td = row.QuerySelector("td");
                        if (th != null && td != null)
                        {
                            string keyJp = th.TextContent.Trim().Replace("：", "").Replace(":", "");
                            if (AppConstants.DOJIN_DETAIL_KEYS_MAP.TryGetValue(keyJp, out string? keyEn))
                            {
                                object value;
                                if (keyJp == "ジャンル")
                                {
                                    var flex = td.QuerySelector("div.flex");
                                    var elements = flex != null ? flex.QuerySelectorAll("a") : td.QuerySelectorAll("a");
                                    value = elements.Select(a => a.TextContent.Trim()).Where(v => !string.IsNullOrEmpty(v)).ToList();
                                }
                                else if (keyJp == "作者")
                                {
                                    var link = td.QuerySelector("a");
                                    value = link != null ? new List<string> { link.TextContent.Trim() } : new List<string> { td.TextContent.Trim() };
                                }
                                else
                                {
                                    var link = td.QuerySelector("a");
                                    value = link != null ? link.TextContent.Trim() : td.TextContent.Trim();
                                }
                                details[keyEn] = value;
                            }
                        }
                    }

                    return details.Count > 0 ? details : null;
                }

                // 旧テーブル (m-productInformation)
                var infoItems = soup.QuerySelectorAll("div.m-productInformation dl.informationList");
                foreach (var item in infoItems)
                {
                    var dt = item.QuerySelector("dt.informationList__ttl");
                    var dd = item.QuerySelector("dd");
                    if (dt != null && dd != null)
                    {
                        string keyJp = dt.TextContent.Trim();
                        if (AppConstants.DOJIN_DETAIL_KEYS_MAP.TryGetValue(keyJp, out string? keyEn))
                        {
                            object value;
                            if (keyJp == "ジャンル")
                            {
                                value = dd.QuerySelectorAll("a.genreTag__txt").Select(a => a.TextContent.Trim()).ToList();
                            }
                            else if (keyJp == "作者")
                            {
                                value = new List<string> { dd.TextContent.Trim() };
                            }
                            else
                            {
                                value = dd.TextContent.Trim();
                            }
                            details[keyEn] = value;
                        }
                    }
                }

                return details.Count > 0 ? details : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"同人作品紹介ページの取得エラー (URL: {productPageUrl}): {ex.Message}");
            }
            return null;
        }
    }
}
