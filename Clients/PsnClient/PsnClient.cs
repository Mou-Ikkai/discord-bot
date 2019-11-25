﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CompatApiClient;
using CompatApiClient.Compression;
using CompatApiClient.Utils;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using PsnClient.POCOs;
using PsnClient.Utils;
using JsonContractResolver = CompatApiClient.JsonContractResolver;

namespace PsnClient
{
    public class Client
    {
        private readonly HttpClient client;
        private readonly MediaTypeFormatterCollection dashedFormatters;
        private readonly MediaTypeFormatterCollection underscoreFormatters;
        private readonly MediaTypeFormatterCollection xmlFormatters;
        private static readonly MemoryCache ResponseCache = new MemoryCache(new MemoryCacheOptions { ExpirationScanFrequency = TimeSpan.FromHours(1) });
        private static readonly Regex ContainerIdLink = new Regex(@"(?<id>STORE-(\w|\d)+-(\w|\d)+)");
        private static readonly string[] KnownStoreLocales =
        {
            "en-US", "en-GB", "en-AE", "en-AR", "en-AU", "en-BG", "en-BH", "en-BR", "en-CA", "en-CL", "en-CO", "en-CY", "en-CZ", "en-DK", "en-FI", "en-GR", "en-HK", "en-HR", "en-HU", "en-ID",
            "en-IE", "en-IL", "en-IN", "en-IS", "en-KW", "en-LB", "en-MT", "en-MX", "en-MY", "en-NO", "en-NZ", "en-OM", "en-PE", "en-PL", "en-QA", "en-RO", "en-SA", "en-SE", "en-SG", "en-SI",
            "en-SK", "en-TH", "en-TR", "en-TW", "en-ZA", "ja-JP", "de-AT", "de-CH", "de-DE", "de-LU", "es-BO", "es-CR", "es-EC", "es-ES", "es-GT", "es-HN", "es-NI", "es-PA", "es-PY", "es-SV",
            "es-UY", "fr-BE", "fr-FR", "it-IT", "ko-KR", "nl-NL", "pt-PT", "ru-RU", "ru-UA", "zh-Hans-CN"
        };
        // Dest=87;ImageVersion=0001091d;SystemSoftwareVersion=4.8500;CDN=http://duk01.ps3.update.playstation.net/update/ps3/image/uk/2019_0828_c975768e5d70e105a72656f498cc9be9/PS3UPDAT.PUP;CDN_Timeout=30;
        private static readonly Regex FwVersionInfo = new Regex(@"Dest=(?<dest>\d+);ImageVersion=(?<image>[0-9a-f]+);SystemSoftwareVersion=(?<version>\d+\.\d+);CDN=(?<url>http[^;]+);CDN_Timeout=(?<timeout>\d+)",
            RegexOptions.Compiled | RegexOptions.ExplicitCapture | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        private static readonly string[] KnownFwLocales = { "us", "eu", "uk", "au", "ru", "jp", "br", "cn", "hk", "mx", "sa", "tw", "kr", };

        public Client()
        {
            client = HttpClientFactory.Create(new CustomTlsCertificatesHandler(), new CompressionMessageHandler());
            var dashedSettings = new JsonSerializerSettings
            {
                ContractResolver = new JsonContractResolver(NamingStyles.Dashed),
                NullValueHandling = NullValueHandling.Ignore
            };
            dashedFormatters = new MediaTypeFormatterCollection(new[] { new JsonMediaTypeFormatter { SerializerSettings = dashedSettings } });

            var underscoreSettings = new JsonSerializerSettings
            {
                ContractResolver = new JsonContractResolver(NamingStyles.Underscore),
                NullValueHandling = NullValueHandling.Ignore
            };
            underscoreFormatters = new MediaTypeFormatterCollection(new[] { new JsonMediaTypeFormatter { SerializerSettings = underscoreSettings } });
            xmlFormatters = new MediaTypeFormatterCollection(new[] {new XmlMediaTypeFormatter {UseXmlSerializer = true}});
        }

        public string[] GetLocales()
        {
            // Sony removed the ability to get the full store list, now relying on geolocation service instead
            return KnownStoreLocales;
        }

        public async Task<Stores> GetStoresAsync(string locale, CancellationToken cancellationToken)
        {
            try
            {
                var cookieHeaderValue = await GetSessionCookies(locale, cancellationToken).ConfigureAwait(false);
                using var getMessage = new HttpRequestMessage(HttpMethod.Get, "https://store.playstation.com/kamaji/api/valkyrie_storefront/00_09_000/user/stores");
                getMessage.Headers.Add("Cookie", cookieHeaderValue);
                using var response = await client.SendAsync(getMessage, cancellationToken).ConfigureAwait(false);
                try
                {
                    await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                    return await response.Content.ReadAsAsync<Stores>(underscoreFormatters, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    ConsoleLogger.PrintError(e, response);
                    return null;
                }
            }
            catch (Exception e)
            {
                ApiConfig.Log.Error(e);
                return null;
            }
        }

        public async Task<List<string>> GetMainPageNavigationContainerIdsAsync(string locale, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = null;
            try
            {
                var baseUrl = $"https://store.playstation.com/{locale}/";
                var sessionCookies = await GetSessionCookies(locale, cancellationToken).ConfigureAwait(false);
                using (var message = new HttpRequestMessage(HttpMethod.Get, baseUrl))
                {
                    message.Headers.Add("Cookie", sessionCookies);
                    response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);

                    await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                    var tries = 0;
                    while (response.StatusCode == HttpStatusCode.Redirect && tries < 10 && !cancellationToken.IsCancellationRequested)
                    {
                        using (var newLocationMessage = new HttpRequestMessage(HttpMethod.Get, response.Headers.Location))
                        {
                            newLocationMessage.Headers.Add("Cookie", sessionCookies);
                            var redirectResponse = await client.SendAsync(newLocationMessage, cancellationToken).ConfigureAwait(false);
                            response.Dispose();
                            response = redirectResponse;
                        }
                        tries++;
                    }
                    if (response.StatusCode == HttpStatusCode.Redirect)
                        return new List<string>(0);
                }

                using (response)
                    try
                    {
                        await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                        var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var matches = ContainerIdLink.Matches(html);
                        var result = new List<string>();
                        foreach (Match m in matches)
                            if (m.Groups["id"].Value is string id && !string.IsNullOrEmpty(id))
                                result.Add(id);
                        return result;
                    }
                    catch (Exception e)
                    {
                        ConsoleLogger.PrintError(e, response);
                        return null;
                    }
            }
            catch (Exception e)
            {
                ConsoleLogger.PrintError(e, response);
                return null;
            }
        }

        public async Task<StoreNavigation> GetStoreNavigationAsync(string locale, string containerId, CancellationToken cancellationToken)
        {
            try
            {
                var loc = locale.AsLocaleData();
                var baseUrl = $"https://store.playstation.com/valkyrie-api/{loc.language}/{loc.country}/999/storefront/{containerId}";
                using var message = new HttpRequestMessage(HttpMethod.Get, baseUrl);
                using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        return null;

                    await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                    return await response.Content.ReadAsAsync<StoreNavigation>(dashedFormatters, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    ConsoleLogger.PrintError(e, response);
                    return null;
                }
            }
            catch (Exception e)
            {
                ApiConfig.Log.Error(e);
                return null;
            }
        }

        public async Task<Container> GetGameContainerAsync(string locale, string containerId, int start, int take, Dictionary<string, string> filters, CancellationToken cancellationToken)
        {
            try
            {
                var loc = locale.AsLocaleData();
                var url = new Uri($"https://store.playstation.com/valkyrie-api/{loc.language}/{loc.country}/999/container/{containerId}");
                filters ??= new Dictionary<string, string>();
                filters["start"] = start.ToString();
                filters["size"] = take.ToString();
                filters["bucket"] = "games";
                url = url.SetQueryParameters(filters);
                using var message = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        return null;

                    await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                    return await response.Content.ReadAsAsync<Container>(dashedFormatters, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    ConsoleLogger.PrintError(e, response);
                    return null;
                }
            }
            catch (Exception e)
            {
                ApiConfig.Log.Error(e);
                return null;
            }
        }

        public async Task<Container> ResolveContentAsync(string locale, string contentId, int depth, CancellationToken cancellationToken)
        {
            try
            {
                var loc = locale.AsLocaleData();
                using var message = new HttpRequestMessage(HttpMethod.Get, $"https://store.playstation.com/valkyrie-api/{loc.language}/{loc.country}/999/resolve/{contentId}?depth={depth}");
                using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        return null;

                    await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                    return await response.Content.ReadAsAsync<Container>(dashedFormatters, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    ConsoleLogger.PrintError(e, response);
                    return null;
                }
            }
            catch (Exception e)
            {
                ApiConfig.Log.Error(e);
                return null;
            }
        }

        public async Task<TitlePatch> GetTitleUpdatesAsync(string productId, CancellationToken cancellationToken)
        {
            if (ResponseCache.TryGetValue(productId, out TitlePatch patchInfo))
                return patchInfo;

            using var message = new HttpRequestMessage(HttpMethod.Get, $"https://a0.ww.np.dl.playstation.net/tpl/np/{productId}/{productId}-ver.xml");
            using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            try
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;

                await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                patchInfo = await response.Content.ReadAsAsync<TitlePatch>(xmlFormatters, cancellationToken).ConfigureAwait(false);
                ResponseCache.Set(productId, patchInfo);
                return patchInfo ?? new TitlePatch { Tag = new TitlePatchTag { Packages = new TitlePatchPackage[0], },  };
            }
            catch (Exception e)
            {
                ConsoleLogger.PrintError(e, response);
                throw e;
            }
        }

