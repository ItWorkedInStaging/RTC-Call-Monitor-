using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RtcCallMonitor
{
    public static class Webhook
    {
        private static readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(10) };

        public static async Task Invoke(Uri uri, object data, ILogger logger)
        {
            try
            {
                var body = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json");
                using var response = await _client.PostAsync(uri, body);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning($"webhook {uri.Host}{uri.AbsolutePath} returned {(int)response.StatusCode} {response.ReasonPhrase}");
                }
            }
            catch (Exception e)
            {
                logger.LogWarning($"webhook {uri.Host}{uri.AbsolutePath} failed: {e.Message}");
            }
        }
    }
}
