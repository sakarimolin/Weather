using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Xml.Linq;

static class Program
{
    record SiteLocation(string Name, string Country, double Latitude, double Longitude, double? ElevationMeters);

    class StationObservation
    {
        public string StationId { get; set; } = string.Empty;
        public string StationName { get; set; } = string.Empty;
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double? ElevationMeters { get; set; }
        public double? TemperatureC { get; set; }
        public double? RelativeHumidity { get; set; }
        public double? PressureHpa { get; set; }
    }

    static async Task Main(string[] args)
    {
        Console.WriteLine("WeatherConsole — FMI closest public weather station lookup");

var (latitude, longitude, fallbackElevation) = GetLocation(args);
Console.WriteLine($"Searching closest weather station near {latitude.ToString(CultureInfo.InvariantCulture)}, {longitude.ToString(CultureInfo.InvariantCulture)}...");

using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var stations = await FindNearestStationsAsync(httpClient, latitude, longitude, 2);

if (stations.Count == 0)
{
    Console.WriteLine("No weather station observations were found near the requested location.");
    return;
}

for (var i = 0; i < stations.Count; i++)
{
    Console.WriteLine();
    Console.WriteLine($"--- Station {i + 1} ---");
    PrintObservation(stations[i], latitude, longitude, fallbackElevation);
}

// Compute and print averages across the found stations
var temps = stations.Where(s => s.TemperatureC.HasValue).Select(s => s.TemperatureC!.Value).ToList();
var hums = stations.Where(s => s.RelativeHumidity.HasValue).Select(s => s.RelativeHumidity!.Value).ToList();
var press = stations.Where(s => s.PressureHpa.HasValue).Select(s => s.PressureHpa!.Value).ToList();

Console.WriteLine();
Console.WriteLine("--- Average of found stations ---");
if (temps.Count > 0)
{
    var avgTempC = temps.Average();
    var avgTempF = CelsiusToFahrenheit(avgTempC);
    Console.WriteLine($"Avg temperature: {avgTempC:F1} °C / {avgTempF:F1} °F");
}
else
{
    Console.WriteLine("Avg temperature: unavailable");
}

if (hums.Count > 0)
{
    Console.WriteLine($"Avg humidity: {hums.Average():F1} %");
}
else
{
    Console.WriteLine("Avg humidity: unavailable");
}

if (press.Count > 0)
{
    var avgPressure = press.Average();
    var avgPressureInHg = HpaToInHg(avgPressure);
    Console.WriteLine($"Avg pressure: {avgPressure:F1} hPa / {avgPressureInHg:F2} inHg");
}
else
{
    Console.WriteLine("Avg pressure: unavailable");
}

// Average density altitude if computable per station
var densityList = new List<double>();
foreach (var s in stations)
{
    var effectiveElevation = s.ElevationMeters ?? fallbackElevation;
    if (s.TemperatureC.HasValue && s.PressureHpa.HasValue && effectiveElevation.HasValue)
    {
        densityList.Add(ComputeDensityAltitude(s.TemperatureC.Value, s.PressureHpa.Value, effectiveElevation.Value));
    }
}

if (densityList.Count > 0)
{
    var avgDensity = densityList.Average();
    Console.WriteLine($"Avg density altitude: {avgDensity:F0} m ({avgDensity * 3.28084:F0} ft)");
}
else
{
    Console.WriteLine("Avg density altitude: unavailable");
}
    }

    static (double Latitude, double Longitude, double? ElevationMeters) GetLocation(string[] args)
{
    if (args.Length > 0)
    {
        var possibleSiteName = string.Join(' ', args).Trim();
        if (!string.IsNullOrWhiteSpace(possibleSiteName)
            && TryGetSiteLocationFromCsv(possibleSiteName, out var siteLocation))
        {
            Console.WriteLine($"Using site: {siteLocation.Name}, {siteLocation.Country}");
            return (siteLocation.Latitude, siteLocation.Longitude, siteLocation.ElevationMeters);
        }

        if (args.Length >= 2 && TryParseCoordinate(args[0], out var lat) && TryParseCoordinate(args[1], out var lon))
        {
            double? elevation = null;
            if (args.Length >= 3 && TryParseCoordinate(args[2], out var elevationValue))
            {
                elevation = elevationValue;
            }

            return (lat, lon, elevation);
        }
    }

    while (true)
    {
        Console.Write("Enter site name, or press Enter to enter latitude/longitude: ");
        var siteName = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(siteName))
        {
            if (TryGetSiteLocationFromCsv(siteName.Trim(), out var siteLocation))
            {
                Console.WriteLine($"Using site: {siteLocation.Name}, {siteLocation.Country}");
                return (siteLocation.Latitude, siteLocation.Longitude, siteLocation.ElevationMeters);
            }

            Console.WriteLine("Site not found in Locations.csv. Please enter coordinates instead.");
        }

        Console.Write("Enter latitude (for example 60.17): ");
        var latText = Console.ReadLine();
        Console.Write("Enter longitude (for example 24.94): ");
        var lonText = Console.ReadLine();

        if (TryParseCoordinate(latText, out var latValue) && TryParseCoordinate(lonText, out var lonValue))
        {
            var elevation = RequestElevation("Enter elevation in meters (optional, press Enter to skip): ");
            return (latValue, lonValue, elevation);
        }

        Console.WriteLine("Invalid coordinates. Please enter numeric latitude and longitude.");
    }
}

