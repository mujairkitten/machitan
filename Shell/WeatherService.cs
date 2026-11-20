using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Machitan.Core;
using Microsoft.UI.Dispatching;
using Windows.Devices.Geolocation;

namespace Machitan.Shell;

/// <summary>The current weather as Open-Meteo reported it.</summary>
/// <param name="TemperatureC">Air temperature 2 m above ground, degrees Celsius.</param>
/// <param name="Code">WMO weather code (0 clear ... 99 thunderstorm with hail).</param>
/// <param name="IsDay">False between sunset and sunrise at the location.</param>
/// <param name="FetchedAt">When Machitan got it.</param>
public sealed record WeatherReading(double TemperatureC, int Code, bool IsDay, DateTimeOffset FetchedAt)
{
    /// <summary>A reading older than this is not shown (real data or nothing).</summary>
    public static readonly TimeSpan ShowFor = TimeSpan.FromHours(2);

    public bool IsFresh => DateTimeOffset.Now - FetchedAt < ShowFor;

    /// <summary>A plain description of the WMO code.</summary>
    public string Description => Code switch
    {
        0 => IsDay ? Strings.WxClear : Strings.WxClearNight,
        1 => Strings.WxMainlyClear,
        2 => Strings.WxPartlyCloudy,
        3 => Strings.WxOvercast,
        45 or 48 => Strings.WxFog,
        51 or 53 or 55 => Strings.WxDrizzle,
        56 or 57 => Strings.WxFreezingDrizzle,
        61 or 63 or 65 => Strings.WxRain,
        66 or 67 => Strings.WxFreezingRain,
        71 or 73 or 75 or 77 => Strings.WxSnow,
        80 or 81 or 82 => Strings.WxRainShowers,
        85 or 86 => Strings.WxSnowShowers,
        95 => Strings.WxThunderstorm,
        96 or 99 => Strings.WxThunderHail,
        _ => Fmt.F(Strings.WxCode, Code),
    };

    /// <summary>A one-colour symbol (Segoe UI Symbol) for the code.</summary>
    public string Symbol => Code switch
    {
        0 or 1 => IsDay ? "\u2600" : "\u263E",
        2 => IsDay ? "\u26C5" : "\u2601",
        3 or 45 or 48 => "\u2601",
        >= 51 and <= 67 => "\u2602",
        >= 80 and <= 82 => "\u2602",
        >= 71 and <= 77 => "\u2744",
        85 or 86 => "\u2744",
        >= 95 => "\u26C8",
        _ => "\u2601",
    };
}

/// <summary>A place found by name (or typed as coordinates), for the weather.</summary>
internal readonly record struct WeatherPlace(string Name, double Latitude, double Longitude);

