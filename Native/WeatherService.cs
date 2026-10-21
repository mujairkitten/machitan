using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Windows.Devices.Geolocation;

namespace Machitan.Native;

public sealed record WeatherInfo(string TemperatureText, string ConditionLabel, string Glyph);

/// <summary>
/// Current weather for the bar's left cluster, from Open-Meteo (free, no API
/// key) at the device's location. Returns null when location is unavailable
/// or the fetch fails — the cluster then shows its glyph only. A weather
/// widget must show real data or nothing; never placeholder text.
/// </summary>
internal static class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<WeatherInfo?> TryGetCurrentAsync()
    {
        try
        {
            if (await Geolocator.RequestAccessAsync() != GeolocationAccessStatus.Allowed)
                return null;

            var locator = new Geolocator { DesiredAccuracyInMeters = 10_000 };
            var position = await locator.GetGeopositionAsync(
                maximumAge: TimeSpan.FromMinutes(10), timeout: TimeSpan.FromSeconds(10));
            var point = position.Coordinate.Point.Position;

            bool metric = RegionInfo.CurrentRegion.IsMetric;
            string unit = metric ? "celsius" : "fahrenheit";
            string url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={point.Latitude:F4}&longitude={point.Longitude:F4}&current=temperature_2m,weather_code&temperature_unit={unit}");

            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var current = doc.RootElement.GetProperty("current");
            double temperature = current.GetProperty("temperature_2m").GetDouble();
            int code = current.GetProperty("weather_code").GetInt32();

            var (label, glyph) = Describe(code);
            string temperatureText = $"{Math.Round(temperature)}°{(metric ? "C" : "F")}";
            return new WeatherInfo(temperatureText, label, glyph);
        }
        catch
        {
            return null;
        }
    }

    // WMO weather-code groups → label + Segoe Fluent glyph. Only glyphs in the
    // core MDL2 set are used; the label carries the precise meaning.
    private static (string Label, string Glyph) Describe(int code) => code switch
    {
        0 => ("Sunny", "\uE706"),                     // clear sky
        1 => ("Mostly sunny", "\uE707"),
        2 => ("Partly cloudy", "\uE707"),
        3 => ("Cloudy", "\uE753"),
        45 or 48 => ("Foggy", "\uE753"),
        51 or 53 or 55 or 56 or 57 => ("Drizzle", "\uE753"),
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => ("Rain", "\uE753"),
        71 or 73 or 75 or 77 or 85 or 86 => ("Snow", "\uE753"),
        95 or 96 or 99 => ("Thunderstorms", "\uE753"),
        _ => ("", "\uE753"),
    };
}
