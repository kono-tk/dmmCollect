using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace dmmCollect
{
    /// <summary>星評価と本文の食い違いの種類。</summary>
    public enum CommentMismatch
    {
        None,

        /// <summary>星は高い（4以上）のに、本文は否定的。</summary>
        HighStarNegativeText,

        /// <summary>星は低い（2以下）のに、本文は肯定的。</summary>
        LowStarPositiveText,
    }

    /// <summary>解析に渡すコメント。</summary>
    public readonly record struct CommentInput(int Rating, string Title, string Body);

    /// <summary>1作品分の解析結果。data.json の comment_aspects / comment_mismatch に書く。</summary>
    public class WorkAnalysis
    {
        /// <summary>観点ごとの評判（「演技が好評」「画質に不満」「ストーリーは賛否」）。</summary>
        public List<string> Aspects { get; } = new();

        /// <summary>星と本文が食い違うコメントがある場合のラベル。</summary>
        public List<string> Mismatches { get; } = new();

        public int HighStarNegativeCount { get; set; }
        public int LowStarPositiveCount { get; set; }

        // うち、LLM の判定で確認できたもの（ラベルの基準が未確認より緩い）
        public int HighStarNegativeConfirmed { get; set; }
        public int LowStarPositiveConfirmed { get; set; }
    }

    /// <summary>
    /// コメントの観点（演技・ストーリー・画質など）の評判と、星評価と本文の食い違いを求める。
    ///
    /// LLM ではなく辞書とルールで判定する。全コメント（約9万件）を数秒で処理でき、結果の理由を
    /// 説明でき、辞書を直せば結果が変わるため。精度の限界（皮肉・文脈・省略表現）は
    /// 作品単位で集計して薄める前提で許容している。
    ///
    /// dmmCollect（data.json へ書き込む側）と dmmWin（コメント表示で食い違いを示す側）に同じ内容を置く。
    /// 片方だけ直さないこと（dmmWin.Tests に両方の一致を確認するテストがある）。
    /// </summary>
    public static class CommentAnalyzer
    {
        // ---- 観点 ----
        // 名前はそのままフィルターのラベルになる（「{名前}が好評」など）。
        private static readonly (string Name, Regex Pattern)[] Aspects =
        {
            ("演技",       new Regex("演技|芝居|表情|リアクション|喘ぎ|あえぎ|反応", RegexOptions.Compiled)),
            ("ストーリー", new Regex("ストーリー|シナリオ|脚本|展開|設定|ドラマ|物語|台本", RegexOptions.Compiled)),
            // 「映像」「画面」「音が」「4K」は「映像を見る限り」「同じ画面でも」「バイブの音が」「4Kが見れるデバイス」のように
            // 画質・音声以外の話にも出るため、評価を表す形だけを拾う。
            ("画質",       new Regex("画質|解像度|映像(?:が|は|の質|美|品質|クオリティ)", RegexOptions.Compiled)),
            ("音声",       new Regex("音声|音質|BGM|音量|環境音|効果音", RegexOptions.Compiled)),
            ("男優",       new Regex("男優|相手役|竿役", RegexOptions.Compiled)),
            ("容姿",       new Regex("可愛|かわい|綺麗|きれい|美人|美乳|美尻|スタイル|ルックス|容姿|美少女|顔面", RegexOptions.Compiled)),
            // 「抜け出せない」「アカ抜けない」「抜け感」は実用性の話ではない
            ("実用性",     new Regex("(?<!アカ)(?<!あか)抜け(?!出|感|殻|落)|ヌケ|実用|抜きどころ|抜き所", RegexOptions.Compiled)),
            ("企画",       new Regex("企画|シチュ|コンセプト|アイデア", RegexOptions.Compiled)),
            // 「長い」「短い」「長時間」は導入や視聴の疲れの話にも使われる。「声のボリューム」は音量の話。
            ("ボリューム", new Regex("収録時間|(?<![声音])ボリューム|コスパ|お得|長尺", RegexOptions.Compiled)),
        };

        /// <summary>
        /// 「好評」だけを出し、「不満」「賛否」は出さない観点。抜き取り評価（LLM との一致率）で否定側の精度が低かったため。
        /// 容姿は「かわいくないパターンかと思いきや」のような期待と実際のずれ、ボリュームは「長い」の良し悪しが文脈次第で、
        /// 辞書とルールでは否定を取り違えやすい（否定側の一致率は容姿 25%、ボリューム 33%）。
        /// </summary>
        private static readonly HashSet<string> PositiveOnlyAspects = new() { "容姿", "ボリューム" };

        /// <summary>観点の評判を付けるのに必要な、肯定・否定のどちらかが付いたコメント数。</summary>
        public const int MinAspectEvidence = 3;

        /// <summary>これ以上が肯定なら「好評」。</summary>
        public const double GoodShare = 0.75;

        /// <summary>これ以上が否定なら「不満」（否定が明らかに優勢）。</summary>
        public const double BadStrongShare = 0.6;

        /// <summary>肯定・否定が拮抗しない場合でも、これ以上が否定なら「不満」。肯定に偏りやすい（平均4.2）ため、低く置く。</summary>
        public const double BadShare = 0.4;

        /// <summary>LLM で確認していない候補だけなら、この件数以上ある作品にラベルを付ける（誤検出が多いため）。</summary>
        public const int MinMismatchCount = 2;

        /// <summary>LLM で確認できたコメントなら、この件数以上ある作品にラベルを付ける。</summary>
        public const int MinConfirmedMismatchCount = 1;

        // ---- 感情語 ----
        // 否定語を含む形（「良くない」「面白くない」）は先に拾って、肯定語として数えない。
        private static readonly Regex NegatedPositive = new(
            "(?:良|よ)くな(?:い|かった)|面白くな|おもしろくな|可愛くな|かわいくな|エロくな|抜けな|興奮しな|" +
            "魅力(?:が|は)?(?:な|あま)|満足でき(?:な|ず)|楽しめな|おすすめしな|オススメしな",
            RegexOptions.Compiled);

        // 「ない」で終わる肯定の慣用句（「間違いない」「文句なし」）。否定として反転されないよう先に数えて取り除く。
        private static readonly Regex PositiveIdioms = new(
            "間違いな|違いな|申し分な|文句(?:の|は|も)?(?:な|無)|言うことな|欠点(?:は|が)?な|悪くは?な|問題な|ケチのつけようが?な|" +
            "言うことなし|文句なし|問題なし|(?:ない|なかった)(?:わけ|はず)(?:が)?な",
            RegexOptions.Compiled);

        // 「抜けないこともない」のような二重否定の留保。肯定とも否定とも数えず取り除く。
        private static readonly Regex NeutralIdioms = new(
            "(?:ない|なかった)(?:こと|でも|わけでは|とも限ら)(?:も)?(?:な|あり)",
            RegexOptions.Compiled);

        // 「ない」で終わる否定の慣用句（「もったいない」「いただけない」）。
        private static readonly Regex NegativeIdioms = new(
            "いただけな|頂けな|物足りな|ありがちで|見ていられな|耐えられな",
            RegexOptions.Compiled);

        // 「かわいくないパターンかと思いきや」「心配していた」のように、期待・予想とのずれや回想を述べる節。
        // 書き手の現在の評価ではないため、観点の極性を付けない。
        private static readonly Regex Expectation = new(
            "思いきや|思いきって|かと思|と思っていた|と思ってた|予想|心配|不安|かもしれな|だったが",
            RegexOptions.Compiled);

        // 作品そのものへの評価を表す強い肯定語。星と本文の食い違いの判定にはこれだけを使う。
        private static readonly Regex StrongPositive = new(
            "最高|素晴らしい|すばらしい|素敵|傑作|名作|良作|秀逸|満足|おすすめ|オススメ|お勧め|大当たり|" +
            "(?:良|よ)かった|良い|(?<!かわ)(?<!可愛)いい(?!加減)|最強|神作|神回|楽しめ|買って正解",
            RegexOptions.Compiled);

        // 容姿・好みなどへの弱い肯定語。「可愛いが内容は…」のように低評価のコメントにも普通に現れる。
        // 観点ごとの評判には使うが、食い違いの判定には使わない。
        private static readonly Regex WeakPositive = new(
            "当たり|ハマ|はまっ|興奮|抜け|ヌケ|エロい|エッチ|可愛い|かわいい|可愛さ|かわいさ|綺麗|きれい|美しい|さすが|流石|" +
            "見事|丁寧|自然|リアル|濃厚|ご褒美|高画質|鮮明|クリア|高精細",
            RegexOptions.Compiled);

        private static readonly Regex Negative = new(
            "残念|期待外れ|期待はずれ|がっかり|ガッカリ|微妙|イマイチ|いまいち|つまらな|退屈|最悪|駄作|不満|" +
            "不自然|棒読み|萎え|冷め|ひどい|酷い|ダメ|だめ|失敗|飽き|眠く|無駄|後悔|詐欺|金返せ|二度と|" +
            "雑(?:すぎ|だ|で|な)|安っぽい|わざとらしい|下手|ヘタ|いらない|不要|しょぼ|ショボ|クソ|くそ|" +
            "悪い|悪かった|悪すぎ|悪っ|悪く|酷く|ひどく|うるさい|聞き取りづら|聞き取りにく|見づらい|見にくい|割れ|ノイズ|" +
            "荒い|粗い|暗すぎ|暗くて|マイナス|汚い|キモい|きもい|ウザ|うざ|低画質",
            RegexOptions.Compiled);

        // 感情語の直後（数文字以内）に否定が来たら極性を反転する（「悪くない」「満足ではない」）。
        // 「良いところが分からなかった」「コスパ良いとは思えない」「ハマらなかった」のように、
        // 句読点を挟まず数文字後ろで打ち消すものも拾う。
        private static readonly Regex FollowingNegation = new(
            "^[^。、，,！!？?\\n]{0,8}?(?:(?<!では)ない(?!でしょう|だろう)|なかった|ません|ず|なし|無い|無かった|無く|(?:分|わ)からな|思えな|できな|できず)",
            RegexOptions.Compiled);

        // 逆接・留保を含むコメントは、良い点と悪い点を両方書いた批評であって、星と本文の食い違いではない。
        private static readonly Regex Contrast = new(
            "(?:だ|です|ます|ました|たが|るが|いが|が)[、,]|けど|けれど|しかし|ただ|でも|のに|惜しい|ものの|" +
            "反面|一方|とはいえ|ところが|だが|ですが|ますが",
            RegexOptions.Compiled);

        // 観点の極性は、観点のキーワードを含む「節」（句読点で区切った範囲）だけで判定する。
        // 文全体で判定すると、同じ文の別の話題の「残念」や「ない」に引きずられる。
        // 逆接（「〜いいが肝心の」「〜けど」）でも切る。「演技が良い」の格助詞「が」は切らない（直前が「い」の「が」だけ）。
        private static readonly Regex ClauseSplit = new(
            "[。．！!？?\\n、，,…‥]|けれども|けれど|けど|のに|だが|ですが|ますが|しかし|ただ|でも|(?<=い)が(?=\\S)",
            RegexOptions.Compiled);

        /// <summary>文章の肯定・否定の語数。</summary>
        /// <param name="Positive">肯定語の数（強・弱の合計。観点の評判に使う）</param>
        /// <param name="Negative">否定語の数</param>
        /// <param name="StrongPositive">うち、作品への評価を表す強い肯定語の数（食い違いの判定に使う）</param>
        public readonly record struct Sentiment(int Positive, int Negative, int StrongPositive = 0)
        {
            public int Net => Positive - Negative;
        }

        public static Sentiment SentimentOf(string text)
        {
            if (string.IsNullOrEmpty(text)) return default;

            int positive = 0, negative = 0, strong = 0;

            // 慣用句と「良くない」のような否定形は先に数え、本文からは取り除く（二重計上や反転を避ける）
            string rest = NeutralIdioms.Replace(text, " ");
            rest = PositiveIdioms.Replace(rest, _ => { positive++; strong++; return " "; });
            rest = NegativeIdioms.Replace(rest, _ => { negative++; return " "; });
            rest = NegatedPositive.Replace(rest, _ => { negative++; return " "; });

            foreach (Match m in StrongPositive.Matches(rest))
            {
                if (IsNegatedAfter(rest, m)) negative++; else { positive++; strong++; }
            }
            foreach (Match m in WeakPositive.Matches(rest))
            {
                if (IsNegatedAfter(rest, m)) negative++; else positive++;
            }
            foreach (Match m in Negative.Matches(rest))
            {
                if (IsNegatedAfter(rest, m)) positive++; else negative++;
            }

            return new Sentiment(positive, negative, strong);
        }

        private static bool IsNegatedAfter(string text, Match m)
        {
            int start = m.Index + m.Length;
            if (start >= text.Length) return false;
            int length = Math.Min(10, text.Length - start);
            return FollowingNegation.IsMatch(text.Substring(start, length));
        }

        /// <summary>
        /// 星と本文の食い違い。感想には「最高だが唯一の不満は…」のような批評が普通に混ざるため、
        /// 片側の語が明らかに優勢で、反対側の語がほぼ無い場合だけを食い違いとする。
        /// </summary>
        public static CommentMismatch MismatchOf(int rating, string text, Sentiment sentiment)
        {
            // 逆接を含むなら、良い点と悪い点の両方を書いた批評として扱う
            if (Contrast.IsMatch(text)) return CommentMismatch.None;

            if (rating >= 4 && sentiment.Positive == 0 && sentiment.Negative >= 2) return CommentMismatch.HighStarNegativeText;
            if (rating >= 4 && sentiment.Net <= -3) return CommentMismatch.HighStarNegativeText;

            // 低評価側は強い肯定語だけで判定する（容姿を褒めて作品を低く評価する、は普通にある）
            if (rating <= 2 && sentiment.Negative == 0 && sentiment.StrongPositive >= 2) return CommentMismatch.LowStarPositiveText;
            if (rating <= 2 && sentiment.StrongPositive - sentiment.Negative >= 3) return CommentMismatch.LowStarPositiveText;
            return CommentMismatch.None;
        }

        /// <summary>LLM が本文だけを読んで付けた判定。</summary>
        public const string VerdictPositive = "positive";
        public const string VerdictNegative = "negative";
        public const string VerdictMixed = "mixed";

        /// <summary>
        /// 判定結果の保存・参照に使う鍵（本文のハッシュ）。コメントの内容が変われば鍵も変わるため、
        /// 古い判定が別の内容に当たることはない。dmmCollect が書き、dmmWin が読む。
        /// </summary>
        public static string VerdictKey(CommentInput comment)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(comment.Title + "\n" + comment.Body);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16];
        }

        /// <summary>
        /// 解析器が食い違いと判定した候補を、LLM の判定で絞り込む。星の向きと逆だと LLM も判断したものだけ残す
        /// （「肯定も否定もある」は、批評として普通にあるため食い違いにしない）。判定が無ければ候補のまま。
        /// </summary>
        public static CommentMismatch Refine(CommentMismatch candidate, string? verdict)
        {
            if (verdict == null || candidate == CommentMismatch.None) return candidate;

            bool confirmed =
                (candidate == CommentMismatch.HighStarNegativeText && verdict == VerdictNegative) ||
                (candidate == CommentMismatch.LowStarPositiveText && verdict == VerdictPositive);
            return confirmed ? candidate : CommentMismatch.None;
        }

        public static CommentMismatch MismatchOf(CommentInput comment)
        {
            string text = comment.Title + "\n" + comment.Body;
            return MismatchOf(comment.Rating, text, SentimentOf(text));
        }

        public const string HighStarNegativeLabel = "星は高いが本文は否定的";
        public const string LowStarPositiveLabel = "星は低いが本文は肯定的";

        /// <summary>コメント1件の、観点ごとの極性（+1 肯定 / -1 否定）。極性が付かない観点は含めない。</summary>
        public static Dictionary<string, int> AspectPolarities(CommentInput comment)
        {
            var result = new Dictionary<string, int>();
            var clauses = ClauseSplit.Split(comment.Title + "\n" + comment.Body)
                .Where(s => s.Length > 0)
                .ToList();

            foreach (var (name, pattern) in Aspects)
            {
                int net = 0;
                foreach (var clause in clauses)
                {
                    if (!pattern.IsMatch(clause) || Expectation.IsMatch(clause)) continue;
                    net += Math.Sign(SentimentOf(clause).Net);
                }
                if (net != 0) result[name] = Math.Sign(net);   // 1コメントは1観点につき1票
            }
            return result;
        }

        private static bool IsMismatchLabeled(int total, int confirmed) =>
            confirmed >= MinConfirmedMismatchCount || total - confirmed >= MinMismatchCount;

        /// <summary>1作品の全コメントから、観点の評判と食い違いのラベルを求める。</summary>
        /// <param name="verdicts">LLM の判定（VerdictKey → positive/negative/mixed）。あれば食い違いの候補を絞り込む。</param>
        public static WorkAnalysis AnalyzeWork(IEnumerable<CommentInput> comments, IReadOnlyDictionary<string, string>? verdicts = null)
        {
            var analysis = new WorkAnalysis();
            var good = new Dictionary<string, int>();
            var bad = new Dictionary<string, int>();

            foreach (var comment in comments)
            {
                foreach (var (aspect, polarity) in AspectPolarities(comment))
                {
                    var target = polarity > 0 ? good : bad;
                    target[aspect] = target.GetValueOrDefault(aspect) + 1;
                }

                var mismatch = MismatchOf(comment);
                bool confirmed = false;
                if (verdicts != null && mismatch != CommentMismatch.None)
                {
                    string? verdict = verdicts.GetValueOrDefault(VerdictKey(comment));
                    mismatch = Refine(mismatch, verdict);
                    confirmed = verdict != null && mismatch != CommentMismatch.None;   // Refine を通ったので、判定があれば確認済み
                }

                switch (mismatch)
                {
                    case CommentMismatch.HighStarNegativeText:
                        analysis.HighStarNegativeCount++;
                        if (confirmed) analysis.HighStarNegativeConfirmed++;
                        break;
                    case CommentMismatch.LowStarPositiveText:
                        analysis.LowStarPositiveCount++;
                        if (confirmed) analysis.LowStarPositiveConfirmed++;
                        break;
                }
            }

            foreach (var (name, _) in Aspects)
            {
                int g = good.GetValueOrDefault(name), b = bad.GetValueOrDefault(name);
                int total = g + b;
                if (total < MinAspectEvidence) continue;

                double goodShare = (double)g / total, badShare = (double)b / total;

                if (goodShare >= GoodShare) analysis.Aspects.Add($"{name}が好評");
                else if (PositiveOnlyAspects.Contains(name)) continue;
                else if (badShare >= BadStrongShare && b >= 2) analysis.Aspects.Add($"{name}に不満");
                else if (g >= MinAspectEvidence && b >= MinAspectEvidence) analysis.Aspects.Add($"{name}は賛否");
                else if (badShare >= BadShare && b >= 2) analysis.Aspects.Add($"{name}に不満");
            }

            if (IsMismatchLabeled(analysis.HighStarNegativeCount, analysis.HighStarNegativeConfirmed)) analysis.Mismatches.Add(HighStarNegativeLabel);
            if (IsMismatchLabeled(analysis.LowStarPositiveCount, analysis.LowStarPositiveConfirmed)) analysis.Mismatches.Add(LowStarPositiveLabel);

            return analysis;
        }
    }
}
