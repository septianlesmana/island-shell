using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DynamicIsland;

/// Reads per-app notification counts from the Windows notification database
/// (Notification Center content + number badges sent by apps such as WhatsApp/Outlook).
static class NotificationCounts
{
    static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Microsoft\Windows\Notifications\wpndatabase.db");

    // ---------- "Clear All": notifications that arrived before this time are hidden ----------
    // Windows doesn't let regular apps remove other apps' notifications, so
    // "Clear All" records the time and older notifications are no longer shown/counted.

    static readonly string ClearedPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "notif-cleared.txt");

    static long? _clearedAt;

    /// Time of the last "Clear All" (FILETIME UTC); 0 = never.
    public static long ClearedAt
    {
        get
        {
            if (_clearedAt is long v) return v;
            try { _clearedAt = File.Exists(ClearedPath) ? long.Parse(File.ReadAllText(ClearedPath).Trim()) : 0; }
            catch { _clearedAt = 0; }
            return _clearedAt.Value;
        }
    }

    /// Raised after "Clear All" (the menu bar & Dock refresh).
    public static event Action? Cleared;

    public static void ClearAll()
    {
        _clearedAt = DateTime.UtcNow.ToFileTimeUtc();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ClearedPath)!);
            File.WriteAllText(ClearedPath, _clearedAt.Value.ToString());
        }
        catch { }
        Cleared?.Invoke();
    }

    static readonly Regex BadgeValue = new(@"<badge[^>]*\svalue=""(\d+)""", RegexOptions.IgnoreCase);

    public record Toast(string AppId, string Title, string Body, DateTime Time);

    static readonly Regex TextTag = new(@"<text[^>]*>(.*?)</text>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// Latest notifications in Notification Center (for the macOS-style Notification Center panel).
    // ---------- Cache: the database is only re-read when its file changes ----------
    static readonly object CacheLock = new();
    static string? _readStamp;
    static Dictionary<string, AppNotifs>? _readCache;
    static readonly Dictionary<int, (string Stamp, List<Toast> List)> _recentCache = new();

    /// Database version stamp: write time & size of the .db and -wal files, Clear All time, and the hour
    /// (so expired notifications are still filtered). Same = content unchanged.
    static string Stamp()
    {
        static long Mark(string path)
        {
            try { var f = new FileInfo(path); return f.Exists ? f.LastWriteTimeUtc.Ticks ^ (f.Length << 20) : 0; }
            catch { return 0; }
        }
        return $"{Mark(DbPath)}|{Mark(DbPath + "-wal")}|{ClearedAt}|{DateTime.UtcNow:yyyyMMddHH}";
    }

    public static List<Toast> ReadRecent(int max)
    {
        var stamp = Stamp();
        lock (CacheLock)
            if (_recentCache.TryGetValue(max, out var c) && c.Stamp == stamp) return c.List.ToList();
        var list = ReadRecentCore(max);
        lock (CacheLock) _recentCache[max] = (stamp, list);
        return list.ToList();
    }

    public static Dictionary<string, AppNotifs> Read()
    {
        var stamp = Stamp();
        lock (CacheLock)
            if (_readStamp == stamp && _readCache != null) return new(_readCache, StringComparer.OrdinalIgnoreCase);
        var result = ReadCore();
        lock (CacheLock) { _readStamp = stamp; _readCache = result; }
        return new(result, StringComparer.OrdinalIgnoreCase);
    }

    static List<Toast> ReadRecentCore(int max)
    {
        var list = new List<Toast>();
        if (!File.Exists(DbPath)) return list;
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT h.PrimaryId, n.Payload, n.ArrivalTime
                FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId
                WHERE n.Type = 'toast' AND (n.ExpiryTime IS NULL OR n.ExpiryTime = 0 OR n.ExpiryTime > $now)
                  AND n.ArrivalTime > $cleared
                ORDER BY n.ArrivalTime DESC LIMIT $max";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToFileTimeUtc());
            cmd.Parameters.AddWithValue("$cleared", ClearedAt);
            cmd.Parameters.AddWithValue("$max", max);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(0)) continue;
                string xml = r.IsDBNull(1) ? "" : r.GetValue(1) switch
                {
                    byte[] b => Encoding.UTF8.GetString(b),
                    string s => s,
                    _ => ""
                };
                var texts = TextTag.Matches(xml).Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim())
                                   .Where(t => t.Length > 0).ToList();
                DateTime time;
                try { time = r.IsDBNull(2) ? DateTime.Now : DateTime.FromFileTimeUtc(r.GetInt64(2)).ToLocalTime(); }
                catch { time = DateTime.Now; }
                list.Add(new Toast(r.GetString(0), texts.ElementAtOrDefault(0) ?? "",
                                   string.Join("\n", texts.Skip(1)), time));
            }
        }
        catch { }
        return list;
    }

    /// Badge: a number set by the app itself (unread count, cleared by the app).
    /// Toasts: arrival time (FILETIME UTC) of each notification still in Notification Center.
    public record AppNotifs(int? Badge, List<long> Toasts);

    /// Key: the sending app's ID (AUMID).
    static Dictionary<string, AppNotifs> ReadCore()
    {
        var result = new Dictionary<string, AppNotifs>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(DbPath)) return result;

        AppNotifs Get(string app) =>
            result.TryGetValue(app, out var n) ? n : result[app] = new AppNotifs(null, new List<long>());
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT h.PrimaryId, n.Type, n.Payload, n.ArrivalTime
                FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId
                WHERE n.Type IN ('toast', 'badge')
                  AND (n.ExpiryTime IS NULL OR n.ExpiryTime = 0 OR n.ExpiryTime > $now)";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToFileTimeUtc());

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(0)) continue;
                string app = r.GetString(0);
                string type = r.IsDBNull(1) ? "" : r.GetString(1);

                if (type == "toast")
                {
                    long arrived = r.IsDBNull(3) ? 0 : r.GetInt64(3);
                    if (arrived > ClearedAt) Get(app).Toasts.Add(arrived);   // notifications cleared with "Clear All" aren't counted
                }
                else if (!r.IsDBNull(2))
                {
                    string xml = r.GetValue(2) switch
                    {
                        byte[] b => Encoding.UTF8.GetString(b),
                        string s => s,
                        _ => ""
                    };
                    var m = BadgeValue.Match(xml);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int v))
                        result[app] = Get(app) with { Badge = v };
                }
            }
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }   // database locked / different format → no badges
        return result;
    }
}
