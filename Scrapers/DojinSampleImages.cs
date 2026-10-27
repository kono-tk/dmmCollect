using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;

namespace dmmCollect.Scrapers
{
    /// <summary>
    /// 同人作品（www.dmm.co.jp/dc/doujin）のサンプル画像を作品ページから取得する。
    ///
    /// 作品ページの li.previewList__item に、表紙（&lt;cid&gt;pr.jpg）と小さいサンプル画像
    /// （&lt;cid&gt;js-001.jpg、120×90）が並ぶ。同じ場所に大きい版（&lt;cid&gt;jp-001.jpg、2000×1500 程度）があるので、
    /// 大きい版を保存し、取れなければ小さい版にする。表紙（一覧で使う画像と同じ作品の表紙）と
    /// 動画作品の再生ボタンの画像（tmb_play.png）は除く。
    /// ログイン不要（年齢確認の Cookie だけ）なのでブラウザは使わない。販売終了の作品は 404 になる。
    /// </summary>
    public static class DojinSampleImages
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
            handler.CookieContainer.Add(new Cookie("age_check_done", "1", "/", ".dmm.co.jp"));
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            return http;
        }

        public static string ProductUrl(string cid) => $"https://www.dmm.co.jp/dc/doujin/-/detail/=/cid={cid}/";

        /// <summary>1枚のサンプル画像（大きい版と、取れなかったときの小さい版）。</summary>
        public sealed record Sample(string LargeUrl, string SmallUrl);

        /// <summary>作品ページの取得結果。NotFound は販売終了などでページが無い（サンプル画像なしとして扱う）。</summary>
        public sealed record PageResult(bool NotFound, List<Sample> Samples);

        /// <summary>作品ページを読み、サンプル画像の一覧を返す。通信の失敗やサーバーのエラーは例外。</summary>
        public static async Task<PageResult> FetchAsync(string cid, CancellationToken ct)
        {
            using var response = await Http.GetAsync(ProductUrl(cid), ct);
            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone)
            {
                return new PageResult(true, new List<Sample>());
            }
            response.EnsureSuccessStatusCode();
            return new PageResult(false, ParsePreviewList(await response.Content.ReadAsStringAsync(ct), cid));
        }

        /// <summary>li.previewList__item の画像から、小さいサンプル画像（&lt;cid&gt;js-NNN）を拾い、大きい版の URL と組にする。</summary>
        public static List<Sample> ParsePreviewList(string html, string cid)
        {
            var smallName = new Regex($@"^{Regex.Escape(cid)}js-(\d+)\.(\w+)$", RegexOptions.IgnoreCase);
            var doc = new HtmlParser().ParseDocument(html);
            var samples = new List<Sample>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var img in doc.QuerySelectorAll("li.previewList__item img"))
            {
                string src = (img.GetAttribute("src") ?? img.GetAttribute("data-src") ?? "").Split('?')[0];
                int slash = src.LastIndexOf('/');
                if (slash < 0) continue;

                var m = smallName.Match(src[(slash + 1)..]);
                if (!m.Success || !seen.Add(src)) continue;

                string large = $"{src[..(slash + 1)]}{cid}jp-{m.Groups[1].Value}.{m.Groups[2].Value}";
                samples.Add(new Sample(large, src));
            }
            return samples;
        }

        /// <summary>
        /// サンプル画像を dir に保存し、保存できた枚数（既にあったものを含む）を返す。
        /// 一時ファイルに書いてから名前を変えるので、途中で止めても壊れたファイルは残らない。
        /// </summary>
        public static async Task<int> DownloadAsync(IReadOnlyList<Sample> samples, string dir, CancellationToken ct)
        {
            Directory.CreateDirectory(dir);
            int saved = 0;
            foreach (var sample in samples)
            {
                ct.ThrowIfCancellationRequested();
                if (await TrySaveAsync(sample.LargeUrl, dir, ct) || await TrySaveAsync(sample.SmallUrl, dir, ct))
                {
                    saved++;
                }
            }
            return saved;
        }

        private static async Task<bool> TrySaveAsync(string url, string dir, CancellationToken ct)
        {
            string path = Path.Combine(dir, Path.GetFileName(new Uri(url).LocalPath));
            if (File.Exists(path)) return true;

            try
            {
                using var response = await Http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) return false;
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length == 0) return false;

                string temp = path + ".tmp";
                await File.WriteAllBytesAsync(temp, bytes, ct);
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (HttpRequestException)
            {
                return false;
            }
        }
    }
}
