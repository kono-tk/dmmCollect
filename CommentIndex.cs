using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace dmmCollect
{
    /// <summary>
    /// コメント全文検索用の SQLite 索引（BaseDirectory\comments.sqlite）の書き込み側。
    ///
    /// 原本は各コンテンツ実体フォルダの comments.json で、この索引はそこから作り直せる派生データ。
    /// dmmWin（読み取り側: Components/CommentSearchIndex.cs）と同じスキーマを使うため、
    /// テーブル定義を変える場合は両方を合わせること。
    ///
    /// 全文検索は FTS5 の trigram トークナイザ（日本語の部分一致。3文字以上で有効）。
    /// 2文字以下の語は読み取り側で LIKE にフォールバックする。
    /// </summary>
    public sealed class CommentIndex : IDisposable
    {
        public const string FileName = "comments.sqlite";

        private readonly SqliteConnection _connection;

        public CommentIndex(string baseDirectory)
        {
            string path = Path.Combine(baseDirectory, FileName);
            _connection = new SqliteConnection($"Data Source={path}");
            _connection.Open();

            // dmmWin が読み取り中でも書き込めるよう WAL にする
            Execute("PRAGMA journal_mode=WAL;");
            Execute("PRAGMA synchronous=NORMAL;");
            Execute(@"
                CREATE TABLE IF NOT EXISTS comments (
                    id INTEGER PRIMARY KEY,
                    cid TEXT NOT NULL,
                    rating INTEGER NOT NULL,
                    title TEXT NOT NULL,
                    body TEXT NOT NULL,
                    reviewer TEXT NOT NULL,
                    date TEXT NOT NULL,
                    purchased INTEGER NOT NULL,
                    helpful_count INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_comments_cid ON comments(cid);
                CREATE VIRTUAL TABLE IF NOT EXISTS comments_fts USING fts5(
                    title, body, content='comments', content_rowid='id', tokenize='trigram'
                );
                CREATE TRIGGER IF NOT EXISTS comments_ai AFTER INSERT ON comments BEGIN
                    INSERT INTO comments_fts(rowid, title, body) VALUES (new.id, new.title, new.body);
                END;
                CREATE TRIGGER IF NOT EXISTS comments_ad AFTER DELETE ON comments BEGIN
                    INSERT INTO comments_fts(comments_fts, rowid, title, body) VALUES ('delete', old.id, old.title, old.body);
                END;
                CREATE TABLE IF NOT EXISTS sources (
                    cid TEXT PRIMARY KEY,
                    path TEXT NOT NULL,
                    mtime_ticks INTEGER NOT NULL,
                    size INTEGER NOT NULL
                );");
        }

        /// <summary>comments.json が索引済みの内容と同じ（更新日時・サイズ一致）か。</summary>
        public bool IsUpToDate(string cid, string commentsJsonPath)
        {
            var info = new FileInfo(commentsJsonPath);
            if (!info.Exists) return false;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT mtime_ticks, size FROM sources WHERE cid = @cid";
            cmd.Parameters.AddWithValue("@cid", cid);
            using var reader = cmd.ExecuteReader();
            return reader.Read()
                && reader.GetInt64(0) == info.LastWriteTimeUtc.Ticks
                && reader.GetInt64(1) == info.Length;
        }

        /// <summary>
        /// 本文が「ネタバレの注意書きだけ」で保存されているコメントを持つ作品の cid。
        /// 展開せずに収集した古いデータ（確認済みでも全文を取り直す必要がある作品）を見つけるのに使う。
        /// </summary>
        public HashSet<string> FindCidsWithCollapsedSpoilers(string placeholder)
        {
            var cids = new HashSet<string>(StringComparer.Ordinal);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT cid FROM comments WHERE body = @placeholder";
            cmd.Parameters.AddWithValue("@placeholder", placeholder);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) cids.Add(reader.GetString(0));
            return cids;
        }

        /// <summary>1作品分のコメントを入れ替える（古い行は削除）。作品単位でトランザクションを張る。</summary>
        public void Upsert(ReviewCollection collection, string commentsJsonPath)
        {
            using var tx = _connection.BeginTransaction();

            using (var del = _connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM comments WHERE cid = @cid";
                del.Parameters.AddWithValue("@cid", collection.Cid);
                del.ExecuteNonQuery();
            }

            using (var ins = _connection.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = @"INSERT INTO comments (cid, rating, title, body, reviewer, date, purchased, helpful_count)
                                    VALUES (@cid, @rating, @title, @body, @reviewer, @date, @purchased, @helpful)";
                var pCid = ins.Parameters.Add("@cid", SqliteType.Text);
                var pRating = ins.Parameters.Add("@rating", SqliteType.Integer);
                var pTitle = ins.Parameters.Add("@title", SqliteType.Text);
                var pBody = ins.Parameters.Add("@body", SqliteType.Text);
                var pReviewer = ins.Parameters.Add("@reviewer", SqliteType.Text);
                var pDate = ins.Parameters.Add("@date", SqliteType.Text);
                var pPurchased = ins.Parameters.Add("@purchased", SqliteType.Integer);
                var pHelpful = ins.Parameters.Add("@helpful", SqliteType.Integer);

                foreach (var c in collection.Comments)
                {
                    pCid.Value = collection.Cid;
                    pRating.Value = c.Rating;
                    pTitle.Value = c.Title;
                    pBody.Value = c.Body;
                    pReviewer.Value = c.Reviewer;
                    pDate.Value = c.Date;
                    pPurchased.Value = c.Purchased ? 1 : 0;
                    pHelpful.Value = c.HelpfulCount;
                    ins.ExecuteNonQuery();
                }
            }

            var info = new FileInfo(commentsJsonPath);
            using (var src = _connection.CreateCommand())
            {
                src.Transaction = tx;
                src.CommandText = @"INSERT INTO sources (cid, path, mtime_ticks, size) VALUES (@cid, @path, @mtime, @size)
                                    ON CONFLICT(cid) DO UPDATE SET path = @path, mtime_ticks = @mtime, size = @size";
                src.Parameters.AddWithValue("@cid", collection.Cid);
                src.Parameters.AddWithValue("@path", commentsJsonPath);
                src.Parameters.AddWithValue("@mtime", info.Exists ? info.LastWriteTimeUtc.Ticks : 0L);
                src.Parameters.AddWithValue("@size", info.Exists ? info.Length : 0L);
                src.ExecuteNonQuery();
            }

            tx.Commit();
        }

        private void Execute(string sql)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public void Dispose() => _connection.Dispose();
    }
}
