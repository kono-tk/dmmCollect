using System.Text.Json.Nodes;

namespace dmmCollect
{
    /// <summary>
    /// 作品の識別子を読み書きする唯一の窓口（dmmWin の ItemIdentity と同じ規則）。
    ///
    /// 識別子のキーは "cid" に統一する（2026-09）。移行前は books / dojin だけが
    /// "product_id" を使っていたため、読むときは "cid" が無ければ "product_id" を見る。
    /// 移行期間中、books / dojin の書き込みは両方のキーに同じ値を入れる
    /// （product_id を読む Python ツールが残っているため）。
    /// </summary>
    public static class ItemIdentity
    {
        public const string Key = "cid";
        public const string LegacyKey = "product_id";

        public static string Of(JsonNode? node)
        {
            if (node is not JsonObject obj) return "";

            string? id = obj[Key]?.ToString();
            if (!string.IsNullOrEmpty(id)) return id;

            return obj[LegacyKey]?.ToString() ?? "";
        }

        /// <summary>識別子を両方のキーに書き込む（移行期間中の books / dojin 用）。空なら何もしない。</summary>
        public static void Set(JsonObject obj, string? id)
        {
            if (string.IsNullOrEmpty(id)) return;
            obj[Key] = id;
            obj[LegacyKey] = id;
        }
    }
}
