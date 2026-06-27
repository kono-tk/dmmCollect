using System.Collections.Generic;

namespace dmmCollect
{
    public static class AppConstants
    {
        public const string CONFIG_FILENAME = "dmmConfig.json";
        public const string DATA_JSON_FILENAME = "data.json";
        public const string VIDEO_HISTORY_FILENAME = "video_history.json";
        public const int CHUNK_SIZE = 50;

        public const string ERROR_SCREENSHOT_FILENAME = "error_screenshot.png";
        public const string ERROR_HTML_FILENAME = "error_page.html";
        public const string SEARCH_TXT_FILENAME = "search.txt";
        public const string LOST_FOUND_DIR_NAME = "lost+found";
        public const string FLAT_DIR_NAME = "FLAT";

        // セレクター (DMMログイン)
        public const string LOGIN_ID_SELECTOR = "#login_id";
        public const string PASSWORD_SELECTOR = "#password";
        public const string LOGIN_BUTTON_SELECTOR = "button[type=\"submit\"]:has-text(\"ログイン\")";
        public const string AD_POPUP_SELECTOR = "#coupon-popup";
        public const string AD_POPUP_CLOSE_BUTTON_SELECTOR = "#promotion_check_modal_close_button";
        public const string NO_RESULTS_SELECTOR = "text=\"絞り込み条件に一致する商品は見つかりませんでした。\"";

        // セレクター (Video)
        public const string MY_SEARCH_LIST_ITEM_SELECTOR = "ul.grid li";
        public const string LOADING_INDICATOR_SELECTOR = "#loading-dmm";
        public const string SEARCH_DATA_LOADING_SELECTOR = "div.mySearchLoading:has-text(\"検索用データを読み込み中です\")";
        public const string KEYWORD_INPUT_SELECTOR = "input[placeholder='キーワードで絞り込む']";
        public const string LOAD_MORE_BUTTON_SELECTOR = "button:has-text(\"\u3082\u3063\u3068\u898b\u308b\")";
        public const string PURCHASE_DATE_POPUP_SELECTOR = "div.text-white.text-xs.mt-2.text-center";
        public const string POPUP_DETAIL_LINK_SELECTOR = "a[href*=\"/av/content/\"]";
        public const string POPUP_CLOSE_BUTTON_SELECTOR = "button.absolute.top-0.right-0";
        public const string IMAGE_TAGS_SELECTOR = "ul.grid li img[src][alt]";

        // セレクター (Books)
        public const string BOOK_IMAGE_SELECTOR = "img[data-testid=\"book-image\"]";
        public const string BOOK_TOTAL_ITEMS_SELECTOR = "p.css-1dab49o";
        public const string BOOK_SERIES_LIST_SELECTOR = "ul[data-e2e=\"library\"] > li";
        public const string BOOK_SERIES_TITLE_SELECTOR = "span[data-is-short-title] a";
        public const string BOOK_VOLUMES_LINK_SELECTOR = "a[data-testid=\"link-button\"]";
        public const string BOOK_SUBTITLE_ITEM_SELECTOR = "div[data-testid=\"purchased-volume-book\"]";
        public const string BOOK_SUBTITLE_LINK_SELECTOR = "a[data-is-limited=\"false\"]";
        public const string BOOK_DOWNLOAD_LINK_SELECTOR = "a[href*=\"/download/?product_id=\"]";
        public const string BOOK_DETAIL_TABLE_SELECTOR = "dl";
        public const string BOOK_DESCRIPTION_SELECTOR = "div[data-testid=\"product-description\"]";
        public const string BOOK_PURCHASE_DATE_SELECTOR = "div[data-testid=\"purchased-date\"] span";

        // セレクター (DLsite)
        public const string DLSITE_LIBRARY_URL = "https://play.dlsite.com/library";
        public const string DLSITE_WORK_COUNT_SELECTOR = "span._labelXsmall_qoa7m_63";
        public const string DLSITE_PURCHASE_DATE_HEADER_SELECTOR = "div._header_1kd4u_27 span";
        public const string DLSITE_WORK_ITEM_SELECTOR = "div._list_1kd4u_153";
        public const string DLSITE_TITLE_SELECTOR = "span._titleMedium_qoa7m_39";
        public const string DLSITE_THUMBNAIL_SELECTOR = "div._thumbnail_1kd4u_117 span";
        public const string DLSITE_MAKER_SELECTOR = "div._makerName_1kd4u_196 span";
        public const string DLSITE_GENRE_SELECTOR = "span._label_x6ta7_1";
        public const string DLSITE_VIRTUOSO_SCROLLER_SELECTOR = "div[data-virtuoso-scroller='true']";
        public const string DLSITE_VIRTUOSO_ITEM_LIST_SELECTOR = "div[data-testid='virtuoso-item-list']";

        public static readonly List<string> DETAIL_KEYS = new()
        {
            "date", "description", "genres", "duration", "performers", "director", "series", "maker", "label"
        };

        public const string NO_DETAIL_PAGE_FLAG = "no_detail_page";

        public static readonly Dictionary<string, string> BOOK_DETAIL_KEYS_MAP = new()
        {
            { "シリーズ名", "series" },
            { "作家", "performers" },
            { "掲載誌・レーベル", "label" },
            { "出版社", "maker" },
            { "カテゴリー", "category" },
            { "ジャンル", "genres" },
            { "ページ数", "pages" },
            { "配信開始日", "release_date" },
            { "ファイル容量", "file_size" },
            { "ファイル形式", "file_format" }
        };

        public static readonly Dictionary<string, string> DOJIN_DETAIL_KEYS_MAP = new()
        {
            { "配信開始日", "release_date" },
            { "最終更新日", "last_updated_date" },
            { "作者", "performers" },
            { "作品形式", "format" },
            { "ページ数", "pages" },
            { "シリーズ", "series" },
            { "題材", "theme" },
            { "ジャンル", "genres" },
            { "ファイル容量", "file_size" }
        };
    }
}
