using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using System.CommandLine;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.CommandLine.Invocation;

using static System.Console;

using HttpDoom.Core;
using HttpDoom.Core.Records;
using OpenQA.Selenium.DevTools;

namespace HttpDoom.Console
{
    internal static class Program
    {
        private static SemaphoreSlim _gate;
        private static readonly List<Response> Responses = new();

        private static async Task<int> Main(string[] args)
        {
            #region Application Banner

            WriteLine(" _   _ _   _        ______                      ");
            WriteLine("| | | | | | |       |  _  \\                     ");
            WriteLine("| |_| | |_| |_ _ __ | | | |___   ___  _ __ ___  ");
            WriteLine("|  _  | __| __| '_ \\| | | / _ \\ / _ \\| '_ ` _ \\ ");
            WriteLine("| | | | |_| |_| |_) | |/ / (_) | (_) | | | | | |");
            WriteLine("\\_| |_/\\__|\\__| .__/|___/ \\___/ \\___/|_| |_| |_|");
            WriteLine("              | |                               ");
            WriteLine("    v1.0.0    |_|    very fast http probing.    ");
            WriteLine();

            #endregion

            var rootCommand =
                new RootCommand("Minimalist (and VERY fast) HTTP-based attack surface analysis tool")
            {
                new Option<string[]>(new[] {"--headers", "-H"})
                {
                    Description = "Headers to be used in every request"
                },
                new Option<string[]>(new[] {"--ports", "-p"})
                {
                    Description = "Default ports for testing (default is 80, 443)."
                },
                new Option<string>(new[] {"--wordlist", "-w"})
                {
                    Description = "Path to the wordlist with targets to flyover against",
                    IsRequired = true
                },
                new Option<string>(new[] {"--output", "-o"})
                {
                    Description = "Directory to save all the enumerated information"
                },
                new Option<string>(new[] {"--screenshot-resolution", "-sR"})
                {
                    Description = "If -S, the resolution of the screenshot (default is 1920x1080)"
                },
                new Option<bool>(new[] {"--allow-automatic-redirect", "-a"})
                {
                    Description = "If HttpDoom will follow HTTP redirects (default is true)"
                },
                new Option<bool>(new[] {"--screenshot", "-S"})
                {
                    Description = "If HttpDoom will take screenshots from the website (default is false)"
                },
                new Option<bool>(new[] {"--verbose", "-v"})
                {
                    Description = "If HttpDoom will print errors, only useful for debugging (default is false)"
                },
                new Option<bool>(new[] {"--show-details", "-s"})
                {
                    Description = "If HttpDoom will print with details in stdout all the information got (default is false)"
                },
                new Option<bool>(new[] {"--ignore-tls", "-i"})
                {
                    Description = "If HttpDoom will ignore invalid TLS for HTTPS requests (default is true)"
                },
                new Option<bool>(new[] {"--resolve", "-r"})
                {
                    Description = "Resolve the domain enumerating the nameservers (default is false)"
                },
                new Option<bool>(new[] {"--detect", "-d"})
                {
                    Description = "If will try to match Wappalyzer rules in every response (default is false)"
                },
                new Option<int>(new[] {"--max-allowed-redirect", "-aL"})
                {
                    Description = "Set the limit of automatic redirects if -a is true (default is 4)"
                },
                new Option<int>(new[] {"--timeout", "-T"})
                {
                    Description = "Set the timeout for HTTP responses (default is 4000)"
                },
                new Option<int>(new[] {"--threads", "-t"})
                {
                    Description = "Set how many threads will HttpDoom utilize in runtime (default is 4)"
                }
            };

            rootCommand.Handler = CommandHandler.Create<Options>(async options => await CommandProxy(options));

            return await rootCommand.InvokeAsync(args);
        }

