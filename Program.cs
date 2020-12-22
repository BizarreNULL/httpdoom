﻿using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Drawing;
using System.Net.Http;
using System.Threading;
using System.Text.Json;
using System.Diagnostics;
using System.CommandLine;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.CommandLine.Invocation;

using static System.Console;

using Pastel;

using HttpDoom.Records;
using HttpDoom.Utilities;

namespace HttpDoom
{
    internal static class Program
    {
        private static SemaphoreSlim _gate;
        
        private static async Task<int> Main(string[] args)
        {
            CursorVisible = false;

            #region Application Banner

            var (left, top) = GetCursorPosition();
            WriteLine(string.Join(string.Empty, Banner.Hello
                .Select(s => s.ToString().Pastel("EE977F").PastelBg("FFFFC5"))));
            SetCursorPosition(left + 12, top + 2);
            Write("HttpDoom, flyover");
            SetCursorPosition(left + 12, top + 3);
            Write("to your horizon.");
            SetCursorPosition(0, top + 5);

            WriteLine("   v0.3".Pastel("EE977F"));
            WriteLine();

            #endregion

            #region Command Line Argument Parsing

            var commands = new RootCommand
            {
                new Option<bool>(new[] {"--debug", "-d"})
                {
                    Description = "Print debugging information"
                },
                new Option<int>(new[] {"--http-timeout", "-t"})
                {
                    Description = "Timeout in milliseconds for HTTP requests (default is 5000)"
                },
                new Option<int>(new[] {"--threads", "-T"})
                {
                    Description = $"Number of concurrent threads (default is {Environment.ProcessorCount})"
                },
                new Option<string>(new[] {"--output-directory", "-o"})
                {
                    Description = "Path to save the output directory"
                },
                new Option<int[]>(new[] {"--ports", "-p"})
                {
                    Description = "Set of ports to check (default is 80, 443, 8080 and 8433)"
                },
                new Option<string>(new[] {"--proxy", "-P"})
                {
                    Description = "Proxy to use for HTTP requests"
                },
                new Option<string>(new[] {"--word-list", "-w"})
                {
                    Description = "List of hosts to flyover against",
                    IsRequired = true
                }
            };

            commands.Description = "HttpDoom is a tool for response-based inspection of websites across a large " +
                                   "amount of hosts for quickly gaining an overview of HTTP-based attack surface.";

            #endregion

            commands.Handler = CommandHandler.Create<Options>(async options => await Router(options));

            CursorVisible = true;
            return await commands.InvokeAsync(args);
        }

