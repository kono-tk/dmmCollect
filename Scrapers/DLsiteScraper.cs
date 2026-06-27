using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace dmmCollect.Scrapers
{
    public class DLsiteScraper : BaseScraper
    {
        private static readonly Regex DlsiteCidPattern = new(@"(RJ\d{6,})", RegexOptions.Compiled);
        private readonly HttpClient _httpClient = new();

        public DLsiteScraper(Dictionary<string, string> urls, bool headless, int slowMo, bool isDebugMode, bool verbose = false)
            : base(urls, headless, slowMo, isDebugMode, verbose)
        {
        }

        public async Task ScrapeDlsiteAsync(DataManager dataManager, string imageDir)
        {
            Console.WriteLine("--- DLsiteモード開始 ---");

            if (Page == null) throw new InvalidOperationException("Page is not initialized.");

            try
            {
                await LoginDlsiteAsync();

                Console.WriteLine("[DLSITE] React Fiber ツリーから全データを直接取得します...");
                
                // React Fiber ツリーからデータを抽出するJS
                var evaluateResult = await Page.EvaluateAsync<JsonElement>(@"() => {
                    const virtuosoContainer = document.querySelector('div[data-virtuoso-scroller=""true""]');
                    if (!virtuosoContainer) {
                        return { error: 'Virtuoso container not found' };
                    }
                    const fiberKey = Object.keys(virtuosoContainer).find(key => key.startsWith('__reactFiber'));
                    if (!fiberKey) {
                        return { error: 'React Fiber not found' };
                    }
                    let fiber = virtuosoContainer[fiberKey];
                    let attempts = 0;
                    const maxAttempts = 50;
                    while (fiber && attempts < maxAttempts) {
                        attempts++;
                        if (fiber.memoizedProps && fiber.memoizedProps.works) {
                            const works = fiber.memoizedProps.works;
                            if (Array.isArray(works) && works.length > 0) {
                                return { success: true, data: works, count: works.length };
                            }
                        }
                        if (fiber.memoizedState) {
                            if (fiber.memoizedState.works || fiber.memoizedState.items || fiber.memoizedState.library) {
                                const data = fiber.memoizedState.works || fiber.memoizedState.items || fiber.memoizedState.library;
                                if (Array.isArray(data) && data.length > 0) {
                                    return { success: true, data: data, count: data.length };
                                }
                            }
                        }
                        fiber = fiber.return;
                    }
                    return { error: 'Data not found in React Fiber tree', attempts };
                }");

                if (evaluateResult.TryGetProperty("success", out var successProp) && successProp.GetBoolean())
                {
                    var dataArray = evaluateResult.GetProperty("data");
                    Console.WriteLine($"[DLSITE] React Fiber から {dataArray.GetArrayLength()} 件の作品データを取得しました。");

                    bool updated = false;
                    Directory.CreateDirectory(imageDir);

                    for (int i = 0; i < dataArray.GetArrayLength(); i++)
                    {
                        var work = dataArray[i];
                        
                        string? title = work.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
                        string? productCode = work.TryGetProperty("productCode", out var codeProp) ? codeProp.GetString() : null;
                        
                        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(productCode)) continue;

                        string? makerName = null;
                        if (work.TryGetProperty("maker", out var makerProp) && makerProp.TryGetProperty("name", out var nameProp))
                        {
                            makerName = nameProp.GetString();
                        }

                        string? imageUrl = null;
                        if (work.TryGetProperty("thumbnail", out var thumbProp))
                        {
                            imageUrl = thumbProp.GetString();
                        }

                        // 日付のパース
                        string? purchaseDate = null;
                        if (work.TryGetProperty("orderedAt", out var orderedProp))
                        {
                            string? orderedStr = orderedProp.GetString();
                            if (DateTime.TryParse(orderedStr, out var dt))
                            {
                                purchaseDate = dt.ToString("yyyy-MM-dd");
                            }
                        }

                        // ジャンル / タグ
                        var tagsArray = new JsonArray();
                        if (work.TryGetProperty("genres", out var genresProp) && genresProp.ValueKind == JsonValueKind.Array)
                        {
                            for (int j = 0; j < genresProp.GetArrayLength(); j++)
                            {
                                var g = genresProp[j];
                                if (g.TryGetProperty("name", out var gName))
                                {
                                    tagsArray.Add(gName.GetString());
                                }
                            }
                        }

                        string? imageFilename = null;
                        if (!string.IsNullOrEmpty(imageUrl))
                        {
                            string filename = Uri.UnescapeDataString(Path.GetFileName(imageUrl));
                            string savePath = Path.Combine(imageDir, filename);

                            if (!File.Exists(savePath))
                            {
                                try
                                {
                                    byte[] bytes = await _httpClient.GetByteArrayAsync(imageUrl);
                                    await File.WriteAllBytesAsync(savePath, bytes);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[DLSITE] 画像ダウンロード失敗 ({imageUrl}): {ex.Message}");
                                }
                            }
                            imageFilename = filename;
                        }

                        var itemData = new JsonObject
                        {
                            ["title"] = title,
                            ["subtitle"] = title,
                            ["cid"] = productCode,
                            ["maker"] = makerName ?? "",
                            ["tags"] = tagsArray,
                            ["purchase_date"] = purchaseDate ?? ""
                        };
                        if (imageFilename != null)
                        {
                            itemData["image_filename"] = imageFilename;
                        }

                        if (dataManager.UpdateOrAddEntry(itemData, isBook: true))
                        {
                            updated = true;
                        }
                    }

                    if (updated)
                    {
                        dataManager.SaveData();
                        Console.WriteLine("[DLSITE] data.json を更新しました。");
                    }
                }
                else
                {
                    string errorMsg = evaluateResult.TryGetProperty("error", out var errProp) ? errProp.GetString()! : "Unknown error";
                    Console.WriteLine($"[DLSITE] React Fiber からのデータ取得に失敗しました: {errorMsg}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DLSITE] エラー: {ex.Message}");
                await SaveDebugArtifactsAsync(Page);
            }

            Console.WriteLine("--- DLsiteモード完了 ---");
        }

        private async Task LoginDlsiteAsync()
        {
            if (Page == null) return;

            Console.WriteLine("[DLSITE] DLsiteログイン処理を開始します...");

            string? loginId = Environment.GetEnvironmentVariable("DLSITE_LOGIN_ID");
            string? password = Environment.GetEnvironmentVariable("DLSITE_PASSWORD");

            if (string.IsNullOrEmpty(loginId) || string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException("DLSITE_LOGIN_ID and DLSITE_PASSWORD must be set in environment variables");
            }

            Console.WriteLine("[DLSITE] ライブラリページにアクセス中...");
            await Page.GotoAsync(Urls["login_url"], new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
            await Page.WaitForTimeoutAsync(2000);

            Console.WriteLine("[DLSITE] ログイン情報を入力中...");
            try
            {
                await Page.WaitForSelectorAsync("input#form_id[name=\"login_id\"]", new() { Timeout = 10000 });

                await Page.FillAsync("input#form_id[name=\"login_id\"]", loginId);
                await Page.FillAsync("input#form_password[name=\"password\"]", password);

                await Page.ClickAsync("button[type=\"submit\"]");
                Console.WriteLine("[DLSITE] ログインボタンをクリックしました");

                await Page.WaitForURLAsync("**/library**", new() { Timeout = 30000 });
                await Page.WaitForTimeoutAsync(3000);

                Console.WriteLine("[DLSITE] ログイン成功！");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DLSITE] ログイン処理中にエラーが発生しました: {ex.Message}");
                await SaveDebugArtifactsAsync(Page, "dlsite_login_error");
                throw;
            }
        }
    }
}
