using Serilog;

namespace FoldersToB2.Notifications;

public class WebhookNotifier
{
    private readonly string _urlTemplate;
    private readonly HttpClient _http = new();

    public WebhookNotifier(string urlTemplate)
    {
        _urlTemplate = urlTemplate;
    }

    public async Task SendAsync(string message, string description)
    {
        var url = _urlTemplate
            .Replace("{MSG}", Uri.EscapeDataString(message))
            .Replace("{DESC}", Uri.EscapeDataString(description));

        Log.Information("Sending webhook notification: {Message}", message);

        try
        {
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                Log.Warning("Webhook returned {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Webhook request failed");
        }
    }
}