static bool TryGetSiteLocationFromCsv(string siteName, out SiteLocation siteLocation)
{
    var csvPath = GetLocationsCsvPath();
    if (!File.Exists(csvPath))
    {
        siteLocation = default!;
        return false;
    }

    var lines = File.ReadAllLines(csvPath);
    for (var i = 1; i < lines.Length; i++)
    {
        var line = lines[i].Trim();
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        var columns = line.Split(',');
        if (columns.Length < 5)
        {
            continue;
        }

        var name = columns[0].Trim();
        var country = columns[1].Trim();
        if (!TryParseCoordinate(columns[2], out var lat)
            || !TryParseCoordinate(columns[3], out var lon))
        {
            continue;
        }

        double? elevation = null;
        if (TryParseCoordinate(columns[4], out var elevValue))
        {
            elevation = elevValue;
        }

        if (string.Equals(name, siteName, StringComparison.OrdinalIgnoreCase))
        {
            siteLocation = new SiteLocation(name, country, lat, lon, elevation);
            return true;
        }
    }

    siteLocation = default!;
    return false;
}

static string GetLocationsCsvPath()
{
    var candidatePaths = new[]
    {
        Path.Combine(Environment.CurrentDirectory, "Locations.csv"),
        Path.Combine(AppContext.BaseDirectory, "Locations.csv")
    };

    return candidatePaths.FirstOrDefault(File.Exists) ?? candidatePaths[0];
}