        private static async Task CommandProxy(Options options)
        {
            _gate = new SemaphoreSlim(options.Threads);

            if (!File.Exists(options.WordList))
            {
                Logger.LogError("Wordlist file don't exist!");
                Environment.Exit(1);
            }

            if (!string.IsNullOrEmpty(options.Output) && Directory.Exists(options.Output))
            {
                Logger.LogInformational("Output directory already exist, want to continue? [y/N]");
                Logger.LogWarning("Files can be rewritten!");
                Write("> ");
                var consoleKey = ReadKey();
                if (consoleKey.Key != ConsoleKey.Y)
                {
                    Logger.LogError("Output directory already exist!");
                    Environment.Exit(1);
                }
                WriteLine();
            }

            if (options.Screenshot && string.IsNullOrEmpty(options.Output))
            {
                Logger.LogError("You need to use -o when -S is utilized!");
                Environment.Exit(1);
            }

            if (options.Screenshot)
            {
                try
                {
                    var process = new Process
                    {
                        StartInfo =
                        {
                            FileName = "chromedriver",
                            Arguments = "--version",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        }
                    };
                    process.Start();

                    var output = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    if (string.IsNullOrEmpty(output))
                    {
                        Logger.LogError("Unable to check chromedriver version!");
                        Environment.Exit(1);
                    }

                    var version = output.Split(" ");
                    if (version.Length < 2)
                    {
                        Logger.LogError("Unable to check chromedriver version!");
                        Environment.Exit(1);
                    }

                    if (version[1] != "91.0.4472.101")
                    {
                        Logger.LogWarning("Unsupported chromedriver version detected");
                    }
                }
                catch
                {
                    Logger.LogError("Unable to run chromedriver, add to your environment variable PATH first!");
                    Environment.Exit(1);
                }
            }

            if (!options.Ports.Any())
            {
                options.Ports = new List<int> {80, 443};
            }

            if (options.Threads > Environment.ProcessorCount * 2)
            {
                Logger.LogWarning("You are using more threads than your processor is capable to handle!");
            }

            if (options.AllowAutomaticRedirect && options.Screenshot)
            {
                Logger.LogWarning("You are disabling automatic redirects, but with screenshots, chromedriver" +
                                  " will follow automatic redirects!");
            }
            
            var domains = await File.ReadAllLinesAsync(options.WordList);
            var targets = domains
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .Where(t =>
                {
                    Uri.TryCreate(t, UriKind.RelativeOrAbsolute, out var parsedUri);
                    return parsedUri is not null;
                })
                .ToList();

            if (targets.Count < domains.Length)
            {
                Logger.LogWarning("There is a difference in parsed URIs and the given wordlist!");
            }

            var targetsWithPorts = new List<Uri>();
            targets.ForEach(t => options.Ports.ForEach(p =>
            {
                switch (p)
                {
                    case 80:
                        var httpUri = new Uri("http://" + t);
                        if (!targetsWithPorts.Contains(httpUri))
                        {
                            targetsWithPorts.Add(httpUri);
                        }

                        break;
                    case 443:
                        var httpsUri = new Uri("https://" + t);
                        if (!targetsWithPorts.Contains(httpsUri))
                        {
                            targetsWithPorts.Add(httpsUri);
                        }

                        break;
                    default:
                        var otherUri = new Uri("http://" + t + ":" + p);
                        if (!targetsWithPorts.Contains(otherUri))
                        {
                            targetsWithPorts.Add(otherUri);
                        }

                        break;
                }
            }));

            var totalRequests = targetsWithPorts.Count;
            if (options.Screenshot) totalRequests *= 2;
            
            Logger.LogInformational($"HttpDoom will do at least {totalRequests} request(s)");

            var stopwatch = Stopwatch.StartNew();
            var tasks = targetsWithPorts
                .Select(target =>
                    Task.Run(() =>
                        RunAsync(target, options)))
                .ToArray();
            await Task.WhenAll(tasks);
            stopwatch.Stop();
            WriteLine();

            List<Response> synchronizedResponses;
            lock (Responses)
            {
                synchronizedResponses = Responses;
            }

            if (synchronizedResponses.Any())
            {
                Logger.LogSuccess($"Got a total of #{synchronizedResponses.Count} alive host(s)!");

                if (!string.IsNullOrEmpty(options.Output))
                {
                    Logger.LogInformational($"Saving to disk on directory {options.Output}");

                    Directory.CreateDirectory(options.Output);
                    var individualOutputDir = Path.Combine(options.Output, "Responses");
                    Directory.CreateDirectory(individualOutputDir);

                    synchronizedResponses.ForEach(async r =>
                    {
                        var outputFile = Path.Combine(individualOutputDir,
                            $"{r.OriginUri.Host}+{r.OriginUri.Port}.json");
                        var individualContent = JsonSerializer.Serialize(r);
                        await File.WriteAllTextAsync(outputFile, individualContent);
                    });

                    var compactPath = Path.Combine(options.Output, "responses.json");
                    var content = JsonSerializer.Serialize(synchronizedResponses);
                    await File.WriteAllTextAsync(compactPath, content);
                }
            }
            else
            {
                Logger.LogError("There is no hosts alive with the given ports!");
            }

            Logger.LogInformational($"Total elapsed time in requests is about {stopwatch.Elapsed}.");
        }