        private static async Task Router(Options options)
        {
            if (options.Debug) Logger.Informational("Validating options...");

            #region Options Assertion

            if (string.IsNullOrEmpty(options.OutputDirectory))
            {
                options.OutputDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

                if (options.Debug)
                {
                    Logger.Informational($"Random output directory was generated, and is" + 
                                         options.OutputDirectory);
                }
            }

            if (!Directory.Exists(options.OutputDirectory))
            {
                Directory.CreateDirectory(options.OutputDirectory);
            }

            if (Directory.GetFiles(options.OutputDirectory).Any() || 
                Directory.GetDirectories(options.OutputDirectory).Any())
            {
                Logger.Error($"Output directory {options.OutputDirectory} is not empty!");
                Environment.Exit(-1);
            }

            if (!Wordlist.IsURL(options.WordList) && !File.Exists(options.WordList))
            {
                Logger.Error($"Wordlist {options.WordList} don't exist!");
                Environment.Exit(-1);
            }          

            if (!options.Ports.Any())
            {
                Logger.Error("You need at least one port!");
                Environment.Exit(-1);
            }

            if (options.HttpTimeout < 3000)
            {
                if (options.HttpTimeout == 666 && options.Debug)
                {
                    Logger.Error("https://www.youtube.com/watch?v=l482T0yNkeo");
                }

                Logger.Error("Invalid timeout for HTTP request. Must be >= 3000!");
                Environment.Exit(-1);
            }

            if (!string.IsNullOrEmpty(options.Proxy))
            {
                if (!options.Proxy.Contains(":"))
                {
                    Logger.Error($"{options.Proxy} is a invalid proxy string! (Correct is 127.0.0.1:1234)");
                    Environment.Exit(-1);
                }

                var values = options.Proxy.Split(":");
                if (values.Length != 2)
                {
                    Logger.Error($"{options.Proxy} is a invalid proxy string! (Correct is 127.0.0.1:1234)");
                    Environment.Exit(-1);
                }
            }

            if (options.Threads <= 0)
            {
                Logger.Error("Wait, what about the threads?!");
                Environment.Exit(-1);
            }
            
            if (options.Threads > Environment.ProcessorCount)
            {
                Logger.Warning("You may have issues with a larger thread count than your processor!");
            }
            else
            {
                Logger.Informational($"Started with #{options.Threads} thread(s)");
            }

            #endregion

            #region Wordlist Validation

            var wordlist = new Wordlist(options.WordList);
            wordlist.OnError = wle => {
                if (options.Debug)
                    Logger.Error(wle.Message);
            };

            var domains = (await wordlist.GetAsync())
                                        .Distinct()
                                        .Where(x => x.IsValid)
                                        .Select(x => x.Domain)
                                        .ToList();

            if (domains.Count == 0)
            {
                Logger.Error("Your wordlist is useless. Try a new one with actual fully qualified domains!");
                Environment.Exit(-1);
            }

            Logger.Informational($"After wordlist sanitization, the total of hosts is #{domains.Count}");
            if (options.Debug)
                Logger.Informational("Starting flyover with port(s): " +
                                     $"{string.Join(", ", options.Ports)}");

            #endregion

            #region Flyover Interactions

            var targets = new List<string>();
            domains.ForEach(d =>
                {
                    options.Ports.ToList()
                        .ForEach(p =>
                        {
                            if (p == 80)
                            {
                                targets.Add($"http://{d}");
                            }
                            else if (p == 443)
                            {
                                targets.Add($"https://{d}");
                            }
                            else
                            {
                                targets.Add($"http://{d}:{p}");
                                targets.Add($"https://{d}:{p}");   
                            }
                        });
                });

            Logger.Informational($"Added ports, the ({"possible".Pastel(Color.MediumAquamarine)}) " +
                                 $"total of requests is #{targets.Count}");
            
            Logger.Warning($"{"Mind the DoS:".Pastel(Color.Red)} This tool can cause instability " +
                           "problems on your network!");
            
            Logger.Warning("Initializing CPU-intensive tasks (this can take a while)...");

            var stopwatch = Stopwatch.StartNew();
            
            // Starting semaphore for limiting concurrent HTTP requests
            _gate = new SemaphoreSlim(options.Threads);  

            var tasks = targets.Select(target =>
                Task.Run(() => Trigger(target, options.Debug, options.HttpTimeout)));

            var flyoverResponseMessages = await Task.WhenAll(tasks)
                .ConfigureAwait(false);

            stopwatch.Stop();

            Logger.Success($"Flyover is done! Enumerated #{flyoverResponseMessages.Length} responses, " +
                           $"total elapsed time is {stopwatch.Elapsed:g}");

            #endregion

            #region flyoverResponseMessages Falidation

            flyoverResponseMessages = flyoverResponseMessages
                .Where(f => f != null)
                .ToArray();
            
            #endregion

            #region Result Persistance

            if (flyoverResponseMessages.Length != 0)
            {
                Logger.Success($"Got a total of #{flyoverResponseMessages.Length} alive hosts!");
                Logger.Informational($"Indexing results in " +
                                     $"{options.OutputDirectory.Pastel(Color.MediumAquamarine)}");

                var groupedOutput = Path.Combine(options.OutputDirectory, "general.json");
                await File.WriteAllTextAsync(groupedOutput, 
                    JsonSerializer.Serialize(flyoverResponseMessages));

                var individualOutputDirectory = Path.Combine(options.OutputDirectory, "Individual Results");
                Directory.CreateDirectory(individualOutputDirectory);
                
                flyoverResponseMessages
                    .ToList()
                    .ForEach(async message =>
                    {
                        var filename = message.Requested.RemoveSchema();
                        Path.GetInvalidFileNameChars()
                            .ToList()
                            .ForEach(c =>
                            {
                                if (!filename.Contains(c)) return;
                                filename = filename.Replace(c.ToString(), string.Empty);
                            });
                        
                        var individualOutput = Path.Combine(individualOutputDirectory, $"{filename}.json");
                        await File.WriteAllTextAsync(individualOutput, JsonSerializer.Serialize(message));
                    });
                
                Logger.Informational("HttpDoom is exiting...");
            }
            else
            {
                Logger.Error("All tested hosts is dead.");
            }
            
            #endregion
        }

        private static async Task<FlyoverResponseMessage> Trigger(string target, bool debug, int httpTimeout)
        {
            try
            {
                if (debug) Logger.Informational($"Requesting {target}...");

                await _gate.WaitAsync();
                var message = await Flyover(target, httpTimeout);
                Logger.Success($"Requested {message.Requested} is alive!");
                Logger.DisplayFlyoverResponseMessage(message);

                return message;
            }
            catch (Exception e)
            {
                if (debug)
                    Logger.Error(e.InnerException == null
                        ? $"Host {target} is dead: {e.Message}"
                        : $"Host {target} is dead: {e.InnerException.Message}");

                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static async Task<FlyoverResponseMessage> Flyover(string target, int timeout, string proxy = null)
        {
            var cookies = new CookieContainer();
            using var clientHandler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = cookies,
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };

            if (!string.IsNullOrEmpty(proxy))
            {
                clientHandler.Proxy = new WebProxy(proxy);
            }

            using var client = new HttpClient(clientHandler)
            {
                Timeout = TimeSpan.FromMilliseconds(timeout)
            };

            ServicePointManager.ServerCertificateValidationCallback += (_, _, _, _) => true;

            var uri = new Uri(target);
            var request = new HttpRequestMessage(HttpMethod.Get, uri)
            {
                Headers =
                {
                    {
                        "User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
                        "Chrome/87.0.4280.88 Safari/537.36"
                    }
                }
            };

            var response = await client.SendAsync(request);
            
            var unparsedHost = target
                .RemoveSchema()
                .Split(":");

            var domain = unparsedHost[0];
            var port = unparsedHost.Length switch
            {
                1 when uri.Scheme == "https" => 443,
                1 when uri.Scheme == "http" => 80,
                _ => int.Parse(unparsedHost[1])
            };

            var hostEntry = await Dns.GetHostEntryAsync(domain);

            return new FlyoverResponseMessage
            {
                Domain = domain,
                Addresses = hostEntry.AddressList.Select(a => a.ToString()).ToArray(),
                Requested = response.RequestMessage?.RequestUri?.ToString(),
                Port = port,
                Headers = response.Headers,
                Cookies = cookies.GetCookies(uri),
                StatusCode = (int) response.StatusCode,
                Content = await response.Content.ReadAsStringAsync()
            };
        }
    }
}