static bool TryParseCoordinate(string? text, out double value)
{
    if (!string.IsNullOrWhiteSpace(text) && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
    {
        return true;
    }

    value = 0;
    return false;
}

static async Task<List<StationObservation>> FindNearestStationsAsync(HttpClient httpClient, double latitude, double longitude, int count)
{
    var nowUtc = DateTime.UtcNow;
    var startTime = nowUtc.AddHours(-12);
    var endTime = nowUtc;
    var parameters = "t2m,rh,p_sea";
    var storedQueryId = "fmi::observations::weather::timevaluepair";
    var searchRadii = new[] { 0.05, 0.1, 0.25, 0.5, 1.0, 2.0, 5.0 };

    foreach (var radius in searchRadii)
    {
        var bbox = BuildBbox(longitude, latitude, radius);
        var requestUri = $"https://opendata.fmi.fi/wfs?request=GetFeature&storedquery_id={Uri.EscapeDataString(storedQueryId)}&bbox={bbox}&starttime={Uri.EscapeDataString(startTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}&endtime={Uri.EscapeDataString(endTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}&parameters={Uri.EscapeDataString(parameters)}";

        try
        {
            using var response = await httpClient.GetAsync(requestUri);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            var observations = await ParseStationObservationsAsync(stream);

            if (observations.Count > 0)
            {
                return observations.Values
                    .OrderBy(obs => HaversineDistance(latitude, longitude, obs.Latitude, obs.Longitude))
                    .Take(count)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Request failed for radius {radius}°: {ex.Message}");
        }
    }

    return new List<StationObservation>();
}

static string BuildBbox(double lon, double lat, double radius)
{
    var minLon = lon - radius;
    var maxLon = lon + radius;
    var minLat = lat - radius;
    var maxLat = lat + radius;
    return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", minLon, minLat, maxLon, maxLat);
}

static async Task<Dictionary<string, StationObservation>> ParseStationObservationsAsync(System.IO.Stream xmlStream)
{
    var doc = await XDocument.LoadAsync(xmlStream, LoadOptions.None, CancellationToken.None);
    var om = XNamespace.Get("http://www.opengis.net/om/2.0");
    var omso = XNamespace.Get("http://inspire.ec.europa.eu/schemas/omso/3.0");
    var gml = XNamespace.Get("http://www.opengis.net/gml/3.2");
    var sams = XNamespace.Get("http://www.opengis.net/sampling/2.0");
    var wml2 = XNamespace.Get("http://www.opengis.net/waterml/2.0");
    var xlink = XNamespace.Get("http://www.w3.org/1999/xlink");

    var observations = new Dictionary<string, StationObservation>(StringComparer.Ordinal);

    foreach (var feature in doc.Descendants(omso + "PointTimeSeriesObservation"))
    {
        var observedPropertyHref = feature.Element(om + "observedProperty")?.Attribute(xlink + "href")?.Value;
        var parameterCode = GetQueryParameter(observedPropertyHref, "param");
        if (string.IsNullOrWhiteSpace(parameterCode))
        {
            continue;
        }

        var stationId = feature.Descendants(gml + "identifier")
            .FirstOrDefault(e => e.Attribute("codeSpace")?.Value?.Contains("stationcode/fmisid") == true)
            ?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(stationId))
        {
            continue;
        }

        var stationName = feature.Descendants(gml + "name")
            .FirstOrDefault(e => e.Attribute("codeSpace")?.Value?.Contains("locationcode/name") == true)
            ?.Value?.Trim()
            ?? feature.Descendants(gml + "name").FirstOrDefault()?.Value?.Trim()
            ?? stationId;

        var posText = feature.Descendants(gml + "pos").FirstOrDefault()?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(posText))
        {
            continue;
        }

        var coords = posText.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (coords.Length < 2
            || !double.TryParse(coords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(coords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
        {
            continue;
        }

        double? elevation = null;
        if (coords.Length >= 3 && double.TryParse(coords[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var alt))
        {
            elevation = alt;
        }

        var measurementPoints = feature.Descendants(wml2 + "MeasurementTVP").ToList();
        if (measurementPoints.Count == 0)
        {
            continue;
        }

        var lastPoint = measurementPoints.Last();
        var valueText = lastPoint.Element(wml2 + "value")?.Value?.Trim();
        if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var measurementValue))
        {
            continue;
        }

        if (!observations.TryGetValue(stationId, out var stationObservation))
        {
            stationObservation = new StationObservation
            {
                StationId = stationId,
                StationName = stationName,
                Latitude = lat,
                Longitude = lon,
                ElevationMeters = elevation,
            };
            observations[stationId] = stationObservation;
        }

        switch (parameterCode)
        {
            case "t2m":
                stationObservation.TemperatureC = measurementValue;
                break;
            case "rh":
                stationObservation.RelativeHumidity = measurementValue;
                break;
            case "p_sea":
                stationObservation.PressureHpa = measurementValue;
                break;
        }
    }

    return observations;
}

static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
{
    const double earthRadiusKm = 6371.0;
    var dLat = ToRadians(lat2 - lat1);
    var dLon = ToRadians(lon2 - lon1);
    var a = Math.Pow(Math.Sin(dLat / 2), 2)
            + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) * Math.Pow(Math.Sin(dLon / 2), 2);
    var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    return earthRadiusKm * c;
}

static double ToRadians(double value) => value * Math.PI / 180.0;

static string GetQueryParameter(string? url, string name)
{
    if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(name))
    {
        return string.Empty;
    }

    var queryIndex = url.IndexOf('?', StringComparison.Ordinal);
    var query = queryIndex >= 0 ? url[(queryIndex + 1)..] : url;
    var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
    foreach (var part in parts)
    {
        var pair = part.Split('=', 2);
        if (pair.Length == 2 && string.Equals(pair[0], name, StringComparison.OrdinalIgnoreCase))
        {
            return Uri.UnescapeDataString(pair[1]);
        }
    }

    return string.Empty;
}

