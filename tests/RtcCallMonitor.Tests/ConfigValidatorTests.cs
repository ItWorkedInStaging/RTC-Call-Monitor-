// ConfigValidatorTests.cs
// Version: 1.1.0
// Changelog:
//   1.1.0 - Empty LocalNetworks is now valid (interface subnets are used).
//   1.0.0 - Initial version. Startup validation of Detection, Webhook and Mqtt.

using RtcCallMonitor.Configuration;
using Xunit;

namespace RtcCallMonitor.Tests
{
    public class ConfigValidatorTests
    {
        private static DetectionOptions GoodDetection() => new DetectionOptions { LocalNetworks = new[] { "192.168.1.0/24" } };

        [Fact]
        public void DefaultsWithLocalNetwork_AreValid()
        {
            Assert.Empty(ConfigValidator.Validate(GoodDetection(), new WebhookOptions(), new MqttOptions()));
        }

        [Fact]
        public void EmptyLocalNetworks_IsValid()
        {
            Assert.Empty(ConfigValidator.Validate(new DetectionOptions(), new WebhookOptions(), new MqttOptions()));
        }

        [Fact]
        public void ShorthandLocalNetwork_IsAnError()
        {
            var detection = new DetectionOptions { LocalNetworks = new[] { "192.168.1/24" } };
            var errors = ConfigValidator.Validate(detection, new WebhookOptions(), new MqttOptions());
            Assert.Contains("192.168.1/24", Assert.Single(errors));
        }

        [Fact]
        public void EnabledMqttWithoutHost_IsAnError()
        {
            var errors = ConfigValidator.Validate(GoodDetection(), new WebhookOptions(), new MqttOptions { Enabled = true });
            Assert.Contains("Mqtt.Host is empty", Assert.Single(errors));
        }

        [Fact]
        public void WildcardInBaseTopic_IsAnError()
        {
            var mqtt = new MqttOptions { Enabled = true, Host = "ha.local", BaseTopic = "calls/#" };
            Assert.Contains("wildcards", Assert.Single(ConfigValidator.Validate(GoodDetection(), new WebhookOptions(), mqtt)));
        }

        [Fact]
        public void PasswordWithoutUsername_IsAnError()
        {
            var mqtt = new MqttOptions { Enabled = true, Host = "ha.local", Password = "x" };
            Assert.Contains("Mqtt.Username is empty", Assert.Single(ConfigValidator.Validate(GoodDetection(), new WebhookOptions(), mqtt)));
        }

        [Fact]
        public void EnabledWebhookWithoutUrls_IsAnError()
        {
            var errors = ConfigValidator.Validate(GoodDetection(), new WebhookOptions { Enabled = true }, new MqttOptions());
            Assert.Contains("neither", Assert.Single(errors));
        }

        [Fact]
        public void NonHttpWebhookUrl_IsAnError()
        {
            var webhook = new WebhookOptions { Enabled = true, CallStartUrl = "ftp://example.com/x" };
            Assert.Contains("not a valid http(s) URL", Assert.Single(ConfigValidator.Validate(GoodDetection(), webhook, new MqttOptions())));
        }

        [Fact]
        public void DisabledOutputs_AreNotValidated()
        {
            var webhook = new WebhookOptions { Enabled = false, CallStartUrl = "not a url" };
            var mqtt = new MqttOptions { Enabled = false, Host = "" };
            Assert.Empty(ConfigValidator.Validate(GoodDetection(), webhook, mqtt));
        }
    }
}
