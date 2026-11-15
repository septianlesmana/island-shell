using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;

namespace DynamicIsland;

/// Prayer times calculated locally (no internet) using the Indonesian Ministry of Religious Affairs (Kemenag) method:
/// Fajr 20°, Isha 18°, Asr Shafi'i (shadow 1×), ihtiyat +2 minutes, Imsak = Fajr − 10 minutes.
/// Sun position formulas follow the PrayTimes.org algorithm.
static class PrayerTimes
{
    public record Location(string Name, double Lat, double Lng, double? TimeZone);   // TimeZone null = the PC's time zone

    public record Prayer(string Name, DateTime Time);

    // Major Indonesian cities (WIB = 7, WITA = 8, WIT = 9)
    public static readonly Location[] Cities =
    {
        new("Jakarta", -6.2088, 106.8456, 7), new("Bogor", -6.5950, 106.8166, 7), new("Bandung", -6.9175, 107.6191, 7),
        new("Semarang", -6.9667, 110.4167, 7), new("Yogyakarta", -7.7956, 110.3695, 7), new("Surabaya", -7.2575, 112.7521, 7),
        new("Malang", -7.9666, 112.6326, 7), new("Medan", 3.5952, 98.6722, 7), new("Pekanbaru", 0.5071, 101.4478, 7),
        new("Palembang", -2.9761, 104.7754, 7), new("Pontianak", -0.0263, 109.3425, 7), new("Banjarmasin", -3.3186, 114.5944, 8),
        new("Balikpapan", -1.2379, 116.8529, 8), new("Samarinda", -0.5022, 117.1536, 8), new("Denpasar", -8.6705, 115.2126, 8),
        new("Makassar", -5.1477, 119.4327, 8), new("Manado", 1.4748, 124.8421, 8), new("Jayapura", -2.5337, 140.7181, 9),
    };

    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "prayer.json");

    /// Location in use (saved). Default: Jakarta until automatic location succeeds / a city is picked.
    public static Location Current { get; private set; } = Load();

    public static event Action? LocationChanged;

    public static void SetLocation(Location loc)
    {
        Current = loc;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(loc));
        }
        catch { }
        LocationChanged?.Invoke();
    }

    static Location Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<Location>(File.ReadAllText(SettingsPath)) ?? Cities[0];
        }
        catch { }
        return Cities[0];
    }

    public static bool HasSavedLocation => File.Exists(SettingsPath);

    /// Automatic location from Windows (requires Location permission). True on success.
    public static async Task<bool> DetectLocationAsync()
    {
        try
        {
            if (await Geolocator.RequestAccessAsync() != GeolocationAccessStatus.Allowed) return false;
            var pos = await new Geolocator { DesiredAccuracy = PositionAccuracy.Default }
                .GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(10));
            var p = pos.Coordinate.Point.Position;
            SetLocation(new Location("My location", p.Latitude, p.Longitude, null));
            return true;
        }
        catch { return false; }
    }

    // ================= Calculation =================

    public static List<Prayer> For(DateTime date, Location loc)
    {
        double tz = loc.TimeZone ?? TimeZoneInfo.Local.GetUtcOffset(date).TotalHours;
        double jd = JulianDate(date.Year, date.Month, date.Day) - loc.Lng / (15 * 24);

        // Initial estimate (hours), then recalculated with the sun position at that time
        double fajr = SunAngleTime(jd, 20, 5 / 24.0, loc.Lat, ccw: true);
        double sunrise = SunAngleTime(jd, 0.833, 6 / 24.0, loc.Lat, ccw: true);
        double dhuhr = MidDay(jd, 12 / 24.0);
        double asr = AsrTime(jd, 1, 13 / 24.0, loc.Lat);
        double maghrib = SunAngleTime(jd, 0.833, 18 / 24.0, loc.Lat, ccw: false);
        double isha = SunAngleTime(jd, 18, 18 / 24.0, loc.Lat, ccw: false);

        double adj = tz - loc.Lng / 15;
        DateTime At(double hours, double minutesExtra)
        {
            var t = date.Date.AddHours(hours + adj).AddMinutes(minutesExtra).AddSeconds(30);   // rounded to the nearest minute
            return new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);
        }

        const double ihtiyat = 2;
        var subuh = At(fajr, ihtiyat);
        return new List<Prayer>
        {
            new("Imsak", subuh.AddMinutes(-10)),
            new("Fajr", subuh),
            new("Sunrise", At(sunrise, -ihtiyat)),
            new("Dhuhr", At(dhuhr, ihtiyat)),
            new("Asr", At(asr, ihtiyat)),
            new("Maghrib", At(maghrib, ihtiyat)),
            new("Isha", At(isha, ihtiyat)),
        };
    }

    static double JulianDate(int y, int m, int d)
    {
        if (m <= 2) { y--; m += 12; }
        double a = Math.Floor(y / 100.0), b = 2 - a + Math.Floor(a / 4);
        return Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + d + b - 1524.5;
    }

    static (double Decl, double EqT) SunPosition(double jd)
    {
        double D = jd - 2451545.0;
        double g = Fix(357.529 + 0.98560028 * D, 360);
        double q = Fix(280.459 + 0.98564736 * D, 360);
        double L = Fix(q + 1.915 * Sin(g) + 0.020 * Sin(2 * g), 360);
        double e = 23.439 - 0.00000036 * D;
        double ra = ArcTan2(Cos(e) * Sin(L), Cos(L)) / 15;
        double eqt = q / 15 - Fix(ra, 24);
        double decl = ArcSin(Sin(e) * Sin(L));
        return (decl, eqt);
    }

    static double MidDay(double jd, double t) => Fix(12 - SunPosition(jd + t).EqT, 24);

    static double SunAngleTime(double jd, double angle, double t, double lat, bool ccw)
    {
        var (decl, _) = SunPosition(jd + t);
        double noon = MidDay(jd, t);
        double v = ArcCos((-Sin(angle) - Sin(decl) * Sin(lat)) / (Cos(decl) * Cos(lat))) / 15;
        return noon + (ccw ? -v : v);
    }

    static double AsrTime(double jd, double factor, double t, double lat)
    {
        var (decl, _) = SunPosition(jd + t);
        double angle = -ArcCot(factor + Tan(Math.Abs(lat - decl)));
        return SunAngleTime(jd, angle, t, lat, ccw: false);
    }

    static double Fix(double a, double b) { a -= b * Math.Floor(a / b); return a < 0 ? a + b : a; }
    static double Rad(double d) => d * Math.PI / 180;
    static double Deg(double r) => r * 180 / Math.PI;
    static double Sin(double d) => Math.Sin(Rad(d));
    static double Cos(double d) => Math.Cos(Rad(d));
    static double Tan(double d) => Math.Tan(Rad(d));
    static double ArcSin(double x) => Deg(Math.Asin(x));
    static double ArcCos(double x) => Deg(Math.Acos(Math.Clamp(x, -1, 1)));
    static double ArcTan2(double y, double x) => Deg(Math.Atan2(y, x));
    static double ArcCot(double x) => Deg(Math.Atan(1 / x));
}