        public async Task<TitleMeta> GetTitleMetaAsync(string productId, CancellationToken cancellationToken)
        {
            var id = productId + "_00";
            if (ResponseCache.TryGetValue(id, out TitleMeta meta))
                return meta;

            var hash = TmdbHasher.GetTitleHash(id);
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, $"https://tmdb.np.dl.playstation.net/tmdb/{id}_{hash}/{id}.xml");
                using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        return null;

                    await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                    meta = await response.Content.ReadAsAsync<TitleMeta>(xmlFormatters, cancellationToken).ConfigureAwait(false);
                    ResponseCache.Set(id, meta);
                    return meta;
                }
                catch (Exception e)
                {
                    ConsoleLogger.PrintError(e, response);
                    return null;
                }
            }
            catch (Exception e)
            {
                ApiConfig.Log.Error(e);
                return null;
            }
        }

        public async Task<Container> SearchAsync(string locale, string search, CancellationToken cancellationToken)
        {
            try
            {
                var loc = locale.AsLocaleData();
                var searchId = Uri.EscapeUriString(search);
                var queryId = Uri.EscapeDataString(searchId);
                var uri = new Uri($"https://store.playstation.com/valkyrie-api/{loc.language}/{loc.country}/999/faceted-search/{searchId}?query={queryId}&game_content_type=games&size=30&bucket=games&platform=ps3&start=0");
                using var message = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        return null;

                    await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                    return await response.Content.ReadAsAsync<Container>(dashedFormatters, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    ConsoleLogger.PrintError(e, response);
                    return null;
                }
            }
            catch (Exception e)
            {
                ApiConfig.Log.Error(e);
                return null;
            }
        }

        public async Task<List<FirmwareInfo>> GetHighestFwVersionAsync(CancellationToken cancellationToken)
        {
            var tasks = new List<Task<FirmwareInfo>>(KnownFwLocales.Length);
            foreach (var fwLocale in KnownFwLocales)
                tasks.Add(GetFwVersionAsync(fwLocale, cancellationToken));
            var allVersions = new List<FirmwareInfo>(KnownFwLocales.Length);
            foreach (var t in tasks)
                try
                {
                    var ver = await t.ConfigureAwait(false);
                    if (ver == null)
                        continue;

                    allVersions.Add(ver);
                }
                catch { }

            allVersions = allVersions.OrderByDescending(fwi => fwi.Version).ToList();
            if (allVersions.Any())
            {
                var maxFw = allVersions.First();
                var result = allVersions.Where(fwi => fwi.Version == maxFw.Version).ToList();
                return result;
            }

            return new List<FirmwareInfo>(0);
        }

        private async Task<string> GetSessionCookies(string locale, CancellationToken cancellationToken)
        {
            var loc = locale.AsLocaleData();
            var uri = new Uri("https://store.playstation.com/kamaji/api/valkyrie_storefront/00_09_000/user/session");
            var tries = 0;
            do
            {
                try
                {
                    HttpResponseMessage response;
                    using (var deleteMessage = new HttpRequestMessage(HttpMethod.Delete, uri))
                    using (response = await client.SendAsync(deleteMessage, cancellationToken))
                        if (response.StatusCode != HttpStatusCode.OK)
                            ConsoleLogger.PrintError(new InvalidOperationException("Couldn't delete current session"), response, false);

                    var authMessage = new HttpRequestMessage(HttpMethod.Post, uri)
                    {
                        Content = new FormUrlEncodedContent(new Dictionary<string, string>
                        {
                            ["country_code"] = loc.country,
                            ["language_code"] = loc.language,
                        })
                    };
                    using (authMessage)
                    using (response = await client.SendAsync(authMessage, cancellationToken).ConfigureAwait(false))
                        try
                        {
                            var cookieContainer = new CookieContainer();
                            foreach (var cookie in response.Headers.GetValues("set-cookie"))
                                cookieContainer.SetCookies(uri, cookie);
                            return cookieContainer.GetCookieHeader(uri);
                        }
                        catch (Exception e)
                        {
                            ConsoleLogger.PrintError(e, response, tries > 2);
                            tries++;
                        }
                }
                catch (Exception e)
                {
                    if (tries < 3)
                        ApiConfig.Log.Warn(e);
                    else
                        ApiConfig.Log.Error(e);
                    tries++;
                }
            } while (tries < 3);
            throw new InvalidOperationException("Couldn't obtain web session");
        }

        private async Task<FirmwareInfo> GetFwVersionAsync(string fwLocale, CancellationToken cancellationToken)
        {
            var uri = new Uri($"http://f{fwLocale}01.ps3.update.playstation.net/update/ps3/list/{fwLocale}/ps3-updatelist.txt");
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                        return null;

                    await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                    var data = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrEmpty(data))
                        return null;

                    if (FwVersionInfo.Match(data) is Match m && m.Success)
                    {
                        var ver = m.Groups["version"].Value;
                        if (!string.IsNullOrEmpty(ver) && ver.Length > 4)
                        {
                            if (ver.EndsWith("00"))
                                ver = ver[..4]; //4.85
                            else
                                ver = ver[..4] + "." + ver[4..].TrimEnd('0'); //4.851 -> 4.85.1
                        }
                        return new FirmwareInfo { Version = ver, DownloadUrl = m.Groups["url"].Value, Locale = fwLocale};
                    }

                    return null;
                }
                catch (Exception e)
                {
                    ConsoleLogger.PrintError(e, response);
                    return null;
                }
            }
            catch (Exception e)
            {
                ApiConfig.Log.Error(e, "Failed to GET " + uri);
                return null;
            }
        }
    }
}
