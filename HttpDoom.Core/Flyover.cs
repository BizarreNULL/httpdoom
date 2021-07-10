using System;
using System.IO;
using System.Net;
using System.Text;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json;

using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

using HttpDoom.Core.Records;
using Response = HttpDoom.Core.Records.Response;

namespace HttpDoom.Core
{
    public class Flyover : IDisposable
    {
        private static readonly string RulesPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".HttpDoomRules.json");

        private readonly Dictionary<string, string> _headers = new();
        private readonly CookieContainer _cookieContainer = new();

        private readonly HttpClientHandler _clientHandler;
        private readonly HttpClient _client;
        private readonly Options _options;

        public Flyover([NotNull] Options options)
        {
            _clientHandler = new HttpClientHandler
            {
                AllowAutoRedirect = options.AllowAutomaticRedirect,
                MaxAutomaticRedirections = options.MaxAllowedRedirect,
                UseCookies = true,
                CookieContainer = _cookieContainer
            };

            if (options.IgnoreTls)
            {
                _clientHandler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
                ServicePointManager.ServerCertificateValidationCallback += (_, _, _, _) => true;
            }

            if (options.Headers != null && options.Headers.Any())
            {
                options.Headers.ToList().ForEach(h =>
                {
                    var header = h.Split(":");
                    if (header.Length != 2) return;

                    _headers.Add(header[0], header[1]);
                });
            }

            _client = new HttpClient(_clientHandler)
            {
                Timeout = TimeSpan.FromMilliseconds(options.Timeout)
            };

            _options = options;
        }

        public async Task<Response> HitAsync(Uri target)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, target);
            if (_headers.Any())
            {
                _headers.ToList().ForEach(h =>
                {
                    var (name, value) = h;
                    request.Headers.TryAddWithoutValidation(name, value);
                });
            }

            var response = await _client.SendAsync(request);
            var resolved = Array.Empty<string>();

            if (_options.Resolve)
            {
                resolved = await Resolve(target.DnsSafeHost);
            }

            var content = await response.Content.ReadAsStringAsync();

            var model = new Response
            {
                Content = content,
                ContentSha256Sum = Sha256Sum(content),
                IsSuccessStatusCode = response.IsSuccessStatusCode,
                RedirectUri = response.RequestMessage?.RequestUri,
                Cookies = _cookieContainer.GetCookies(target).ToList(),
                StatusCode = response.StatusCode,
                ResponseHeaders = response.Headers,
                RequestHeaders = request.Headers,
                Addresses = resolved,
                OriginUri = target
            };

            if (_options.Detect)
            {
                await GetRulesAsync();
                var json = await File.ReadAllTextAsync(RulesPath);
                var fatRules = JsonConvert.DeserializeObject<Rules>(json);
                if (fatRules == null) throw new NullReferenceException("Unable to deserialize Wappalyzer rules.");

                var categories = new List<MinifiedCategory>();
                fatRules.Categories.ToList().ForEach(c =>
                {
                    var minifiedCategory = new MinifiedCategory
                    {
                        Id = int.Parse(c.Key),
                        Name = c.Value.Name
                    };
                    
                    categories.Add(minifiedCategory);
                });

                var rules = new List<MinifiedRule>();
                fatRules.Technologies.ToList().ForEach(t =>
                {
                    var (vendor, technology) = t;
                    var minifiedRule = new MinifiedRule
                    {
                        Vendor = vendor,
                        VendorWebsite = technology.VendorWebsite,
                        Description = technology.Description,
                        IsOpenSource = technology.IsOpenSource,
                        Implies = technology.Implies
                    };

                    technology.Categories.ForEach(c =>
                    {
                        var category = categories.Find(cat => cat.Id == c);
                        minifiedRule.Categories.Add(category);
                    });
                    
                    // TODO: Define regular expressions exportation for minified model
                    
                    rules.Add(minifiedRule);
                });
            }

            if (!_options.Screenshot) return model;

            try
            {
                var launcherOptions = new ChromeOptions
                {
                    AcceptInsecureCertificates = true
                };

                launcherOptions.AddArguments(
                    "--headless",
                    "--disable-gpu",
                    "--hide-scrollbars",
                    "--mute-audio",
                    "--disable-notifications",
                    "--no-first-run",
                    "--disable-crash-reporter",
                    "--ignore-certificate-errors",
                    "--incognito",
                    "--disable-infobars",
                    "--disable-sync",
                    "--no-default-browser-check",
                    "--disable-extensions",
                    "--silent",
                    "--window-size=" + _options.ScreenshotResolution,
                    "log-level=3"
                );

                var service = ChromeDriverService.CreateDefaultService();
                service.HideCommandPromptWindow = true;
                service.SuppressInitialDiagnosticInformation = true;

                using var driver = new ChromeDriver(service, launcherOptions);
                driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromMilliseconds(_options.Timeout);

                if (response.RequestMessage?.RequestUri != target)
                {
                    driver.Navigate().GoToUrl(target);
                }
                else
                {
                    driver.Navigate().GoToUrl(target);
                }

                var outputDirectory = Path.Combine(_options.Output, "Screenshots");
                if (!Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                var path = Path.Combine(outputDirectory, $"{target.Host}+{target.Port}.png");
                var screenshot = driver.GetScreenshot();

                screenshot.SaveAsFile(path, ScreenshotImageFormat.Png);

                driver.Close();
                driver.Dispose();

                model.ScreenshotPath = path;
            }
            catch
            {
                // ignored
            }

            return model;
        }

        private static async Task<string[]> Resolve(string target)
        {
            try
            {
                var hostEntry = await Dns.GetHostEntryAsync(target);
                return hostEntry.AddressList.Select(a => a.ToString()).ToArray();
            }
            catch
            {
                // ignored
            }

            return Array.Empty<string>();
        }

        private static string Sha256Sum(string value)
        {
            var sb = new StringBuilder();
            using var hash = SHA256.Create();

            foreach (var b in hash.ComputeHash(Encoding.Default.GetBytes(value)))
                sb.Append(b.ToString("x2"));

            return sb.ToString();
        }

        private static async Task GetRulesAsync()
        {
            const string technologies =
                "https://raw.githubusercontent.com/AliasIO/wappalyzer/master/src/technologies.json";

            if (!File.Exists(RulesPath))
            {
                var content = await new HttpClient().GetAsync(technologies);
                content.EnsureSuccessStatusCode();
                var response = await content.Content.ReadAsStringAsync();
                await File.WriteAllTextAsync(RulesPath, response);
            }
        }

        public void Dispose()
        {
            _clientHandler?.Dispose();
            _client?.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
