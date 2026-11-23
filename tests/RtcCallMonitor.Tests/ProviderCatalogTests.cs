// ProviderCatalogTests.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Provider file parsing, folder loading, inline
//           ranges, lookups and error reporting.

using System;
using System.Collections.Generic;
using System.IO;
using RtcCallMonitor.Configuration;
using Xunit;

namespace RtcCallMonitor.Tests
{
    public class ProviderCatalogTests
    {
        [Fact]
        public void ParseLines_SkipsCommentsAndBlankLines()
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var lines = new[] { "# header", "", "3.7.35.0/25   # trailing comment", "   ", "13.107.64.0/18" };

            var result = ProviderCatalog.ParseLines(lines, "zoom.txt", errors, warnings);

            Assert.Equal(2, result.Count);
            Assert.Empty(errors);
            Assert.Empty(warnings);
        }

        [Fact]
        public void ParseLines_ReportsInvalidLineWithLineNumber()
        {
            var errors = new List<string>();
            var result = ProviderCatalog.ParseLines(new[] { "3.7.35.0/25", "not-a-range" }, "providers/zoom.txt", errors, new List<string>());

            Assert.Single(result);
            var error = Assert.Single(errors);
            Assert.Contains("providers/zoom.txt line 2", error);
        }

        [Fact]
        public void ParseLines_WarnsWhenHostBitsAreSet()
        {
            var warnings = new List<string>();
            ProviderCatalog.ParseLines(new[] { "202.173.24.0/20" }, "gotomeeting.txt", new List<string>(), warnings);
            Assert.Contains("202.173.16.0/20", Assert.Single(warnings));
        }

        [Fact]
        public void Load_ReadsOneFilePerProvider_AndMergesInline()
        {
            var root = Path.Combine(Path.GetTempPath(), "rtcmon-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "providers"));
            try
            {
                File.WriteAllLines(Path.Combine(root, "providers", "Zoom.txt"), new[] { "# zoom", "170.114.0.0/16" });
                File.WriteAllLines(Path.Combine(root, "providers", "msteams.txt"), new[] { "52.112.0.0/14" });
                var options = new ProvidersOptions
                {
                    Directory = "providers",
                    Inline = new Dictionary<string, string[]> { ["zoom"] = new[] { "3.7.35.0/25" }, ["custom"] = new[] { "198.51.100.0/24" } }
                };

                var catalog = ProviderCatalog.Load(options, root);

                Assert.Empty(catalog.Errors);
                Assert.Equal(3, catalog.Networks.Count);
                Assert.Equal(2, catalog.Networks["zoom"].Length);
                Assert.Equal("zoom", catalog.Lookup("170.114.1.1"));
                Assert.Equal("zoom", catalog.Lookup("3.7.35.10"));
                Assert.Equal("msteams", catalog.Lookup("52.113.0.1"));
                Assert.Equal("custom", catalog.Lookup("198.51.100.7"));
                Assert.Null(catalog.Lookup("8.8.8.8"));
                Assert.Null(catalog.Lookup("not-an-ip"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Load_MissingFolderWithoutInline_IsAnError()
        {
            var catalog = ProviderCatalog.Load(new ProvidersOptions { Directory = "does-not-exist" }, Path.GetTempPath());
            Assert.Contains("Providers folder not found", Assert.Single(catalog.Errors));
        }
    }
}