        private static async Task RunAsync(Uri target, Options options)
        {
            await _gate.WaitAsync();
            using var flyover = new Flyover(options);
            try
            {
                var response = await flyover.HitAsync(target);
                lock (Responses)
                {
                    Responses.Add(response);
                    Logger.LogSuccess($"{target.Host}:{target.Port} answered {response.StatusCode} " +
                                      $"({(int) response.StatusCode})");

                    if (options.ShowDetails)
                    {
                        Logger.LogDetails(response);
                    }
                }
            }
            catch (Exception e)
            {
                if (options.Verbose)
                {
                    Logger.LogError($"{target.Host}:{target.Port} got a unhandled exception: {e.Message}");
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    internal static class Logger
    {
        public static void LogError(string message)
        {
            ForegroundColor = ConsoleColor.Red;
            WriteLine($"[!] {message}");
            ResetColor();
        }

        public static void LogInformational(string message)
        {
            ForegroundColor = ConsoleColor.DarkGray;
            WriteLine($"[+] {message}");
            ResetColor();
        }

        public static void LogSuccess(string message)
        {
            ForegroundColor = ConsoleColor.DarkGreen;
            WriteLine($"[+] {message}");
            ResetColor();
        }

        public static void LogWarning(string message)
        {
            ForegroundColor = ConsoleColor.DarkYellow;
            WriteLine($"[*] {message}");
            ResetColor();
        }

        public static void LogDetails(Response response)
        {
            ForegroundColor = ConsoleColor.DarkBlue;
            WriteLine(response.IsSuccessStatusCode ? " + Got a good response" : " + Got a bad response");
            WriteLine(response.ResponseHeaders.Any()
                ? $" + With #{response.ResponseHeaders.Count()} response headers(s)"
                : " + Without any response headers");
            WriteLine(response.RequestHeaders.Any()
                ? $" + With #{response.RequestHeaders.Count()} request headers(s)"
                : " + Without any request headers");
            WriteLine(response.Cookies.Any()
                ? $" + With #{response.Cookies.Count} cookie(s)"
                : " + Without any cookies");
            WriteLine($" + With {response.Content.Length} byte(s) long content");
            WriteLine(response.Addresses.Any()
                ? $" + With #{response.Addresses.Length} nameserver(s)"
                : " + Without any resolved nameservers");

            if (!response.DetectedTechnologies.Any())
            {
                WriteLine(" + Unable to detect any technologies");
            }
            else
            {
                var technologies = response.DetectedTechnologies
                    .Select(dt => dt.Vendor)
                    .Distinct();
                var concatenated = string.Join(", ", technologies);
                WriteLine($" + Got technology fingerprint for given target: {concatenated}");
            }
            
            ResetColor();
        }
    }
}
