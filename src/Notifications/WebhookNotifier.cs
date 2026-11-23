// WebhookNotifier.cs
// Version: 1.1.0
// Changelog:
//   1.1.0 - TestAsync for the settings window's "Send test".
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

        /// <summary>POSTs a test call (provider "test") to url; returns (ok, plain-language message).</summary>
        public static async Task<(bool Ok, string Message)> TestAsync(string url, System.Collections.Generic.IDictionary<string, string> headers, CancellationToken ct)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return (false, "Enter a full http:// or https:// URL.");
            }
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { provider = "test", test = true }), Encoding.UTF8, "application/json")
                };
                if (headers != null)
                {
                    foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                using var response = await Client.SendAsync(request, ct);
                var code = $"{(int)response.StatusCode} {response.ReasonPhrase}";
                return response.IsSuccessStatusCode
                    ? (true, $"{uri.Host} answered {code}.")
                    : (false, $"{uri.Host} answered {code}.");
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                var inner = e;
                while (inner.InnerException != null) inner = inner.InnerException;
                return (false, $"Could not reach {uri.Host}: {inner.Message}");
            }
        }

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
