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
    public class BooksScraper : BaseScraper
    {
        private static readonly Regex TotalItemsPattern = new(@"全(\d+)件", RegexOptions.Compiled);
        private readonly HttpClient _httpClient = new();

        public BooksScraper(Dictionary<string, string> urls, bool headless, int slowMo, bool isDebugMode, bool verbose = false)
            : base(urls, headless, slowMo, isDebugMode, verbose)
        {
        }

        private async Task<int> GetTotalBookPagesAsync()
        {
            if (Page == null) return 1;
            try
            {
                string totalItemsText = await Page.Locator(AppConstants.BOOK_TOTAL_ITEMS_SELECTOR).First.InnerTextAsync(new() { Timeout = 10000 });
                var match = TotalItemsPattern.Match(totalItemsText);
                if (!match.Success)
                {
                    Console.WriteLine("[WARNING] 総アイテム数が見つかりませんでした。1ページのみ処理します。");
                    return 1;
                }
                int totalItems = int.Parse(match.Groups[1].Value);
                return (totalItems + 19) / 20; // 1ページ20アイテム
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 総ページ数の取得に失敗: {ex.Message}");
                return 1;
            }
        }

        public async Task<Dictionary<string, object>?> ScrapeBookDetailPageAsync(string detailUrl)
        {
            var details = new Dictionary<string, object>();
            try
            {
                await using var detailPage = await Context!.NewPageAsync();
                await detailPage.GotoAsync(detailUrl, new() { Timeout = 30000, WaitUntil = WaitUntilState.DOMContentLoaded });

                await CloseAdPopupIfPresentAsync(detailPage);

                try
                {
                    // ナビゲーション用の dl が先に検知されてしまうのを防ぐため、
                    // 詳細テーブルの具体的な項目（著者、ページ数等を示す testid）を優先して待つ
                    await detailPage.WaitForSelectorAsync("span[data-testid='volume-detail-info-total-pages'], a[data-testid='volume-detail-info-author'], dl dt", new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
                }
                catch
                {
                    try
                    {
                        await detailPage.WaitForSelectorAsync(AppConstants.BOOK_DETAIL_TABLE_SELECTOR, new() { State = WaitForSelectorState.Visible, Timeout = 2000 });
                    }
                    catch
                    {
                        Console.WriteLine($"[WARNING] 作品詳細テーブルが見つかりません: {detailUrl}");
                        return null;
                    }
                }

                string pageContent = await detailPage.ContentAsync();
                var parser = new HtmlParser();
                var soup = await parser.ParseDocumentAsync(pageContent);

                // JSON-LD
                string? jsonLdDesc = null;
                List<string>? jsonLdGenres = null;
                List<string>? jsonLdAuthors = null;

                var jsonLdScript = soup.QuerySelector("script[type='application/ld+json']");
                if (jsonLdScript != null)
                {
                    try
                    {
                        var jsonNode = JsonNode.Parse(jsonLdScript.TextContent);
                        if (jsonNode != null)
                        {
                            jsonLdDesc = jsonNode["description"]?.ToString()?.Trim();
                            var subjectOf = jsonNode["subjectOf"];
                            if (subjectOf != null)
                            {
                                var genreNode = subjectOf["genre"];
                                if (genreNode is JsonArray genresArray)
                                {
                                    jsonLdGenres = genresArray.Select(g => g?.ToString()?.Trim()).Where(g => !string.IsNullOrEmpty(g)).ToList()!;
                                }

                                var authorNode = subjectOf["author"];
                                if (authorNode is JsonObject authorObj)
                                {
                                    var nameNode = authorObj["name"];
                                    if (nameNode is JsonArray authorsArray)
                                    {
                                        jsonLdAuthors = authorsArray.Select(a => a?.ToString()?.Trim()).Where(a => !string.IsNullOrEmpty(a)).ToList()!;
                                    }
                                    else if (nameNode != null)
                                    {
                                        jsonLdAuthors = new List<string> { nameNode.ToString().Trim() };
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(jsonLdDesc))
                {
                    details["description"] = jsonLdDesc;
                }
                else
                {
                    var descTag = soup.QuerySelector(AppConstants.BOOK_DESCRIPTION_SELECTOR);
                    if (descTag != null && !string.IsNullOrEmpty(descTag.TextContent))
                    {
                        details["description"] = descTag.TextContent.Trim();
                    }
                    else
                    {
                        var metaDesc = soup.QuerySelector("meta[name='description']") ?? soup.QuerySelector("meta[name='Description']");
                        string? metaContent = metaDesc?.GetAttribute("content");
                        if (!string.IsNullOrEmpty(metaContent))
                        {
                            details["description"] = metaContent.Trim();
                        }
                    }
                }

                // 購入日
                var purchaseTags = soup.QuerySelectorAll(AppConstants.BOOK_PURCHASE_DATE_SELECTOR);
                if (purchaseTags.Length >= 2)
                {
                    details["purchase_date"] = purchaseTags[1].TextContent.Trim();
                }
                else if (purchaseTags.Length == 1)
                {
                    string purchaseDateText = purchaseTags[0].TextContent.Trim();
                    if (!purchaseDateText.Contains("購入日"))
                    {
                        details["purchase_date"] = purchaseDateText;
                    }
                }
                else
                {
                    // 代替判定
                    var altDiv = soup.QuerySelector("div[data-testid=\"purchased-date\"]");
                    if (altDiv != null)
                    {
                        var span = altDiv.QuerySelector("span");
                        if (span != null)
                        {
                            details["purchase_date"] = span.TextContent.Trim();
                        }
                    }
                }

                // 詳細テーブル解析
                var tableElements = soup.QuerySelectorAll(AppConstants.BOOK_DETAIL_TABLE_SELECTOR);
                if (tableElements.Length == 0)
                {
                    // 代替セレクター
                    var tableRows = soup.QuerySelectorAll("table.text-xs tbody tr, table tbody tr");
                    foreach (var row in tableRows)
                    {
                        var th = row.QuerySelector("th");
                        var td = row.QuerySelector("td");
                        if (th != null && td != null)
                        {
                            string key = th.TextContent.Trim().Replace("：", "").Replace(":", "");
                            if (AppConstants.BOOK_DETAIL_KEYS_MAP.TryGetValue(key, out string? targetKey))
                            {
                                if (key == "ジャンル" || key == "作家")
                                {
                                    var values = td.QuerySelectorAll("a").Select(a => a.TextContent.Trim()).Where(v => !string.IsNullOrEmpty(v)).ToList();
                                    details[targetKey] = values;
                                }
                                else
                                {
                                    var link = td.QuerySelector("a");
                                    details[targetKey] = link != null ? link.TextContent.Trim() : td.TextContent.Trim();
                                }
                            }
                        }
                    }
                }
                else
                {
                    foreach (var dl in tableElements)
                    {
                        var dt = dl.QuerySelector("dt");
                        var dd = dl.QuerySelector("dd");
                        if (dt != null && dd != null)
                        {
                            string key = dt.TextContent.Trim();
                            if (AppConstants.BOOK_DETAIL_KEYS_MAP.TryGetValue(key, out string? targetKey))
                            {
                                if (key == "ジャンル" || key == "作家")
                                {
                                    var values = dd.QuerySelectorAll("a").Select(a => a.TextContent.Trim()).ToList();
                                    details[targetKey] = values;
                                }
                                else
                                {
                                    details[targetKey] = dd.TextContent.Trim();
                                }
                            }
                        }
                    }
                }

                if (jsonLdGenres != null && jsonLdGenres.Count > 0)
                {
                    details["genres"] = jsonLdGenres;
                }
                if (jsonLdAuthors != null && jsonLdAuthors.Count > 0)
                {
                    details["performers"] = jsonLdAuthors;
                }

                var cleaned = new Dictionary<string, object>();
                foreach (var kvp in details)
                {
                    if (kvp.Value != null) cleaned[kvp.Key] = kvp.Value;
                }
                return cleaned;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] 書籍詳細の取得エラー (URL: {detailUrl}): {ex.Message}");
                return null;
            }
        }

        private async Task<(string? downloadUrl, string? productId)> GetBookDownloadInfoAsync(ILocator itemLoc)
        {
            try
            {
                var downloadLinkLoc = itemLoc.Locator(AppConstants.BOOK_DOWNLOAD_LINK_SELECTOR).First;
                string? href = await downloadLinkLoc.GetAttributeAsync("href", new() { Timeout = 5000 });
                if (!string.IsNullOrEmpty(href))
                {
                    string downloadUrl = new Uri(new Uri(Page!.Url), href).ToString();
                    var uri = new Uri(downloadUrl);
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    string? productId = query.Get("product_id");
                    return (downloadUrl, productId);
                }
            }
            catch
            {
                // ignore
            }
            return (null, null);
        }

        private async Task<string?> DownloadBookImageAsync(ILocator itemLoc, string subtitle, string imageDir)
        {
            try
            {
                var imageLoc = itemLoc.Locator(AppConstants.BOOK_IMAGE_SELECTOR).First;
                string? imageUrl = await imageLoc.GetAttributeAsync("src", new() { Timeout = 5000 });
                if (!string.IsNullOrEmpty(imageUrl))
                {
                    if (imageUrl.StartsWith("//"))
                    {
                        imageUrl = "https:" + imageUrl;
                    }

                    string filename = Uri.UnescapeDataString(Path.GetFileName(imageUrl));
                    string savePath = Path.Combine(imageDir, filename);

                    if (!File.Exists(savePath))
                    {
                        Directory.CreateDirectory(imageDir);
                        byte[] bytes = await _httpClient.GetByteArrayAsync(imageUrl);
                        await File.WriteAllBytesAsync(savePath, bytes);
                        Console.WriteLine($"  [DOWNLOAD IMG] {subtitle} -> {filename}");
                    }
                    return filename;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] 「{subtitle}」の画像ダウンロードに失敗: {ex.Message}");
            }
            return null;
        }

        public async Task ScrapeAndDownloadBooksAsync(DataManager dataManager, string imageDir, int startPage, int endPage, string contentDir)
        {
            Console.WriteLine("--- Booksモード開始 ---");

            var (seriesList, standaloneBooks) = await CollectAllSeriesUrlsAsync(startPage, endPage, imageDir);
            var allBooks = await CollectAllBookItemsAsync(seriesList, standaloneBooks, imageDir);

            Console.WriteLine("中間データを data.json に保存します...");
            foreach (var bookData in allBooks)
            {
                dataManager.UpdateOrAddEntry(bookData, isBook: true);
            }
            dataManager.SaveData();

            await FetchMissingBookDetailsAsync(dataManager);

            Console.WriteLine("最終データを data.json に保存します...");
            dataManager.SaveData();

            await DownloadAllBooksAsync(dataManager, contentDir);

            Console.WriteLine("--- Booksモード完了 ---");
        }

        private async Task<(List<Dictionary<string, string>> seriesList, List<Dictionary<string, object>> standaloneBooks)> CollectAllSeriesUrlsAsync(int startPage, int endPage, string imageDir)
        {
            Console.WriteLine("[フェーズ1/3] 全ライブラリページを巡回し、シリーズ情報を収集します...");
            var seriesList = new List<Dictionary<string, string>>();
            var standaloneBooks = new List<Dictionary<string, object>>();

            if (Page == null) return (seriesList, standaloneBooks);

            string baseUrl = Urls["library_url"];
            await Page.GotoAsync(baseUrl.Replace("{page_num}", "1"), new() { WaitUntil = WaitUntilState.DOMContentLoaded });

            await CloseAdPopupIfPresentAsync();

            int totalPages = await GetTotalBookPagesAsync();
            int effectiveEndPage = Math.Min(endPage, totalPages);
            Console.WriteLine($"全{totalPages}ページのうち、{startPage}ページから{effectiveEndPage}ページまでを走査します。");

            for (int pageNum = startPage; pageNum <= effectiveEndPage; pageNum++)
            {
                Console.WriteLine($"  [フェーズ1/3] ページ {pageNum} / {effectiveEndPage} を走査中...");
                if (pageNum > 1)
                {
                    await Page.GotoAsync(baseUrl.Replace("{page_num}", pageNum.ToString()), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                    await CloseAdPopupIfPresentAsync();
                }

                try
                {
                    await Page.WaitForSelectorAsync(AppConstants.BOOK_SERIES_LIST_SELECTOR, new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
                }
                catch
                {
                    Console.WriteLine($"{pageNum}ページでシリーズ一覧の読み込みがタイムアウト。スキップします。");
                    continue;
                }

                var seriesLocators = await Page.Locator(AppConstants.BOOK_SERIES_LIST_SELECTOR).AllAsync();
                foreach (var seriesLoc in seriesLocators)
                {
                    try
                    {
                        var titleLoc = seriesLoc.Locator(AppConstants.BOOK_SERIES_TITLE_SELECTOR).First;
                        string seriesTitle = await titleLoc.InnerTextAsync();

                        if (await seriesLoc.Locator(AppConstants.BOOK_VOLUMES_LINK_SELECTOR).CountAsync() > 0)
                        {
                            var volumesLink = seriesLoc.Locator(AppConstants.BOOK_VOLUMES_LINK_SELECTOR).First;
                            string? href = await volumesLink.GetAttributeAsync("href");
                            if (!string.IsNullOrEmpty(href))
                            {
                                string volumesUrl = new Uri(new Uri(Page.Url), href).ToString();
                                seriesList.Add(new Dictionary<string, string>
                                {
                                    ["series_title"] = seriesTitle,
                                    ["list_url"] = volumesUrl
                                });
                            }
                        }
                        else
                        {
                            string? href = await titleLoc.GetAttributeAsync("href");
                            string detailUrl = !string.IsNullOrEmpty(href) ? new Uri(new Uri(Page.Url), href).ToString() : "";
                            string? imageFilename = await DownloadBookImageAsync(seriesLoc, seriesTitle, imageDir);
                            var (downloadUrl, productId) = await GetBookDownloadInfoAsync(seriesLoc);

                            standaloneBooks.Add(new Dictionary<string, object>
                            {
                                ["series_title"] = seriesTitle,
                                ["subtitle"] = seriesTitle,
                                ["title"] = seriesTitle,
                                ["detail_url"] = detailUrl,
                                ["download_url"] = downloadUrl ?? "",
                                ["product_id"] = productId ?? "",
                                ["image_filename"] = imageFilename ?? ""
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"書籍情報の取得中にエラー: {ex.Message}");
                    }
                }
            }

            Console.WriteLine($"[フェーズ1完了] シリーズ: {seriesList.Count}件、スタンドアロン書籍: {standaloneBooks.Count}件を収集しました。");
            return (seriesList, standaloneBooks);
        }

        private async Task<List<JsonObject>> CollectAllBookItemsAsync(List<Dictionary<string, string>> seriesList, List<Dictionary<string, object>> standaloneBooks, string imageDir)
        {
            Console.WriteLine("[フェーズ2/3] 単行本リストを巡回し、基本情報を収集します...");
            var allBooks = new List<JsonObject>();

            // スタンドアロン
            foreach (var book in standaloneBooks)
            {
                var obj = new JsonObject();
                foreach (var kvp in book)
                {
                    obj[kvp.Key] = JsonValue.Create(kvp.Value);
                }
                allBooks.Add(obj);
            }

            if (Page == null) return allBooks;

            // シリーズ
            for (int i = 0; i < seriesList.Count; i++)
            {
                var series = seriesList[i];
                Console.WriteLine($"  [フェーズ2/3] シリーズ {i + 1} / {seriesList.Count} を処理中: {series["series_title"]}");
                try
                {
                    await Page.GotoAsync(series["list_url"], new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                    await Page.WaitForSelectorAsync(AppConstants.BOOK_SUBTITLE_ITEM_SELECTOR, new() { State = WaitForSelectorState.Visible, Timeout = 10000 });

                    var items = await Page.Locator(AppConstants.BOOK_SUBTITLE_ITEM_SELECTOR).AllAsync();
                    foreach (var itemLoc in items)
                    {
                        var linkLoc = itemLoc.Locator(AppConstants.BOOK_SUBTITLE_LINK_SELECTOR).First;
                        string subtitle = await linkLoc.InnerTextAsync();
                        string? href = await linkLoc.GetAttributeAsync("href");
                        string detailUrl = !string.IsNullOrEmpty(href) ? new Uri(new Uri(Page.Url), href).ToString() : "";
                        var (downloadUrl, productId) = await GetBookDownloadInfoAsync(itemLoc);
                        string? imageFilename = await DownloadBookImageAsync(itemLoc, subtitle, imageDir);

                        allBooks.Add(new JsonObject
                        {
                            ["series_title"] = series["series_title"],
                            ["subtitle"] = subtitle,
                            ["title"] = subtitle,
                            ["detail_url"] = detailUrl,
                            ["download_url"] = downloadUrl ?? "",
                            ["product_id"] = productId ?? "",
                            ["image_filename"] = imageFilename ?? ""
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"シリーズ「{series["series_title"]}」の処理中にエラー: {ex.Message}");
                }
            }

            Console.WriteLine($"[フェーズ2完了] {allBooks.Count}件の単行本の基本情報を収集しました。");
            return allBooks;
        }

        private async Task FetchMissingBookDetailsAsync(DataManager dataManager)
        {
            Console.WriteLine("[フェーズ3/3] 未取得の書籍の詳細情報を収集します...");
            var itemsToFetch = dataManager.Data.Where(item =>
                item.ContainsKey("subtitle") && item["subtitle"] != null &&
                item.ContainsKey("detail_url") && item["detail_url"] != null &&
                (!item.ContainsKey("purchase_date") || item["purchase_date"] == null ||
                 !item.ContainsKey("description") || item["description"] == null ||
                 !item.ContainsKey("genres") || item["genres"] == null ||
                 !item.ContainsKey("pages") || item["pages"] == null ||
                 !item.ContainsKey("maker") || item["maker"] == null)
            ).ToList();

            Console.WriteLine($"詳細情報を取得する必要がある書籍は {itemsToFetch.Count} 件です。");

            int currentIdx = 0;
            foreach (var item in itemsToFetch)
            {
                currentIdx++;
                string subtitle = item["subtitle"]!.ToString();
                string detailUrl = item["detail_url"]!.ToString();

                Console.WriteLine($"  [フェーズ3/3] {currentIdx} / {itemsToFetch.Count} 件目の詳細情報を取得中: {subtitle}");
                var details = await ScrapeBookDetailPageAsync(detailUrl);

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

                    if (updatedData.ContainsKey(AppConstants.NO_DETAIL_PAGE_FLAG))
                    {
                        updatedData.Remove(AppConstants.NO_DETAIL_PAGE_FLAG);
                    }

                    dataManager.UpdateOrAddEntry(updatedData, isBook: true);
                }
                else
                {
                    var noDetailData = new JsonObject();
                    foreach (var pair in item)
                    {
                        noDetailData[pair.Key] = pair.Value?.DeepClone();
                    }
                    noDetailData[AppConstants.NO_DETAIL_PAGE_FLAG] = "詳細情報ページへのリンクを持たないコンテンツ";
                    dataManager.UpdateOrAddEntry(noDetailData, isBook: true);
                }
            }

            Console.WriteLine("[フェーズ3完了] 全て詳細情報を収集しました。");
        }

        public async Task DownloadAllBooksAsync(DataManager dataManager, string contentDir)
        {
            Console.WriteLine("[フェーズ4/4] コンテンツのダウンロードを開始します...");
            Directory.CreateDirectory(contentDir);

            var itemsToDownload = new List<JsonObject>();
            foreach (var item in dataManager.Data)
            {
                if (!item.ContainsKey("download_url") || item["download_url"] == null || item["download_url"]!.ToString() == "DOWNLOAD_LINK_NOT_FOUND")
                {
                    continue;
                }

                string? contentFilename = item.ContainsKey("content_filename") ? item["content_filename"]?.ToString() : null;
                if (string.IsNullOrEmpty(contentFilename) || contentFilename == "DOWNLOAD_FAILED")
                {
                    itemsToDownload.Add(item);
                }
                else
                {
                    string filePath = Path.Combine(contentDir, contentFilename);
                    if (!File.Exists(filePath))
                    {
                        Console.WriteLine($"  [MISSING-FILE] content_filenameはあるがファイルが存在しません: {contentFilename}");
                        itemsToDownload.Add(item);
                    }
                }
            }

            Console.WriteLine($"ダウンロード対象のコンテンツは {itemsToDownload.Count} 件です。");

            if (itemsToDownload.Count > 0 && Page != null)
            {
                int currentDlIdx = 0;
                foreach (var item in itemsToDownload)
                {
                    currentDlIdx++;
                    string subtitle = item["subtitle"]!.ToString();
                    string downloadUrl = item["download_url"]!.ToString();

                    string? finalFilename = null;
                    for (int retry = 0; retry < 3; retry++)
                    {
                        try
                        {
                            Console.WriteLine($"  [DOWNLOAD] 開始: {subtitle} ({currentDlIdx}/{itemsToDownload.Count}) (試行 {retry + 1}/3)");

                            var download = await Page.RunAndWaitForDownloadAsync(async () =>
                            {
                                try
                                {
                                    await Page.GotoAsync(downloadUrl);
                                }
                                catch
                                {
                                    // ignore navigation aborts
                                }
                            }, new() { Timeout = 300000 });

                            string originalFilename = download.SuggestedFilename;
                            string cleanedSubtitle = FileSanitizer.SanitizeFilename(subtitle);
                            finalFilename = $"{cleanedSubtitle}_{originalFilename}";
                            string savePath = Path.Combine(contentDir, finalFilename);

                            if (File.Exists(savePath))
                            {
                                Console.WriteLine($"  [SKIP] ファイルが既に存在します: {finalFilename}");
                                break;
                            }

                            await download.SaveAsAsync(savePath);
                            Console.WriteLine($"  [SUCCESS] ダウンロード完了: {finalFilename}");
                            break;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  [ERROR] ダウンロード処理でエラー: {subtitle} - {ex.Message}");
                            finalFilename = null;
                            if (retry < 2)
                            {
                                Console.WriteLine("  3秒待機してリトライします...");
                                await Task.Delay(3000);
                            }
                        }
                    }

                    item["content_filename"] = finalFilename ?? "DOWNLOAD_FAILED";
                    dataManager.UpdateOrAddEntry(item, isBook: true);
                    dataManager.SaveData();

                    if (!string.IsNullOrEmpty(finalFilename) && finalFilename != "DOWNLOAD_FAILED")
                    {
                        await Task.Delay(1000);
                    }
                }
            }

            Console.WriteLine("[フェーズ4完了] 全てのダウンロード処理が完了しました。");
        }
    }
}
