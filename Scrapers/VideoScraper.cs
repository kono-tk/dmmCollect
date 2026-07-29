using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using AngleSharp.Html.Parser;
using AngleSharp.Dom;

namespace dmmCollect.Scrapers
{
    public class VideoScraper : BaseScraper
    {
        private static readonly Regex CidFromHrefPattern = new(@"(?:cid|id)=([a-zA-Z0-9_]+)", RegexOptions.Compiled);
        private static readonly Regex CidFromSrcPattern = new(@"/digital/video/([^/]+)/", RegexOptions.Compiled);
        private static readonly Regex HashPattern = new(@"[_\-]?[a-fA-F0-9]{6,32}$", RegexOptions.Compiled);
        
        private readonly HttpClient _httpClient = new();

        private readonly string _downloadDirectory;

        public VideoScraper(Dictionary<string, string> urls, bool headless, int slowMo, string downloadDirectory, bool isDebugMode, bool verbose = false)
            : base(urls, headless, slowMo, isDebugMode, verbose)
        {
            _downloadDirectory = downloadDirectory;
        }

        public async Task NavigateAndWaitForVideoLibraryAsync()
        {
            if (Page == null) throw new InvalidOperationException("Page is not initialized.");

            Console.WriteLine("マイライブラリに移動します...");
            await Page.GotoAsync(Urls["my_library_search"], new() { WaitUntil = WaitUntilState.DOMContentLoaded });

            await CloseAdPopupIfPresentAsync();

            Console.WriteLine("購入済み商品リストの読み込みを待機します...");

            try
            {
                Console.WriteLine("ネットワークがアイドル状態になるのを待機します...");
                await Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 30000 });
                Console.WriteLine("ネットワークはアイドル状態です。");
            }
            catch (TimeoutException)
            {
                Console.WriteLine("ネットワークアイドルの待機がタイムアウトしました。処理を続行します。");
            }

