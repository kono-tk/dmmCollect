using System;

namespace dmmCollect.Scrapers
{
    /// <summary>
    /// 動画の「フロア」。通常の作品（av）と素人作品（amateur）では URL の形が違う。
    ///   作品ページ: video.dmm.co.jp/av/content/?id=… / video.dmm.co.jp/amateur/content/?id=…
    ///   旧形式の詳細ページ: /digital/videoa/… / /digital/videoc/…
    ///   サムネイル: /digital/video/&lt;cid&gt;/… / /digital/amateur/&lt;cid&gt;/…
    /// 素人作品を通常作品の URL で開くと 404（販売終了扱い）になるため、作品ごとにフロアを判別して URL を作る。
    /// フロアは data.json の detail_url から判別する（素人作品は登録時に素人用の URL を入れる）。
    /// </summary>
    public static class VideoFloor
    {
        public const string Av = "av";
        public const string Amateur = "amateur";

        public static string FromUrl(string? url)
        {
            string u = url ?? "";
            return u.Contains("/amateur/", StringComparison.OrdinalIgnoreCase)
                || u.Contains("/videoc/", StringComparison.OrdinalIgnoreCase)
                ? Amateur
                : Av;
        }

        public static string ContentPageUrl(string cid, string floor) =>
            $"https://video.dmm.co.jp/{(floor == Amateur ? Amateur : Av)}/content/?id={Uri.EscapeDataString(cid)}";
    }
}
