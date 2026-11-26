using System.Net.Http;
using System.Text.Json;
using ProjectCrystal.Core.Diagnostics;
using ProjectCrystal.Core.Models;

namespace ProjectCrystal.Native.System;

public sealed class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private WeatherInfo? _cached;
    private string _cachedQuery = "";
    private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;

    public async Task<WeatherInfo> GetWeatherAsync(string locationSetting, bool forceRefresh = false)
    {
        string query = string.IsNullOrWhiteSpace(locationSetting) ? "Auto" : locationSetting.Trim();
        if (!forceRefresh && _cached != null && _cachedQuery == query && (DateTimeOffset.Now - _lastFetch).TotalMinutes < 15)
            return _cached;

        try
        {
            double lat = 12.9716, lon = 77.5946;
            string locName = "Local";

            if (string.Equals(query, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string ipJson = await Http.GetStringAsync("http://ip-api.com/json/?fields=status,city,lat,lon");
                    using var ipDoc = JsonDocument.Parse(ipJson);
                    if (ipDoc.RootElement.TryGetProperty("status", out var st) && st.GetString() == "success")
                    {
                        lat = ipDoc.RootElement.GetProperty("lat").GetDouble();
                        lon = ipDoc.RootElement.GetProperty("lon").GetDouble();
                        locName = ipDoc.RootElement.GetProperty("city").GetString() ?? "Current City";
                    }
                }
                catch { }
            }
            else
            {
                string geoUrl = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count=1&language=en&format=json";
                string geoJson = await Http.GetStringAsync(geoUrl);
                using var geoDoc = JsonDocument.Parse(geoJson);
                if (geoDoc.RootElement.TryGetProperty("results", out var arr) && arr.GetArrayLength() > 0)
                {
                    var first = arr[0];
                    lat = first.GetProperty("latitude").GetDouble();
                    lon = first.GetProperty("longitude").GetDouble();
                    locName = first.GetProperty("name").GetString() ?? query;
                }
            }

            string wxUrl = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(global::System.Globalization.CultureInfo.InvariantCulture)}&longitude={lon.ToString(global::System.Globalization.CultureInfo.InvariantCulture)}&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m";
            string wxJson = await Http.GetStringAsync(wxUrl);
            using var wxDoc = JsonDocument.Parse(wxJson);
            var cur = wxDoc.RootElement.GetProperty("current");
            double temp = cur.GetProperty("temperature_2m").GetDouble();
            double apparent = cur.GetProperty("apparent_temperature").GetDouble();
            int humidity = cur.GetProperty("relative_humidity_2m").GetInt32();
            int code = cur.GetProperty("weather_code").GetInt32();
            double wind = cur.GetProperty("wind_speed_10m").GetDouble();

            _cached = new WeatherInfo(
                true,
                locName,
                temp,
                apparent,
                code,
                DecodeWmoWeatherCode(code),
                wind,
                humidity,
                DateTimeOffset.Now,
                "Open-Meteo Live");
            _cachedQuery = query;
            _lastFetch = DateTimeOffset.Now;
            return _cached;
        }
        catch (Exception ex)
        {
            CrystalLogger.Warn("Weather", $"Open-Meteo fetch failed: {ex.Message}");
            return _cached ?? new WeatherInfo(false, query, 0, 0, -1, "Weather Unavailable", 0, 0, DateTimeOffset.Now, "Offline");
        }
    }

    private static string DecodeWmoWeatherCode(int code) => code switch
    {
        0 => "Clear Sky",
        1 => "Mainly Clear",
        2 => "Partly Cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        61 or 63 or 65 => "Rain",
        71 or 73 or 75 => "Snow",
        80 or 81 or 82 => "Rain Showers",
        95 or 96 or 99 => "Thunderstorm",
        _ => "Fair"
    };
}