            var loadingIndicator = Page.Locator(AppConstants.SEARCH_DATA_LOADING_SELECTOR);
            try
            {
                Console.WriteLine("検索データ読み込みインジケーターの表示を待機します...");
                await loadingIndicator.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
                Console.WriteLine("検索データ読み込みインジケーターが表示されました。非表示になるのを待ちます...");
                await loadingIndicator.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 600000 });
                Console.WriteLine("検索データ読み込みインジケーターが非表示になりました。");
            }
            catch (TimeoutException)
            {
                Console.WriteLine("検索データ読み込みインジケーターの処理がタイムアウトまたはスキップされました。");
            }

            var firstItemLocator = Page.Locator(AppConstants.MY_SEARCH_LIST_ITEM_SELECTOR).First;
            var noResultsLocator = Page.Locator(AppConstants.NO_RESULTS_SELECTOR);

            try
            {
                Console.WriteLine("商品リストの最初のアイテム、または「結果なし」メッセージの表示を待機します...");
                await firstItemLocator.Or(noResultsLocator).WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30000 });
                Console.WriteLine("商品リストまたは「結果なし」メッセージが表示されました。");
            }
            catch (TimeoutException)
            {
                Console.WriteLine("商品リストの読み込みがタイムアウトしました。デバッグ情報を保存します。");
                await SaveDebugArtifactsAsync(Page);
                throw;
            }

            Console.WriteLine("商品リストの読み込み完了。");
        }

        // キーワードで絞り込み（指定時）、結果の1ページ目を表示した状態にする。
        // 2026-07 の新レイアウトでは「もっと見る」→ ページネーション（?page=N）に変わったため、
        // 全ページの展開は各処理側が ForEachPageAsync / CollectSearchResultHtmlAsync で行う。
        public async Task<bool> SearchAndExpandAsync(string? keyword = null)
        {
            if (Page == null) throw new InvalidOperationException("Page is not initialized.");

            if (!string.IsNullOrEmpty(keyword))
            {
                // ライブラリのキーワード絞り込みは URL パラメータ ?key=<キーワード> で行われる
                // （キーワード欄はフォーム無しの React 制御入力で打鍵が不安定なため、URLへ直接遷移する）。
                // ページ送りも ?key=...&page=N となるため、後段の全ページ巡回もそのまま機能する。
                string keyUrl = $"{AppConstants.MYLIBRARY_BASE_URL}?key={Uri.EscapeDataString(keyword)}";
                Console.WriteLine($"キーワード '{keyword}' で検索します: {keyUrl}");
                try
                {
                    await Page.GotoAsync(keyUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                    await CloseAdPopupIfPresentAsync();

                    bool hasResults = await WaitForSearchResultsAsync();
                    int itemCount = await Page.Locator(AppConstants.MY_SEARCH_LIST_ITEM_SELECTOR).CountAsync();
                    Console.WriteLine($"キーワード '{keyword}' 検索結果: URL={Page.Url} / 表示件数={itemCount}");
                    if (!hasResults)
                    {
                        Console.WriteLine($"キーワード '{keyword}' に一致する商品が見つかりませんでした。");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"キーワード '{keyword}' の検索結果の読み込み中にエラー/タイムアウトが発生しました: {ex.GetType().Name}: {ex.Message}");
                    await SaveDebugArtifactsAsync(Page);
                    return false;
                }
            }
            else
            {
                // 全件（キーワードなし）の場合も、1ページ目の結果が出ていることを確認する
                try { await WaitForSearchResultsAsync(); } catch (TimeoutException) { }
            }

            return true;
        }

        // 検索/絞り込み後、結果グリッドの読み込みを待つ。アイテムが1件以上あれば true。
        private async Task<bool> WaitForSearchResultsAsync()
        {
            if (Page == null) return false;

            try { await Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }); }
            catch (TimeoutException) { }

            try
            {
                await Page.Locator(AppConstants.MY_SEARCH_LIST_ITEM_SELECTOR).First
                    .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
            }
            catch (TimeoutException) { }

            int count = await Page.Locator(AppConstants.MY_SEARCH_LIST_ITEM_SELECTOR).CountAsync();
            return count > 0;
        }

        // ページネーション（?page=N のURLリンク）から全ページのURLを求める。
        // 1ページ目は現在URL。以降は最大ページ番号までURLテンプレートから生成する。
        private static readonly Regex PageParamPattern = new(@"([?&])page=\d+", RegexOptions.Compiled);
        private async Task<List<string>> GetAllPageUrlsAsync()
        {
            var urls = new List<string>();
            if (Page == null) return urls;

            string hrefsJson;
            try
            {
                hrefsJson = await Page.EvaluateAsync<string>(@"() => {
                    const anchors = Array.from(document.querySelectorAll('ul[data-e2eid=""pagination""] a[href]'));
                    return JSON.stringify(anchors.map(a => a.href));
                }");
            }
            catch
            {
                urls.Add(Page.Url);
                return urls;
            }

            var arr = JsonNode.Parse(hrefsJson) as JsonArray;
            string? template = null;
            int maxPage = 1;
            var pageNumRegex = new Regex(@"[?&]page=(\d+)");
            if (arr != null)
            {
                foreach (var node in arr)
                {
                    string href = node?.ToString() ?? "";
                    var m = pageNumRegex.Match(href);
                    if (!m.Success) continue;
                    template ??= href;   // page= を含む絶対URL（キーワード等の状態も保持している）
                    int p = int.Parse(m.Groups[1].Value);
                    if (p > maxPage) maxPage = p;
                }
            }

            if (template == null)
            {
                // ページネーションなし（単一ページ）
                urls.Add(Page.Url);
                return urls;
            }

            // page=1..max の正規URLをテンプレートから生成（現在位置に依存せず順送りできる）
            for (int p = 1; p <= maxPage; p++)
            {
                urls.Add(PageParamPattern.Replace(template, $"$1page={p}"));
            }
            return urls;
        }

        // 全ページを順に開き、各ページのDOMに対して onPage(現在ページ番号, 総ページ数) を実行する。
        private async Task ForEachPageAsync(Func<int, int, Task> onPage)
        {
            if (Page == null) return;

            var urls = await GetAllPageUrlsAsync();
            int total = urls.Count;
            Console.WriteLine($"全 {total} ページを巡回します。");

            for (int i = 0; i < total; i++)
            {
                // 単一ページ（ページネーションなし）は現在の表示のまま処理する。
                // 複数ページの場合は各ページの ?page=N URL へ確実に移動してから処理する
                // （直前の処理が最終ページで終わっていても、常に1ページ目から順送りできる）。
                if (total > 1)
                {
                    Console.WriteLine($"  ページ {i + 1}/{total} を開きます: {urls[i]}");
                    await Page.GotoAsync(urls[i], new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                    await CloseAdPopupIfPresentAsync();
                    await WaitForSearchResultsAsync();
                }
                await onPage(i + 1, total);
            }
        }

        // 現在の検索結果（全ページ）のグリッドHTMLを連結して返す。キーワード検索結果の保存に使う。
        public async Task<string> CollectSearchResultHtmlAsync()
        {
            if (Page == null) return "";

            var sb = new System.Text.StringBuilder();
            sb.Append("<html><body>\n");

            await ForEachPageAsync(async (pageNum, total) =>
            {
                string gridHtml = await Page.EvaluateAsync<string>(@"() => {
                    const grids = Array.from(document.querySelectorAll('ul.grid'));
                    return grids.map(g => g.outerHTML).join('\n');
                }");
                int cnt = await Page.Locator(AppConstants.MY_SEARCH_LIST_ITEM_SELECTOR).CountAsync();
                Console.WriteLine($"  ページ {pageNum}/{total}: {cnt} 件のアイテムを収集しました。");
                sb.Append(gridHtml);
                sb.Append('\n');
            });

            sb.Append("</body></html>\n");
            return sb.ToString();
        }

        // フェーズ1の全処理（FLAT保存用HTML収集・画像/CID更新・購入日収集）を「1回のページ巡回」でまとめて行う。
        // ページネーション化により各処理を個別に呼ぶと全ページを3周してしまうため、1周に集約する。
        // ページURLは巡回開始時（1ページ目）に一括算出されるので、個別呼び出しで起きていた
        // 「最終ページ始点だと総ページ数を1つ少なく誤検出する」取りこぼしも防げる。
        // 戻り値は FLAT 保存用に集約したグリッドHTML。
        public async Task<string> ProcessAllPagesAsync(string saveDir, DataManager dataManager)
        {
            if (Page == null) return "";

            var sb = new System.Text.StringBuilder();
            sb.Append("<html><body>\n");
            int totalPurchaseUpdated = 0;

            await ForEachPageAsync(async (pageNum, total) =>
            {
                int cnt = await Page.Locator(AppConstants.MY_SEARCH_LIST_ITEM_SELECTOR).CountAsync();
                Console.WriteLine($"  [ページ {pageNum}/{total}] グリッド収集・画像/CID・購入日をまとめて処理します（{cnt}件）...");

                // 1) FLAT保存用のグリッドHTMLを収集（購入日クリックで開くポップアップの影響を避けるため最初に取得する）
                try
                {
                    string gridHtml = await Page.EvaluateAsync<string>(@"() => {
                        const grids = Array.from(document.querySelectorAll('ul.grid'));
                        return grids.map(g => g.outerHTML).join('\n');
                    }");
                    sb.Append(gridHtml);
                    sb.Append('\n');
                }
                catch (Exception ex) { Console.WriteLine($"    [WARN] グリッドHTML収集に失敗（続行）: {ex.Message}"); }

                // 2) 画像ダウンロード・CID更新（読み取りのみ）
                try { await ProcessCurrentPageImagesAsync(saveDir, dataManager); }
                catch (Exception ex) { Console.WriteLine($"    [WARN] 画像/CID処理に失敗（続行）: {ex.Message}"); }

                // 3) 購入日収集（アイテムをクリックしてDOMを変化させるため、必ず最後に行う）
                try { totalPurchaseUpdated += await ProcessPurchaseDatesOnCurrentPageAsync(dataManager); }
                catch (Exception ex) { Console.WriteLine($"    [WARN] 購入日収集に失敗（続行）: {ex.Message}"); }
            });

            sb.Append("</body></html>\n");

            if (totalPurchaseUpdated > 0) dataManager.SaveData();
            Console.WriteLine($"全ページの一括処理が完了しました（購入日更新: {totalPurchaseUpdated}件）。");
            return sb.ToString();
        }

        public async Task CollectPurchaseDatesAsync(DataManager dataManager)
        {
            if (Page == null) throw new InvalidOperationException("Page is not initialized.");

            Console.WriteLine("購入日の収集を開始します...");
            // 新レイアウトはページネーション式のため、全ページを巡回して各ページの購入日を収集する。
            int totalUpdated = 0;
            await ForEachPageAsync(async (pageNum, total) =>
            {
                Console.WriteLine($"  [ページ {pageNum}/{total}] 購入日を収集します...");
                totalUpdated += await ProcessPurchaseDatesOnCurrentPageAsync(dataManager);
            });
            if (totalUpdated > 0) dataManager.SaveData();
            Console.WriteLine($"購入日の収集が完了しました（合計更新: {totalUpdated}件）。");
        }

        private async Task<int> ProcessPurchaseDatesOnCurrentPageAsync(DataManager dataManager)
        {
            if (Page == null) return 0;

            var itemLocators = await Page.Locator("ul.grid li").AllAsync();
            if (itemLocators.Count == 0)
            {
                Console.WriteLine("購入日を収集するアイテムが見つかりませんでした。");
                return 0;
            }

            int totalItems = itemLocators.Count;

            // DOMから全アイテムのインデックス、ID、alt属性を1回で一括取得する
            Console.WriteLine("DOMから全アイテム情報を一括取得しています...");
            var jsonString = await Page.EvaluateAsync<string>(@"() => {
                const results = Array.from(document.querySelectorAll('ul.grid li')).map((li, index) => {
                    const img = li.querySelector('img');
                    return {
                        index: index,
                        id: '',
                        alt: img ? img.getAttribute('alt') : null
                    };
                });
                return JSON.stringify(results);
            }");

            if (string.IsNullOrEmpty(jsonString))
            {
                Console.WriteLine("DOMからのアイテム情報一括取得に失敗しました。");
                return 0;
            }

            var items = System.Text.Json.Nodes.JsonNode.Parse(jsonString) as System.Text.Json.Nodes.JsonArray;
            if (items == null)
            {
                Console.WriteLine("JSONパース結果がJsonArrayではありません。");
                return 0;
            }

            var pendingItems = new List<(int Index, string RawTitle, System.Text.Json.Nodes.JsonObject? Entry)>();
            int skippedCount = 0;

            foreach (var itemNode in items)
            {
                if (itemNode == null) continue;
                int index = itemNode["index"]?.GetValue<int>() ?? -1;
                string? id = itemNode["id"]?.ToString();
                string? alt = itemNode["alt"]?.ToString();

                if (index < 0 || index >= totalItems || string.IsNullOrEmpty(alt))
                {
                    skippedCount++;
                    continue;
                }

                string rawTitle = alt.Trim();
                string normTitle = DataManager.NormalizeTitle(rawTitle);
                var entry = dataManager.FindEntry(normTitle);

                if (entry != null)
                {
                    bool hasDate = entry.ContainsKey("purchase_date") && entry["purchase_date"] != null;
                    if (IsDebugMode)
                    {
                        Console.WriteLine($"[DEBUG] Entry Match: '{rawTitle}' -> Found. HasDate: {hasDate} (value: {entry["purchase_date"]})");
                    }
                    if (hasDate)
                    {
                        skippedCount++;
                        continue;
                    }
                }
                else
                {
                    if (IsDebugMode)
                    {
                        Console.WriteLine($"[DEBUG] Entry Match: '{rawTitle}' -> NOT FOUND (NormTitle: '{normTitle}')");
                    }
                }

                // ユーザーが先行してダウンロードしている可能性を考慮：
                // ダウンロード先ディレクトリがすでに生成されているときは、ダウンロード対象から除外する
                if (!string.IsNullOrEmpty(_downloadDirectory))
                {
                    string cleanTitle = FileSanitizer.SanitizeFilename(rawTitle);
                    string targetFolder = Path.Combine(_downloadDirectory, cleanTitle);
                    if (Directory.Exists(targetFolder))
                    {
                        if (IsDebugMode)
                        {
                            Console.WriteLine($"[DEBUG] Skip Item: '{rawTitle}' -> Download directory already exists at '{targetFolder}'");
                        }
                        skippedCount++;
                        continue;
                    }
                }

                pendingItems.Add((index, rawTitle, entry));
            }

            int itemsWithPurchaseDate = totalItems - pendingItems.Count;
            Console.WriteLine($"全{totalItems}件中、{itemsWithPurchaseDate}件は購入日収集済み（スキップ: {skippedCount}件）");

            if (pendingItems.Count == 0)
            {
                Console.WriteLine("このページのアイテムはすべて購入日収集済みのため、スキップします。");
                return 0;
            }

            int updatedCount = 0;
            int saveInterval = 100;

            for (int i = 0; i < pendingItems.Count; i++)
            {
                var pending = pendingItems[i];
                var itemLoc = itemLocators[pending.Index];
                
                bool result = await ProcessSinglePurchaseItemAsync(itemLoc, pending.RawTitle, pending.Entry, dataManager);
                if (result)
                {
                    updatedCount++;
                    if (updatedCount > 0 && updatedCount % saveInterval == 0)
                    {
                        dataManager.SaveData();
                        Console.WriteLine($"  [中間保存] {updatedCount}件の購入日情報を保存しました");
                    }
                }
                else
                {
                    skippedCount++;
                }

                Console.Write($"\r進行状況: {i + 1}/{pendingItems.Count} (更新: {updatedCount}, スキップ: {skippedCount})");
            }
            Console.WriteLine();

            if (updatedCount > 0)
            {
                dataManager.SaveData();
            }
            Console.WriteLine($"{updatedCount}件の購入日情報を更新しました（スキップ: {skippedCount}件）。");
            return updatedCount;
        }

        private async Task<bool> ProcessSinglePurchaseItemAsync(ILocator itemLoc, string rawTitle, System.Text.Json.Nodes.JsonObject? entry, DataManager dataManager)
        {
            if (Page == null) return false;

            try
            {
                // 既存のポップアップを閉じる
                try
                {
                    var detailPopup = Page.Locator("div.global-dialog");
                    if (await detailPopup.IsVisibleAsync())
                    {
                        var closeBtn = Page.Locator(AppConstants.POPUP_CLOSE_BUTTON_SELECTOR);
                        if (await closeBtn.IsVisibleAsync())
                        {
                            await closeBtn.ClickAsync();
                            await detailPopup.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 3000 });
                        }
                    }
                }
                catch { }

                await itemLoc.ScrollIntoViewIfNeededAsync();
                try
                {
                    await itemLoc.ClickAsync(new() { Timeout = 5000 });
                }
                catch
                {
                    await itemLoc.ClickAsync(new() { Force = true, Timeout = 5000 });
                }

                // ポップアップ待ち
                var popup = Page.Locator("div.global-dialog");
                string purchaseDate = "";
                try
                {
                    await popup.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3000 });
                }
                catch { }

                // スケルトンローダーが消えてコンテンツが実際にロードされるまで待機
                // ポップアップ表示直後はグレーのプレースホルダー状態のことがある
                try
                {
                    await Page.WaitForFunctionAsync(@"() => {
                        const dialog = document.querySelector('div.global-dialog');
                        if (!dialog) return false;
                        const divs = dialog.querySelectorAll('div.text-white');
                        for (const div of divs) {
                            let text = '';
                            for (const node of div.childNodes) {
                                if (node.nodeType === Node.TEXT_NODE) text += node.textContent;
                            }
                            if (text.includes('購入日')) return true;
                        }
                        return false;
                    }", null, new PageWaitForFunctionOptions() { Timeout = 5000 });
                }
                catch { }
                // タイムアウトしても処理は継続（購入日なしコンテンツの可能性）

                // JavaScriptで「購入日：」を含むdivの直接のテキストノードのみを取得する
                // InnerTextAsync()は子要素のテキストも含めてしまうため使用しない
                var dateRegex = new Regex(@"\d{4}年\d{1,2}月\d{1,2}日\s+\d{2}:\d{2}");
                string rawDateText = await Page.EvaluateAsync<string>(@"() => {
                    // div.global-dialog内の購入日divを探す
                    const dialog = document.querySelector('div.global-dialog');
                    if (!dialog) return '';
                    // text-white text-xs mt-2 text-center クラスのdivを全て確認
                    const divs = dialog.querySelectorAll('div.text-white');
                    for (const div of divs) {
                        // 直接のテキストノードのみ結合する
                        let text = '';
                        for (const node of div.childNodes) {
                            if (node.nodeType === Node.TEXT_NODE) {
                                text += node.textContent;
                            }
                        }
                        text = text.trim();
                        if (text.includes('購入日')) {
                            return text;
                        }
                    }
                    return '';
                }");

                if (!string.IsNullOrEmpty(rawDateText))
                {
                    string cleaned = rawDateText.Replace("購入日：", "").Replace("購入日:", "").Replace("購入日 :", "").Trim();
                    var m = dateRegex.Match(cleaned);
                    if (m.Success) purchaseDate = m.Value;
                }

                if (string.IsNullOrEmpty(purchaseDate))
                {
                    // フォールバック: セレクターで要素を取得してテキストから正規表現で抽出
                    await Page.WaitForTimeoutAsync(1000);
                    rawDateText = await Page.EvaluateAsync<string>(@"() => {
                        const dialog = document.querySelector('div.global-dialog');
                        if (!dialog) return '';
                        const divs = dialog.querySelectorAll('div.text-white');
                        for (const div of divs) {
                            let text = '';
                            for (const node of div.childNodes) {
                                if (node.nodeType === Node.TEXT_NODE) {
                                    text += node.textContent;
                                }
                            }
                            text = text.trim();
                            if (text.includes('購入日')) {
                                return text;
                            }
                        }
                        return '';
                    }");
                    if (!string.IsNullOrEmpty(rawDateText))
                    {
                        string cleaned = rawDateText.Replace("購入日：", "").Replace("購入日:", "").Replace("購入日 :", "").Trim();
                        var m = dateRegex.Match(cleaned);
                        if (m.Success) purchaseDate = m.Value;
                    }
                }

                if (string.IsNullOrEmpty(purchaseDate))
                {
                    Console.WriteLine($"\n[WARNING] 購入日を取得できませんでした (タイトル: '{rawTitle}')。ダウンロードのみ試みます。");

                    // デバッグ時はポップアップの全HTMLを出力して原因調査
                    if (IsDebugMode)
                    {
                        try
                        {
                            string dialogHtml = await Page.EvaluateAsync<string>(@"() => {
                                const dialog = document.querySelector('div.global-dialog');
                                return dialog ? dialog.innerHTML : '(dialog not found)';
                            }");
                            Console.WriteLine($"[DEBUG] Popup innerHTML:\n{dialogHtml}");
                        }
                        catch (Exception dbex)
                        {
                            Console.WriteLine($"[DEBUG] DOM dump failed: {dbex.Message}");
                        }
                    }
                }

                string? existingCid = null;
                if (entry != null && entry.ContainsKey("cid"))
                {
                    existingCid = entry["cid"]?.ToString();
                }
                else
                {
                    var detailLink = Page.Locator("div.global-dialog:visible").Locator(AppConstants.POPUP_DETAIL_LINK_SELECTOR).First;
                    string? href = await detailLink.GetAttributeAsync("href");
                    if (!string.IsNullOrEmpty(href))
                    {
                        var match = CidFromHrefPattern.Match(href);
                        if (match.Success)
                        {
                            existingCid = match.Groups[1].Value;
                        }
                    }
                }

                // --- ダウンロード処理 (購入日の成否に関わらず実行) ---
                if (!string.IsNullOrEmpty(_downloadDirectory))
                {
                    try
                    {
                        var downloadLinks = await Page.Locator("div.global-dialog:visible a[href*='transfer_type=download'][href*='ftype=']").AllAsync();
                        if (downloadLinks.Count > 0)
                        {
                            string cleanTitle = FileSanitizer.SanitizeFilename(rawTitle);
                            string targetFolder = Path.Combine(_downloadDirectory, cleanTitle);
                            Directory.CreateDirectory(targetFolder);

                            Console.WriteLine($"\n[DOWNLOAD] '{rawTitle}' のダウンロードを開始します (全 {downloadLinks.Count} パート)。");

                            for (int i = 0; i < downloadLinks.Count; i++)
                            {
                                var linkLoc = downloadLinks[i];
                                string? href = await linkLoc.GetAttributeAsync("href");
                                if (string.IsNullOrEmpty(href)) continue;

                                // ftype の動的抽出
                                string extension = ".dcv"; // デフォルト
                                var ftypeMatch = Regex.Match(href, @"ftype=([^/]+)");
                                if (ftypeMatch.Success)
                                {
                                    extension = "." + ftypeMatch.Groups[1].Value;
                                }

                                // 命名規則の解決
                                string fileName;
                                if (downloadLinks.Count == 1)
                                {
                                    fileName = $"{cleanTitle}{extension}";
                                }
                                else
                                {
                                    int partNum = i + 1;
                                    bool endsWithDigit = cleanTitle.Length > 0 && char.IsDigit(cleanTitle[cleanTitle.Length - 1]);
                                    if (endsWithDigit)
                                    {
                                        fileName = $"{cleanTitle}-{partNum}{extension}";
                                    }
                                    else
                                    {
                                        fileName = $"{cleanTitle}{partNum}{extension}";
                                    }
                                }

                                string filePath = Path.Combine(targetFolder, fileName);
                                if (File.Exists(filePath))
                                {
                                    Console.WriteLine($"  [SKIP] すでに存在します: {fileName}");
                                    continue;
                                }

                                Console.WriteLine($"  [{i + 1}/{downloadLinks.Count}] ダウンロード中: {fileName}...");
                                try
                                {
                                    var download = await Page.RunAndWaitForDownloadAsync(async () =>
                                    {
                                        await linkLoc.ClickAsync(new() { Timeout = 10000 });
                                    });
                                    await download.SaveAsAsync(filePath);
                                    Console.WriteLine($"  [COMPLETED] ダウンロード完了: {fileName}");
                                }
                                catch (Exception dex)
                                {
                                    Console.WriteLine($"  [ERROR] ダウンロード失敗 ({fileName}): {dex.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WARNING] ダウンロード処理中にエラーが発生しました: {ex.Message}");
                    }
                }

                // 購入日が取得できなかった場合は data.json 更新をスキップ
                if (string.IsNullOrEmpty(purchaseDate))
                {
                    return false;
                }


                var updateData = new System.Text.Json.Nodes.JsonObject
                {
                    ["title"] = rawTitle,
                    ["purchase_date"] = purchaseDate,
                    ["cid"] = existingCid
                };

                return dataManager.UpdateOrAddEntry(updateData);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[ERROR] '{rawTitle}' の処理中にエラー: {ex.Message}");
                return false;
            }
            finally
            {
                // ポップアップを閉じる
                try
                {
                    string[] closeSelectors = new[]
                    {
                        AppConstants.POPUP_CLOSE_BUTTON_SELECTOR,
                        "#js-detail-wp",
                        ".close-btn",
                        ".modal-close",
                        "button:has-text(\"閉じる\")",
                        "button:has-text(\"×\")"
                    };

                    foreach (var selector in closeSelectors)
                    {
                        var closeEl = Page.Locator(selector);
                        if (await closeEl.CountAsync() > 0 && await closeEl.IsVisibleAsync())
                        {
                            await closeEl.ClickAsync(new() { Timeout = 2000 });
                            await Page.WaitForTimeoutAsync(500);
                            break;
                        }
                    }

                    await Page.Keyboard.PressAsync("Escape");
                    await Page.WaitForTimeoutAsync(500);
                }
                catch { }
            }
        }


        public async Task UpdateCidsAndDownloadImagesAsync(string saveDir, DataManager dataManager)
        {
            if (Page == null) throw new InvalidOperationException("Page is not initialized.");

            Console.WriteLine("CIDの更新と画像のダウンロードを開始します...");
            // 新レイアウトはページネーション式のため、全ページを巡回して各ページの画像/CIDを処理する。
            await ForEachPageAsync(async (pageNum, total) =>
            {
                Console.WriteLine($"  [ページ {pageNum}/{total}] 画像/CIDを処理します...");
                await ProcessCurrentPageImagesAsync(saveDir, dataManager);
            });
        }

        private async Task ProcessCurrentPageImagesAsync(string saveDir, DataManager dataManager)
        {
            if (Page == null) throw new InvalidOperationException("Page is not initialized.");

            string htmlContent = await Page.ContentAsync();
            var parser = new HtmlParser();
            var document = await parser.ParseDocumentAsync(htmlContent);

            // 1. 新レイアウトの画像構造を最優先でチェック (これが現在最も確実で網羅的)
            var gridImages = document.QuerySelectorAll($"{AppConstants.MY_SEARCH_LIST_ITEM_SELECTOR} img").ToList();
            if (gridImages.Count > 0)
            {
                Console.WriteLine($"新グリッド画像構造を使用: {gridImages.Count}個の画像");
                await ProcessImagesFromSrcAsync(gridImages, saveDir, dataManager);
                return;
            }

            // 2. フォールバック: 従来の mySearchList_item_pict 構造
            var imageTags = document.QuerySelectorAll("span.mySearchList_item_pict img[src][alt]");
            if (imageTags.Length > 0)
            {
                Console.WriteLine($"従来のmySearchList_item_pict構造を使用: {imageTags.Length}個の画像");
                await ProcessImagesWithHrefExtractionAsync(imageTags, saveDir, dataManager, document);
                return;
            }

            // 3. フォールバック: CIDリンク構造
            var cidLinks = document.QuerySelectorAll("a").Where(a => {
                string? href = a.GetAttribute("href");
                return href != null && CidFromHrefPattern.IsMatch(href);
            }).ToList();
            if (cidLinks.Count > 0)
            {
                Console.WriteLine($"CIDリンク構造を使用: {cidLinks.Count}個のリンク");
                await ProcessCidLinksAsync(cidLinks, saveDir, dataManager);
                return;
            }

            Console.WriteLine("CIDを含むリンクまたは画像が見つかりませんでした。");
        }

        private async Task ProcessImagesFromSrcAsync(List<AngleSharp.Dom.IElement> imageTags, string saveDir, DataManager dataManager)
        {
            bool dataUpdated = false;
            Directory.CreateDirectory(saveDir);

            int total = imageTags.Count;
            for (int i = 0; i < total; i++)
            {
                var img = imageTags[i];
                string? imageUrl = img.GetAttribute("src");
                string? title = img.GetAttribute("alt")?.Trim();

                if (string.IsNullOrEmpty(imageUrl) || string.IsNullOrEmpty(title)) continue;

                var match = CidFromSrcPattern.Match(imageUrl);
                if (!match.Success) continue;

                string cid = match.Groups[1].Value;
                string imageFilename = Path.GetFileName(imageUrl);
                int qIdx = imageFilename.IndexOf('?');
                if (qIdx >= 0)
                {
                    imageFilename = imageFilename.Substring(0, qIdx);
                }

                var entryData = new JsonObject
                {
                    ["title"] = title,
                    ["cid"] = cid,
                    ["image_filename"] = imageFilename
                };

                if (dataManager.UpdateOrAddEntry(entryData))
                {
                    dataUpdated = true;
                }

                string cleanImageUrl = imageUrl.Split('?')[0];
                await DownloadImageAsync(cleanImageUrl, Path.Combine(saveDir, imageFilename));

                if ((i + 1) % 10 == 0 || i + 1 == total)
                {
                    Console.Write($"\rCID・画像処理中(新レイアウト): {i + 1}/{total}");
                }
            }
            Console.WriteLine();

            if (dataUpdated)
            {
                dataManager.SaveData();
            }
        }

        private async Task ProcessImagesWithHrefExtractionAsync(IEnumerable<IElement> imageTags, string saveDir, DataManager dataManager, IDocument document)
        {
            bool dataUpdated = false;
            Directory.CreateDirectory(saveDir);

            var imageList = imageTags.ToList();
            int total = imageList.Count;

            for (int i = 0; i < total; i++)
            {
                var img = imageList[i];
                string? imageUrl = img.GetAttribute("src");
                string? title = img.GetAttribute("alt")?.Trim();

                if (string.IsNullOrEmpty(imageUrl) || string.IsNullOrEmpty(title)) continue;

                string imageFilename = Path.GetFileName(imageUrl);
                string? cid = null;

                // 親要素から a タグを辿って CID を探す
                var current = img;
                bool hrefFound = false;

                for (int level = 0; level < 5; level++)
                {
                    if (current == null) break;

                    if (current.TagName.Equals("LI", StringComparison.OrdinalIgnoreCase))
                    {
                        var aTags = current.QuerySelectorAll("a").Where(a => a.GetAttribute("href")?.Contains("cid=") == true).ToList();
                        if (aTags.Count > 0)
                        {
                            string? href = aTags[0].GetAttribute("href");
                            if (!string.IsNullOrEmpty(href))
                            {
                                var match = CidFromHrefPattern.Match(href);
                                if (match.Success)
                                {
                                    cid = match.Groups[1].Value;
                                    hrefFound = true;
                                    break;
                                }
                            }
                        }
                    }
                    current = current.ParentElement;
                }

                if (!hrefFound)
                {
                    var match = CidFromSrcPattern.Match(imageUrl);
                    if (match.Success)
                    {
                        cid = match.Groups[1].Value;
                    }
                }

                var entryData = new JsonObject
                {
                    ["title"] = title,
                    ["image_filename"] = imageFilename
                };
                if (cid != null)
                {
                    entryData["cid"] = cid;
                }

                if (dataManager.UpdateOrAddEntry(entryData))
                {
                    dataUpdated = true;
                }

                // 画像ダウンロード
                await DownloadImageAsync(imageUrl, Path.Combine(saveDir, imageFilename));

                if ((i + 1) % 10 == 0 || i + 1 == total)
                {
                    Console.Write($"\rCID・画像処理中: {i + 1}/{total}");
                }
            }
            Console.WriteLine();

            if (dataUpdated)
            {
                dataManager.SaveData();
            }
        }

        private async Task ProcessCidLinksAsync(List<IElement> cidLinks, string saveDir, DataManager dataManager)
        {
            bool dataUpdated = false;
            Directory.CreateDirectory(saveDir);

            int total = cidLinks.Count;
            for (int i = 0; i < total; i++)
            {
                var link = cidLinks[i];
                string? href = link.GetAttribute("href");
                if (string.IsNullOrEmpty(href)) continue;

                var match = CidFromHrefPattern.Match(href);
                if (!match.Success) continue;

                string cid = match.Groups[1].Value;
                var images = link.QuerySelectorAll("img");

                foreach (var img in images)
                {
                    string? imageUrl = img.GetAttribute("src");
                    string? title = img.GetAttribute("alt")?.Trim();

                    if (string.IsNullOrEmpty(imageUrl) || string.IsNullOrEmpty(title)) continue;

                    string imageFilename = Path.GetFileName(imageUrl);

                    var entryData = new JsonObject
                    {
                        ["title"] = title,
                        ["cid"] = cid,
                        ["image_filename"] = imageFilename
                    };

                    if (dataManager.UpdateOrAddEntry(entryData))
                    {
                        dataUpdated = true;
                    }

                    await DownloadImageAsync(imageUrl, Path.Combine(saveDir, imageFilename));
                }

                if ((i + 1) % 10 == 0 || i + 1 == total)
                {
                    Console.Write($"\rCIDリンク処理中: {i + 1}/{total}");
                }
            }
            Console.WriteLine();

            if (dataUpdated)
            {
                dataManager.SaveData();
            }
        }

        private async Task DownloadImageAsync(string url, string savePath)
        {
            if (File.Exists(savePath)) return;

            try
            {
                byte[] bytes = await _httpClient.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(savePath, bytes);
                if (Verbose)
                {
                    Console.WriteLine($"  [DOWNLOAD] {Path.GetFileName(savePath)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[WARNING] 画像ダウンロード失敗 ({url}): {ex.Message}");
            }
        }

        public async Task<Dictionary<string, object>?> FetchDetailsForItemAsync(string cid)
        {
            string url = Urls["detail_page_base"].Replace("{cid}", cid);
            return await FetchDetailsForItemByUrlAsync(url);
        }

        public async Task<Dictionary<string, object>?> FetchDetailsForItemByUrlAsync(string detailUrl)
        {
            var details = new Dictionary<string, object>
            {
                ["detail_url"] = detailUrl
            };

            try
            {
                await using var page = await Context!.NewPageAsync();
                await page.GotoAsync(detailUrl, new() { Timeout = 20000, WaitUntil = WaitUntilState.DOMContentLoaded });

                // 年齢確認ページへリダイレクトされた場合は自動で通過する
                string currentUrl = page.Url;
                if (currentUrl.Contains("/age_check/") || currentUrl.Contains("age_check"))
                {
                    Console.WriteLine($"  [INFO] 年齢確認ページを検出しました。自動で通過します...");
                    try
                    {
                        // "I Agree" / 「同意する」ボタンのhrefを取得してそのURLへ遷移
                        var agreeLink = page.Locator("a[href*='declared=yes']").First;
                        string? agreeHref = await agreeLink.GetAttributeAsync("href");
                        if (!string.IsNullOrEmpty(agreeHref))
                        {
                            await page.GotoAsync(agreeHref, new() { Timeout = 20000, WaitUntil = WaitUntilState.DOMContentLoaded });
                            Console.WriteLine($"  [INFO] 年齢確認通過後のURL: {page.Url}");
                        }
                    }
                    catch (Exception ageEx)
                    {
                        Console.WriteLine($"  [WARNING] 年齢確認の自動通過に失敗しました: {ageEx.Message}");
                    }
                }

                await CloseAdPopupIfPresentAsync(page);

                try
                {
                    await page.WaitForSelectorAsync("table.text-xs, td.nw, .mg-b20", new() { Timeout = 10000 });
                }
                catch
                {
                    // ignore
                }

                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5000 });
                }
                catch { }

                // 「すべて表示する」をクリックして出演者リストを展開
                try
                {
                    var performersRow = page.Locator("tr:has(th:has-text('出演者')), tr:has(td.nw:has-text('出演者'))").First;
                    if (await performersRow.IsVisibleAsync())
                    {
                        var showAllLink = performersRow.Locator("button:has-text('すべて表示する'), a:has-text('すべて表示する')").First;
                        if (await showAllLink.IsVisibleAsync())
                        {
                            int initialCount = await performersRow.Locator("a").CountAsync();
                            await showAllLink.ClickAsync();

                            for (int attempt = 0; attempt < 20; attempt++)
                            {
                                await page.WaitForTimeoutAsync(500);
                                int currentCount = await performersRow.Locator("a").CountAsync();
                                if (attempt > 0 && currentCount == initialCount && currentCount > 10)
                                {
                                    break;
                                }
                                initialCount = currentCount;
                            }
                        }
                    }
                }
                catch { }

                string pageContent = await page.ContentAsync();
                var parser = new HtmlParser();
                var soup = await parser.ParseDocumentAsync(pageContent);

                string pageTitle = soup.Title ?? "";
                bool hasTable = soup.QuerySelector("table") != null;
                bool is404 = pageTitle.Contains("404") || pageContent.Contains("指定されたページが見つかりません");
                bool isError = is404 || pageTitle.Contains("エラーが発生しました") || pageTitle == "FANZA動画" || !hasTable;

                if (isError)
                {
                    Console.WriteLine($"[WARNING] 詳細ページのエラー検出 (URL: {detailUrl}) [Title: '{pageTitle}', HasTable: {hasTable}]");
                    if (is404)
                    {
                        details["no_detail_page"] = "True";
                        Console.WriteLine($"  -> 404エラー（販売終了）を検出したため、次回以降この商品の詳細情報の取得をスキップします。");
                    }
                    else
                    {
                        string safeCid = detailUrl.Contains("id=") ? detailUrl.Split("id=")[1] : "unknown";
                        await SaveDebugArtifactsAsync(page, $"detail_error_{safeCid}");
                    }
                    details["detail_page_error"] = $"ページエラー: {pageTitle} (HasTable: {hasTable})";
                    return details;
                }

                Func<string, bool, object?> getTableData = (header, isList) =>
                {
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
                                string thText = th.TextContent.Trim().Replace("：", "").Replace(":", "");
                                if (thText.Equals(header, StringComparison.OrdinalIgnoreCase) || thText.Contains(header))
                                {
                                    if (isList)
                                    {
                                        var flexDiv = td.QuerySelector("div.flex");
                                        var elements = flexDiv != null ? flexDiv.QuerySelectorAll("a") : td.QuerySelectorAll("a");
                                        return elements.Select(a => a.TextContent.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
                                    }
                                    else
                                    {
                                        var link = td.QuerySelector("a");
                                        return link != null ? link.TextContent.Trim() : td.TextContent.Trim();
                                    }
                                }
                            }
                        }
                    }

                    // 旧形式テーブルのフォールバック
                    var headerTds = soup.QuerySelectorAll("td.nw");
                    foreach (var td in headerTds)
                    {
                        if (td.TextContent.Contains(header))
                        {
                            var nextTd = td.NextElementSibling;
                            if (nextTd != null)
                            {
                                if (isList)
                                {
                                    return nextTd.QuerySelectorAll("a").Select(a => a.TextContent.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
                                }
                                else
                                {
                                    return nextTd.TextContent.Trim();
                                }
                            }
                        }
                    }

                    return null;
                };

                details["date"] = getTableData("商品発売日", false) ?? getTableData("配信開始日", false) ?? "";
                details["duration"] = getTableData("収録時間", false) ?? "";
                details["performers"] = getTableData("出演者", true) ?? new List<string>();
                details["director"] = getTableData("監督", false) ?? "";
                details["series"] = getTableData("シリーズ", false) ?? "";
                details["maker"] = getTableData("メーカー", false) ?? "";
                details["label"] = getTableData("レーベル", false) ?? "";

                var ogTitleTag = soup.QuerySelector("meta[property='og:title']");
                if (ogTitleTag != null && !string.IsNullOrEmpty(ogTitleTag.GetAttribute("content")))
                {
                    details["title"] = ogTitleTag.GetAttribute("content")!.Trim();
                }

                // JSON-LD からの description / genres 取得
                string? jsonLdDesc = null;
                List<string>? jsonLdGenres = null;

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
                            }
                        }
                    }
                    catch { }
                }

                // genres の決定
                if (jsonLdGenres != null && jsonLdGenres.Count > 0)
                {
                    details["genres"] = CleanGenres(jsonLdGenres);
                }
                else
                {
                    var rawGenres = getTableData("ジャンル", true) as List<string>;
                    details["genres"] = CleanGenres(rawGenres);
                }

                // description の決定
                if (!string.IsNullOrEmpty(jsonLdDesc))
                {
                    details["description"] = jsonLdDesc;
                }
                else
                {
                    var metaDesc = soup.QuerySelector("meta[name='description']") ?? soup.QuerySelector("meta[name='Description']");
                    string? metaContent = metaDesc?.GetAttribute("content");
                    if (!string.IsNullOrEmpty(metaContent))
                    {
                        details["description"] = metaContent.Trim();
                    }
                    else
                    {
                        var descDiv = soup.QuerySelector("div.mg-b20.lh4");
                        if (descDiv != null)
                        {
                            string rawDesc = descDiv.TextContent.Trim();
                            var descParts = Regex.Split(rawDesc, @"-{10,}");
                            details["description"] = descParts[0].Trim();
                        }
                        else
                        {
                            details["description"] = "";
                        }
                    }
                }

                // 関連タグ
                details["related_tags"] = ExtractRelatedTags(soup) ?? new List<string>();

                // サンプル画像の取得
                var sampleImagePairs = new List<(string jpUrl, string originalUrl)>();
                try
                {
                    var sampleImgTags = soup.QuerySelectorAll("img").Where(img => {
                        string? alt = img.GetAttribute("alt");
                        return alt != null && Regex.IsMatch(alt, @"サンプル画像\d+");
                    });

                    foreach (var img in sampleImgTags)
                    {
                        string? src = img.GetAttribute("src");
                        if (!string.IsNullOrEmpty(src))
                        {
                            string cleanSrc = src.Split('?')[0];
                            if (!cleanSrc.Contains("jp-"))
                            {
                                string jpSrc = Regex.Replace(cleanSrc, @"-(\d+)\.jpg$", "jp-$1.jpg");
                                sampleImagePairs.Add((jpSrc, cleanSrc));
                            }
                            else
                            {
                                sampleImagePairs.Add((cleanSrc, cleanSrc));
                            }
                        }
                    }

                    var sortedPairs = sampleImagePairs.OrderBy(p => p.jpUrl).ToList();
                    details["sample_images"] = sortedPairs.Select(p => p.jpUrl).ToList();
                    details["sample_images_mini"] = sortedPairs.Select(p => p.originalUrl).ToList();
                    if (sortedPairs.Count > 0)
                    {
                        Console.WriteLine($"サンプル画像を {sortedPairs.Count} 枚検出しました。");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[WARNING] サンプル画像の抽出中にエラーが発生しました: {e.Message}");
                    details["sample_images"] = new List<string>();
                    details["sample_images_mini"] = new List<string>();
                }

                return details;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] 詳細取得エラー (URL: {detailUrl}): {ex.Message}");
                details["detail_page_error"] = ex.Message;
                return details;
            }
        }

        private List<string> CleanGenres(List<string>? genres)
        {
            if (genres == null) return new List<string>();
            var excludeKeywords = new[] { "％OFF", "限定セール", "日替わりセール", "AV OPEN" };
            return genres.Where(g => !excludeKeywords.Any(kw => g.Contains(kw))).ToList();
        }

        private List<string>? ExtractRelatedTags(IDocument soup)
        {
            var tagLinks = soup.QuerySelectorAll("a").Where(a => a.GetAttribute("href")?.Contains("?tag=") == true).ToList();
            if (tagLinks.Count == 0) return null;
            
            var tags = tagLinks.Select(a => a.TextContent.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
            return tags.Count > 0 ? tags : null;
        }

        public class RelatedTagsResult
        {
            public List<string>? RelatedTags { get; set; }
            public bool Is404 { get; set; }
        }

        public async Task<RelatedTagsResult> FetchRelatedTagsForItemAsync(string detailUrl)
        {
            var result = new RelatedTagsResult { RelatedTags = null, Is404 = false };
            if (Page == null) return result;

            try
            {
                await using var page = await Context!.NewPageAsync();
                await page.GotoAsync(detailUrl, new() { Timeout = 20000, WaitUntil = WaitUntilState.DOMContentLoaded });

                await CloseAdPopupIfPresentAsync(page);

                bool detailContentFound = false;
                try
                {
                    await page.WaitForSelectorAsync("table.text-xs, td.nw, .mg-b20", new() { Timeout = 3000 });
                    detailContentFound = true;
                }
                catch { }

                if (!detailContentFound)
                {
                    try
                    {
                        string pageContentForCheck = await page.ContentAsync();
                        if (pageContentForCheck.Contains("ページが見つかりません"))
                        {
                            result.Is404 = true;
                            return result;
                        }
                        var error404Elements = await page.Locator("text=/^404$/").CountAsync();
                        if (error404Elements > 0)
                        {
                            result.Is404 = true;
                            return result;
                        }
                    }
                    catch { }
                }

                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 3000 });
                }
                catch { }

                string pageContent = await page.ContentAsync();
                var parser = new HtmlParser();
                var soup = await parser.ParseDocumentAsync(pageContent);
                result.RelatedTags = ExtractRelatedTags(soup);
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] 関連タグ取得エラー (URL: {detailUrl}): {ex.Message}");
                return result;
            }
        }

        public async Task UpdateAllDetailsAsync(DataManager dataManager, string imageDir)
        {
            Console.WriteLine("不足している詳細情報の収集を開始します...");
            
            var itemsToFetch = new List<JsonObject>();
            foreach (var item in dataManager.Data)
            {
                string? cid = item["cid"]?.ToString();
                if (string.IsNullOrEmpty(cid)) continue;
                if (item.ContainsKey("no_detail_page") && item["no_detail_page"] != null) continue;
                if (item.ContainsKey("discontinued") && item["discontinued"]?.GetValue<bool>() == true) continue;
                
                bool missingDetail = false;
                foreach (var key in AppConstants.DETAIL_KEYS)
                {
                    if (!item.ContainsKey(key) || item[key] == null)
                    {
                        missingDetail = true;
                        break;
                    }
                }
                
                if (!missingDetail && item.ContainsKey("genres"))
                {
                    var genresArr = item["genres"]?.AsArray();
                    if (genresArr == null || genresArr.Count == 0)
                    {
                        missingDetail = true;
                    }
                }
                
                if (missingDetail)
                {
                    itemsToFetch.Add(item);
                }
            }

            if (itemsToFetch.Count == 0)
            {
                Console.WriteLine("詳細情報を更新する対象はありませんでした。");
                return;
            }

            Console.WriteLine($"詳細情報収集対象: {itemsToFetch.Count} 件");
            bool updated = false;
            int count = 0;

            foreach (var item in itemsToFetch)
            {
                count++;
                string cid = item["cid"]!.ToString();
                string title = item["title"]?.ToString() ?? $"CID {cid}";
                
                Console.WriteLine($"[{count}/{itemsToFetch.Count}] 詳細情報を取得中: {title} (CID: {cid})");
                
                var details = await FetchDetailsForItemAsync(cid);
                await Task.Delay(1000); // サーバー負荷軽減

                if (details != null)
                {
                    var updatePayload = new JsonObject
                    {
                        ["title"] = title,
                        ["cid"] = cid
                    };
                    
                    foreach (var pair in details)
                    {
                        if (pair.Value is List<string> listStr)
                        {
                            var arr = new JsonArray();
                            foreach (var s in listStr) arr.Add(s);
                            updatePayload[pair.Key] = arr;
                        }
                        else
                        {
                            updatePayload[pair.Key] = pair.Value?.ToString();
                        }
                    }

                    if (dataManager.UpdateOrAddEntry(updatePayload))
                    {
                        updated = true;
                        if (details.ContainsKey("detail_page_error"))
                        {
                            Console.WriteLine($"  -> 詳細ページエラー情報を保存しました: {details["detail_page_error"]}");
                        }
                        else
                        {
                            Console.WriteLine("  -> 詳細情報を更新しました。");
                        }
                    }

                    // サンプル画像のダウンロード
                    var sampleImages = details.ContainsKey("sample_images") ? details["sample_images"] as List<string> : null;
                    var sampleImagesMini = details.ContainsKey("sample_images_mini") ? details["sample_images_mini"] as List<string> : null;

                    if (!string.IsNullOrEmpty(imageDir) && ((sampleImages != null && sampleImages.Count > 0) || (sampleImagesMini != null && sampleImagesMini.Count > 0)))
                    {
                        string sampleDir = Path.Combine(imageDir, cid);
                        if (Directory.Exists(sampleDir) && Directory.GetFiles(sampleDir).Length > 0)
                        {
                            Console.WriteLine("  -> サンプル画像: 既存のためスキップ");
                        }
                        else
                        {
                            try
                            {
                                Directory.CreateDirectory(sampleDir);
                                int downloadCount = 0;
                                int sampleCount = sampleImages?.Count ?? 0;

                                for (int idx = 0; idx < sampleCount; idx++)
                                {
                                    string imgUrl = sampleImages![idx];
                                    try
                                    {
                                        string filename = Path.GetFileName(imgUrl);
                                        string savePath = Path.Combine(sampleDir, filename);
                                        if (!File.Exists(savePath))
                                        {
                                            await DownloadImageAsync(imgUrl, savePath);
                                            downloadCount++;
                                        }
                                    }
                                    catch (HttpRequestException)
                                    {
                                        if (sampleImagesMini != null && idx < sampleImagesMini.Count)
                                        {
                                            string miniUrl = sampleImagesMini[idx];
                                            try
                                            {
                                                string miniFilename = Path.GetFileName(miniUrl);
                                                string miniSavePath = Path.Combine(sampleDir, miniFilename);
                                                if (!File.Exists(miniSavePath))
                                                {
                                                    await DownloadImageAsync(miniUrl, miniSavePath);
                                                    downloadCount++;
                                                }
                                            }
                                            catch { }
                                        }
                                    }
                                    catch { }
                                }

                                if (downloadCount > 0)
                                {
                                    Console.WriteLine($"  -> サンプル画像: {downloadCount}枚ダウンロード");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"  -> サンプル画像の保存フォルダ作成またはダウンロード中にエラー（スキップします）: {ex.Message}");
                            }
                        }
                    }
                }
                else
                {
                    var noDetailPayload = new JsonObject
                    {
                        ["title"] = title,
                        [AppConstants.NO_DETAIL_PAGE_FLAG] = "詳細情報ページへのリンクを持たないコンテンツ"
                    };
                    if (dataManager.UpdateOrAddEntry(noDetailPayload))
                    {
                        updated = true;
                        Console.WriteLine("  -> 詳細ページなしフラグを設定しました。");
                    }
                }
            }

            if (updated)
            {
                dataManager.SaveData();
            }
        }

        public async Task CollectRelatedTagsBatchAsync(DataManager dataManager)
        {
            var itemsToFetch = new List<JsonObject>();
            foreach (var item in dataManager.Data)
            {
                if (item.ContainsKey("detail_url") && item["detail_url"] != null &&
                    !item.ContainsKey("related_tags") &&
                    !item.ContainsKey("no_related_tags") &&
                    (!item.ContainsKey("discontinued") || item["discontinued"]?.GetValue<bool>() == false) &&
                    !item.ContainsKey("no_detail_page"))
                {
                    itemsToFetch.Add(item);
                }
            }

            if (itemsToFetch.Count == 0)
            {
                Console.WriteLine("関連タグを収集する対象はありませんでした。");
                return;
            }

            Console.WriteLine($"関連タグ収集対象: {itemsToFetch.Count} 件");
            int updatedCount = 0;
            int removedUrlCount = 0;
            int noTagsCount = 0;
            int errorCount = 0;
            int saveInterval = 10;

            for (int i = 0; i < itemsToFetch.Count; i++)
            {
                var item = itemsToFetch[i];
                string cid = item["cid"]?.ToString() ?? "N/A";
                string title = item["title"]?.ToString() ?? "N/A";
                string detailUrl = item["detail_url"]!.ToString();

                try
                {
                    var result = await FetchRelatedTagsForItemAsync(detailUrl);
                    await Task.Delay(1000);

                    if (result.Is404)
                    {
                        item.Remove("detail_url");
                        removedUrlCount++;
                        updatedCount++;
                        Console.WriteLine($"  [404] {title} (CID: {cid}) - detail_url を削除しました");
                    }
                    else if (result.RelatedTags != null && result.RelatedTags.Count > 0)
                    {
                        var arr = new JsonArray();
                        foreach (var tag in result.RelatedTags) arr.Add(tag);
                        item["related_tags"] = arr;
                        updatedCount++;
                        Console.WriteLine($"  [TAGS] {title} (CID: {cid}) - {result.RelatedTags.Count}個のタグを取得");
                    }
                    else
                    {
                        item["no_related_tags"] = true;
                        noTagsCount++;
                        updatedCount++;
                        Console.WriteLine($"  [NO-TAGS] {title} (CID: {cid}) - 関連タグなし");
                    }
                }
                catch (Exception ex)
                {
                    errorCount++;
                    Console.WriteLine($"  [ERROR] {title} (CID: {cid}) - 想定外のエラー: {ex.Message}");
                }

                if (updatedCount > 0 && updatedCount % saveInterval == 0)
                {
                    dataManager.SaveData();
                    Console.WriteLine($"  [中間保存] {updatedCount}件の更新を保存しました");
                }
            }

            if (updatedCount > 0)
            {
                dataManager.SaveData();
            }

            int tagsCollected = updatedCount - removedUrlCount - noTagsCount;
            Console.WriteLine($"関連タグ収集完了: タグ取得={tagsCollected}件, タグなし={noTagsCount}件, 404削除={removedUrlCount}件, エラー={errorCount}件");
        }
    }
}
