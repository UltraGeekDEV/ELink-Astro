using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace ELink.Stellarium;

/// <summary>A client for Stellarium's Remote Control plugin (HTTP, default port 8090): centre the view on a position, and read
/// the selected object (name and J2000 position).</summary>
public sealed class StellariumRemote : IDisposable
{
    private readonly HttpClient _http;

    public StellariumRemote(string baseUrl = "http://127.0.0.1:8090", HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(4);
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try { using var r = await _http.GetAsync("api/main/status", ct); return r.IsSuccessStatusCode; }
        catch (Exception) { return false; }
    }

    /// <summary>Centres the view on a J2000 position (focus by direction vector).</summary>
    public async Task ShowAsync(double raHours, double decDegrees, CancellationToken ct = default)
    {
        double a = raHours * 15 * Math.PI / 180, d = decDegrees * Math.PI / 180;
        string vec = string.Create(CultureInfo.InvariantCulture, $"[{Math.Cos(d) * Math.Cos(a):R},{Math.Cos(d) * Math.Sin(a):R},{Math.Sin(d):R}]");
        using var body = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("position", vec) });
        using var r = await _http.PostAsync("api/main/focus", body, ct);
        r.EnsureSuccessStatusCode();
    }

    /// <summary>Centres the view on a named object (Stellarium's own search).</summary>
    public async Task ShowAsync(string name, CancellationToken ct = default)
    {
        using var body = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("target", name) });
        using var r = await _http.PostAsync("api/main/focus", body, ct);
        r.EnsureSuccessStatusCode();
    }

    public sealed record Selection(string Name, string Kind, double RaHours, double DecDegrees, double Magnitude);

    /// <summary>The selected object, or null when nothing is selected.</summary>
    public async Task<Selection?> GetSelectionAsync(CancellationToken ct = default)
    {
        using var r = await _http.GetAsync("api/objects/info?format=json", ct);
        if (!r.IsSuccessStatusCode) return null;
        string text = await r.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('{')) return null;    // "no current selection" comes back as plain text
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        double Num(params string[] keys)
        {
            foreach (var k in keys)
                if (root.TryGetProperty(k, out var v))
                {
                    if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
                    if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
                }
            return double.NaN;
        }
        string Str(params string[] keys) { foreach (var k in keys) if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) return s; return ""; }
        double raDeg = Num("raJ2000", "ra"), dec = Num("decJ2000", "dec");
        if (double.IsNaN(raDeg) || double.IsNaN(dec)) return null;
        return new Selection(Str("localized-name", "name", "designations"), Str("object-type", "type"), ((raDeg / 15) % 24 + 24) % 24, dec, Num("vmag", "vmage"));
    }

    public void Dispose() => _http.Dispose();
}
