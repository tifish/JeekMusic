namespace JeekMusic.Library;

/// <summary>智能标签的分析结果和物化标签（设计 §10）。</summary>
public sealed partial class LibraryDb
{
    /// <summary>某首歌的分析结果；没有时返回 null。</summary>
    public FeaturesRow? Features(string trackKey)
    {
        lock (Gate)
        {
            using var cmd = Command("SELECT track_key, signature, analyzer_version, data, failed FROM track_features WHERE track_key = $k");
            cmd.Parameters.AddWithValue("$k", trackKey);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new FeaturesRow(r.GetString(0), r.GetString(1), r.GetInt32(2), r.Str(3), r.Bool(4)) : null;
        }
    }

    /// <summary>一次写入多首歌的分析结果。</summary>
    public void SaveFeatures(IEnumerable<FeaturesRow> rows)
    {
        lock (Gate)
        {
            using var tx = Begin();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var row in rows)
                Exec("INSERT OR REPLACE INTO track_features(track_key, signature, analyzer_version, data, failed, analyzed_at) VALUES($k, $s, $v, $d, $f, $t)", tx,
                    ("$k", row.TrackKey), ("$s", row.Signature), ("$v", row.AnalyzerVersion), ("$d", row.Data), ("$f", row.Failed ? 1 : 0), ("$t", now));
            tx.Commit();
        }
    }

    /// <summary>"重新分析全部"：清掉分析结果，所有歌重新排队（手动修正保留）。</summary>
    public void ClearFeatures()
    {
        lock (Gate) Exec("DELETE FROM track_features");
    }

    /// <summary>用重算的结果替换全部物化标签（手动修正优先已经在调用方处理）。</summary>
    public void ReplaceAllTags(IEnumerable<(string trackKey, string dimension, string value, string source)> tags)
    {
        lock (Gate)
        {
            using var tx = Begin();
            Exec("DELETE FROM track_tag", tx);
            using var cmd = Command("INSERT OR REPLACE INTO track_tag(track_key, dimension, value, source) VALUES($k, $d, $v, $s)", tx);
            var k = cmd.Parameters.Add("$k", Microsoft.Data.Sqlite.SqliteType.Text);
            var d = cmd.Parameters.Add("$d", Microsoft.Data.Sqlite.SqliteType.Text);
            var v = cmd.Parameters.Add("$v", Microsoft.Data.Sqlite.SqliteType.Text);
            var s = cmd.Parameters.Add("$s", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var (trackKey, dimension, value, source) in tags)
            {
                k.Value = trackKey;
                d.Value = dimension;
                v.Value = value;
                s.Value = source;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
