// WebhookNotifier.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Replaces Webhook.cs: POSTs {provider} on call start
//           and {provider, duration} on call end, with optional custom headers,
//           a 10 s timeout and logged failures.

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RtcCallMonitor.Configuration;

namespace RtcCallMonitor.Notifications
{
    public class WebhookNotifier : ICallNotifier
    {
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        private readonly WebhookOptions _options;
        private readonly ILogger<WebhookNotifier> _logger;

        public WebhookNotifier(IOptions<WebhookOptions> options, ILogger<WebhookNotifier> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public string Name => "webhook";
        public bool Enabled => _options.Enabled;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CallStartedAsync(CallInfo call, CancellationToken cancellationToken) =>
            PostAsync(_options.CallStartUrl, new { provider = call.Provider }, cancellationToken);

        public Task CallEndedAsync(CallInfo call, CancellationToken cancellationToken) =>
            PostAsync(_options.CallEndUrl,
                new { provider = call.Provider, duration = (int)(call.Duration ?? TimeSpan.Zero).TotalSeconds },
                cancellationToken);

        private async Task PostAsync(string url, object body, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                };
                foreach (var header in _options.Headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                using var response = await Client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("webhook {Target} returned {Status} {Reason}",
                        uri.Host + uri.AbsolutePath, (int)response.StatusCode, response.ReasonPhrase);
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                _logger.LogWarning("webhook {Target} failed: {Message}", uri.Host + uri.AbsolutePath, e.Message);
            }
        }
    }
}