/// <summary>
/// The weather for the info line. Uses either a place chosen in Settings or Windows' location
/// (asked at most once an hour), rounded to about a kilometre, and asks Open-Meteo for the
/// current weather there every 15 minutes (5 minutes after a failure). Nothing is shown
/// without a real, recent reading. Runs only while something wants it (<see cref="SetWanted"/>).
/// Use on the UI thread.
/// </summary>
internal sealed class WeatherService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PlaceFor = TimeSpan.FromHours(1);
    private static readonly HttpClient Http = MakeClient();

    private readonly DispatcherQueueTimer _timer;
    private bool _wanted;
    private bool _busy;
    private (double Latitude, double Longitude)? _place;
    private DateTimeOffset _placeAt;
    private bool _manual;
    private (double Latitude, double Longitude)? _manualPlace;
    private string _lastProblem = string.Empty;

    internal WeatherService(DispatcherQueue queue)
    {
        _timer = queue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    /// <summary>Raised on the UI thread when <see cref="Current"/> changes.</summary>
    internal event Action? Changed;

    /// <summary>The latest reading, or null. Check <see cref="WeatherReading.IsFresh"/> before showing it.</summary>
    internal WeatherReading? Current { get; private set; }

    internal void SetWanted(bool wanted)
    {
        if (wanted == _wanted)
        {
            return;
        }

        _wanted = wanted;
        _timer.Stop();
        if (wanted)
        {
            Schedule(Current is { IsFresh: true } reading
                ? Every - (DateTimeOffset.Now - reading.FetchedAt)
                : TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>
    /// Weather for a chosen place (<paramref name="manual"/> true; no place means no weather) or
    /// for Windows' location. A change drops the reading for the old place and fetches anew.
    /// </summary>
    internal void UsePlace(bool manual, double? latitude, double? longitude)
    {
        (double Latitude, double Longitude)? place = manual && latitude is double lat && longitude is double lon
            ? (Math.Round(lat, 2), Math.Round(lon, 2))
            : null;
        if (manual == _manual && place == _manualPlace)
        {
            return;
        }

        _manual = manual;
        _manualPlace = place;
        if (Current is not null)
        {
            Current = null;
            Changed?.Invoke();
        }

        if (_wanted)
        {
            Schedule(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>
    /// Finds a place by name through Open-Meteo's place search (the typed text is sent to it),
    /// or reads typed coordinates ("-5.14, 119.42") without asking anyone. Never throws.
    /// </summary>
    internal static async Task<(WeatherPlace? Place, string Problem)> FindPlaceAsync(string query)
    {
        query = query.Trim();
        if (TryParseCoordinates(query, out double typedLatitude, out double typedLongitude))
        {
            string name = typedLatitude.ToString("0.00", CultureInfo.InvariantCulture) + ", "
                + typedLongitude.ToString("0.00", CultureInfo.InvariantCulture);
            return (new WeatherPlace(name, typedLatitude, typedLongitude), string.Empty);
        }

        if (query.Length < 2)
        {
            return (null, Strings.WxTypeMore);
        }

        try
        {
            string language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            string url = "https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(query)
                + "&count=1&language=" + Uri.EscapeDataString(language) + "&format=json";
            using HttpResponseMessage response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return (null, Fmt.F(Strings.WxSearchAnswered, (int)response.StatusCode));
            }

            await using Stream body = await response.Content.ReadAsStreamAsync();
            using JsonDocument document = await JsonDocument.ParseAsync(body);
            if (!document.RootElement.TryGetProperty("results", out JsonElement results)
                || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
            {
                return (null, Fmt.F(Strings.WxNoPlace, query));
            }

            JsonElement first = results[0];
            if (!first.TryGetProperty("latitude", out JsonElement latitude) || latitude.ValueKind != JsonValueKind.Number
                || !first.TryGetProperty("longitude", out JsonElement longitude) || longitude.ValueKind != JsonValueKind.Number)
            {
                return (null, Fmt.F(Strings.WxNoCoordinates, query));
            }

            string found = Text(first, "name");
            string region = Text(first, "admin1");
            string country = Text(first, "country");
            string display = found.Length > 0 ? found : query;
            if (region.Length > 0 && region != found)
            {
                display += ", " + region;
            }

            if (country.Length > 0)
            {
                display += ", " + country;
            }

            return (new WeatherPlace(display, latitude.GetDouble(), longitude.GetDouble()), string.Empty);
        }
        catch (Exception ex)
        {
            return (null, Fmt.F(Strings.WxSearchFailed, ex.GetType().Name));
        }
    }

    private static string Text(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    /// <summary>"lat, lon" or "lat lon", dots for decimals.</summary>
    private static bool TryParseCoordinates(string text, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        string[] parts = text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out latitude)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out longitude)
            && latitude is >= -90 and <= 90
            && longitude is >= -180 and <= 180;
    }

    private void Schedule(TimeSpan delay)
    {
        _timer.Stop();
        _timer.Interval = delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay;
        _timer.Start();
    }

    private async Task RefreshAsync()
    {
        if (_busy || !_wanted)
        {
            return;
        }

        _busy = true;
        TimeSpan next = AfterFailure;
        try
        {
            (double Latitude, double Longitude)? place = await LocateAsync();
            if (place is { } where)
            {
                WeatherReading? reading = await FetchAsync(where.Latitude, where.Longitude);
                if (reading is not null)
                {
                    bool first = Current is null;
                    Current = reading;
                    next = Every;
                    Note(string.Empty);
                    if (first)
                    {
                        Log.Info("Weather: first reading this run (" + reading.Description + ", "
                            + reading.TemperatureC.ToString("0.#", CultureInfo.InvariantCulture) + " C).");
                    }

                    Changed?.Invoke();
                }
            }
            else
            {
                next = Every;
            }
        }
        catch (Exception ex)
        {
            Note("Weather: the update failed (" + ex.GetType().Name + ": " + ex.Message + "); trying again in 5 minutes.");
        }
        finally
        {
            _busy = false;
            if (Current is { IsFresh: false })
            {
                Current = null;
                Changed?.Invoke();
            }

            if (_wanted)
            {
                Schedule(next);
            }
        }
    }

    /// <summary>
    /// The chosen place, or the PC's place from Windows rounded to 2 decimals (about 1 km).
    /// Null when there is none (no place chosen, or Windows does not allow its location).
    /// </summary>
    private async Task<(double Latitude, double Longitude)?> LocateAsync()
    {
        if (_manual)
        {
            if (_manualPlace is null)
            {
                Note("Weather: Settings say to use a chosen place, but none is set; weather stays hidden until one is found in Settings.");
            }

            return _manualPlace;
        }

        if (_place is not null && DateTimeOffset.Now - _placeAt < PlaceFor)
        {
            return _place;
        }

        GeolocationAccessStatus access;
        try
        {
            access = await Geolocator.RequestAccessAsync();
        }
        catch (Exception ex)
        {
            Note("Weather: Windows' location service is not available (" + ex.GetType().Name + "); weather stays hidden.");
            return null;
        }

        if (access != GeolocationAccessStatus.Allowed)
        {
            Note("Weather: Windows does not let Machitan use your location (Settings > Privacy & security > Location > "
                + "\"Let desktop apps access your location\"); weather stays hidden. Checked again every 15 minutes.");
            return null;
        }

        try
        {
            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            Geoposition position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(20));
            BasicGeoposition point = position.Coordinate.Point.Position;
            _place = (Math.Round(point.Latitude, 2), Math.Round(point.Longitude, 2));
            _placeAt = DateTimeOffset.Now;
            return _place;
        }
        catch (UnauthorizedAccessException)
        {
            Note("Weather: Windows refused the location (location is off for desktop apps); weather stays hidden.");
            return null;
        }
        catch (Exception ex)
        {
            Note("Weather: Windows could not find the location (" + ex.GetType().Name + "); trying again later.");
            return _place;
        }
    }

    private async Task<WeatherReading?> FetchAsync(double latitude, double longitude)
    {
        string url = string.Create(
            CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={latitude:0.00}&longitude={longitude:0.00}&current=temperature_2m,weather_code,is_day&timezone=auto");

        using HttpResponseMessage response = await Http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            Note("Weather: Open-Meteo answered " + (int)response.StatusCode + "; trying again in 5 minutes.");
            return null;
        }

        await using Stream body = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(body);
        if (!document.RootElement.TryGetProperty("current", out JsonElement current)
            || !current.TryGetProperty("temperature_2m", out JsonElement temperature)
            || temperature.ValueKind != JsonValueKind.Number
            || !current.TryGetProperty("weather_code", out JsonElement code)
            || code.ValueKind != JsonValueKind.Number)
        {
            Note("Weather: Open-Meteo's answer had no current temperature or weather code; trying again in 5 minutes.");
            return null;
        }

        bool isDay = !current.TryGetProperty("is_day", out JsonElement day)
            || day.ValueKind != JsonValueKind.Number
            || day.GetInt32() != 0;

        return new WeatherReading(temperature.GetDouble(), code.GetInt32(), isDay, DateTimeOffset.Now);
    }

    private static HttpClient MakeClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.TryParseAdd("Machitan/0.1");
        return client;
    }

    /// <summary>Logs a problem once, not on every retry.</summary>
    private void Note(string problem)
    {
        if (problem == _lastProblem)
        {
            return;
        }

        _lastProblem = problem;
        if (problem.Length > 0)
        {
            Log.Warn(problem);
        }
    }
}
