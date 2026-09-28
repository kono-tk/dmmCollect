using System.Text.Json.Nodes;

namespace dmmCollect
{
    /// <summary>
    /// 作品の識別子を読み書きする唯一の窓口（dmmWin の ItemIdentity と同じ規則）。
    ///
    /// 識別子のキーは全モードで "cid"。books / dojin はかつて "product_id" を使っていたが、
    /// 2026-09 に data.json・閲覧履歴とも cid へ移行し、product_id は削除した。
    /// （サイトの URL にある product_id= パラメータは別物で、そちらは今も使う）
    /// </summary>
    public static class ItemIdentity
    {
        public const string Key = "cid";

        public static string Of(JsonNode? node) =>
            (node as JsonObject)?[Key]?.ToString() ?? "";

        /// <summary>識別子を書き込む。空なら何もしない（既存の値を空で上書きしないため）。</summary>
        public static void Set(JsonObject obj, string? id)
        {
            if (string.IsNullOrEmpty(id)) return;
            obj[Key] = id;
        }
    }
}