static void PrintObservation(StationObservation observation, double requestLat, double requestLon, double? fallbackElevationMeters)
{
    var distance = HaversineDistance(requestLat, requestLon, observation.Latitude, observation.Longitude);
    Console.WriteLine();
    Console.WriteLine($"Station: {observation.StationName} (fmisid: {observation.StationId})");
    Console.WriteLine($"Location: {observation.Latitude.ToString(CultureInfo.InvariantCulture)}, {observation.Longitude.ToString(CultureInfo.InvariantCulture)}");
    var effectiveElevation = observation.ElevationMeters ?? fallbackElevationMeters;
    if (observation.ElevationMeters.HasValue)
    {
        Console.WriteLine($"Elevation: {observation.ElevationMeters.Value:F0} m");
    }
    else if (fallbackElevationMeters.HasValue)
    {
        Console.WriteLine($"Elevation (user supplied): {fallbackElevationMeters.Value:F0} m");
    }
    Console.WriteLine($"Distance: {distance:F2} km");
    Console.WriteLine();

    if (observation.TemperatureC.HasValue)
    {
        var tempF = CelsiusToFahrenheit(observation.TemperatureC.Value);
        Console.WriteLine($"Temperature: {observation.TemperatureC.Value:F1} °C / {tempF:F1} °F");
    }
    else
    {
        Console.WriteLine("Temperature: unavailable");
    }

    if (observation.RelativeHumidity.HasValue)
    {
        Console.WriteLine($"Humidity: {observation.RelativeHumidity.Value:F1} %");
    }
    else
    {
        Console.WriteLine("Humidity: unavailable");
    }

    if (observation.TemperatureC.HasValue && observation.RelativeHumidity.HasValue)
    {
        var dewPoint = ComputeDewPoint(observation.TemperatureC.Value, observation.RelativeHumidity.Value);
        var dewPointF = CelsiusToFahrenheit(dewPoint);
        Console.WriteLine($"Dew point: {dewPoint:F1} °C / {dewPointF:F1} °F");

        var wetBulb = ComputeWetBulb(observation.TemperatureC.Value, observation.RelativeHumidity.Value);
        var wetBulbF = CelsiusToFahrenheit(wetBulb);
        Console.WriteLine($"Wet temperature: {wetBulb:F1} °C / {wetBulbF:F1} °F");
    }
    else
    {
        Console.WriteLine("Wet temperature: unavailable");
    }

    if (observation.PressureHpa.HasValue)
    {
        var pressureInHg = HpaToInHg(observation.PressureHpa.Value);
        Console.WriteLine($"Air pressure: {observation.PressureHpa.Value:F1} hPa / {pressureInHg:F2} inHg");
    }
    else
    {
        Console.WriteLine("Air pressure: unavailable");
    }

    if (observation.TemperatureC.HasValue && observation.PressureHpa.HasValue && effectiveElevation.HasValue)
    {
        var densityAltitude = ComputeDensityAltitude(observation.TemperatureC.Value, observation.PressureHpa.Value, effectiveElevation.Value);
        Console.WriteLine($"Density altitude: {densityAltitude:F0} m ({densityAltitude * 3.28084:F0} ft)");
    }
    else
    {
        Console.WriteLine("Density altitude: unavailable");
    }
}

static double? RequestElevation(string prompt)
{
    while (true)
    {
        Console.Write(prompt);
        var text = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (TryParseCoordinate(text, out var elevation))
        {
            return elevation;
        }

        Console.WriteLine("Invalid elevation. Enter a numeric value in meters or press Enter to skip.");
    }
}

static double ComputeDewPoint(double temperatureC, double relativeHumidity)
{
    var a = 17.27;
    var b = 237.7;
    var rh = Math.Clamp(relativeHumidity, 0.0, 100.0) / 100.0;
    var alpha = (a * temperatureC) / (b + temperatureC) + Math.Log(rh);
    return (b * alpha) / (a - alpha);
}

static double ComputeWetBulb(double temperatureC, double relativeHumidity)
{
    var t = temperatureC;
    var rh = Math.Clamp(relativeHumidity, 0.0, 100.0);
    var result = t * Math.Atan(0.151977 * Math.Sqrt(rh + 8.313659))
               + Math.Atan(t + rh)
               - Math.Atan(rh - 1.676331)
               + 0.00391838 * Math.Pow(rh, 1.5) * Math.Atan(0.023101 * rh)
               - 4.686035;
    return result;
}

static double ComputeDensityAltitude(double temperatureC, double qnhHpa, double elevationMeters)
{
    var stationPressure = ComputeStationPressure(qnhHpa, elevationMeters);
    var pressureAltitude = ComputePressureAltitudeMeters(stationPressure);
    var isaTemperature = 15.0 - 0.0065 * pressureAltitude;
    return pressureAltitude + 65.235 * (temperatureC - isaTemperature);
}

static double CelsiusToFahrenheit(double celsius) => celsius * 9.0 / 5.0 + 32.0;

static double HpaToInHg(double hpa) => hpa * 0.029529983071445;

static double ComputeStationPressure(double qnhHpa, double elevationMeters)
{
    if (elevationMeters <= 0)
    {
        return qnhHpa;
    }

    var ratio = 1.0 - 0.0065 * elevationMeters / 288.15;
    return qnhHpa * Math.Pow(ratio, 5.255877);
}

static double ComputePressureAltitudeMeters(double pressureHpa)
{
    return 44330.77 * (1.0 - Math.Pow(pressureHpa / 1013.25, 0.190284));
}
}
