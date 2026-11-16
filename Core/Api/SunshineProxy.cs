using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenStreamMS.Core.Api;

/// <summary>
/// Helper que hace reverse proxy de peticiones HTTP a las instancias de Sunshine
/// que corren en localhost, inyectando las credenciales internas vía Basic Auth.
/// El panel de gestión (puerto 47990+) nunca se expone hacia fuera: OpenStreamMS
/// es el único frontend.
/// </summary>
public sealed class SunshineProxy
{
    private static readonly HttpClient _http = BuildHttpClient();

    private static HttpClient BuildHttpClient()
    {
        // Sunshine expone su panel por HTTPS con un cert auto-firmado. No podemos
        // validar la cadena; confiamos en que sólo llamamos a localhost:{port}.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            AllowAutoRedirect                         = false,
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("OpenStreamMS-Proxy");
        return c;
    }

    /// <summary>
    /// Envía <paramref name="method"/> <paramref name="path"/> (p.ej. <c>"/api/pin"</c>)
    /// a la Sunshine de <paramref name="session"/> con Basic Auth y el cuerpo JSON
    /// opcional indicado. Devuelve la respuesta JSON parseada.
    /// </summary>
    public async Task<(int StatusCode, JsonElement? Body)> SendJsonAsync(
        StreamSession session, HttpMethod method, string path, object? body = null,
        CancellationToken ct = default)
    {
        // Sin credenciales (la sesión aún no se ha iniciado nunca) o con Sunshine sin responder
        // (en pausa por un puesto, relanzándose): 503 sin cuerpo, que cada endpoint convierte en
        // su error estructurado, en vez de una excepción que acababa en un 500 genérico.
        if (string.IsNullOrEmpty(session.SunshineAuthUser) ||
            string.IsNullOrEmpty(session.SunshineAuthPass))
            return (StatusCodes.Status503ServiceUnavailable, null);

        var url = $"https://127.0.0.1:{session.SunshineWebPort}{path}";
        using var req = new HttpRequestMessage(method, url);

        var basic = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{session.SunshineAuthUser}:{session.SunshineAuthPass}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        if (body is not null)
            req.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        string text;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return (StatusCodes.Status503ServiceUnavailable, null);
        }
        using (resp)
            text = await resp.Content.ReadAsStringAsync(ct);
        var statusCode = (int)resp.StatusCode;

        JsonElement? parsed = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                parsed = doc.RootElement.Clone();
            }
            catch { /* no-JSON response */ }
        }

        return (statusCode, parsed);
    }
}
