using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Proxy
{
    internal static class Program
    {
        public static void Main(string[] args)
        {
            CreateWebHostBuilder(args)
                .Build()
                    .Run();
        }

        public static IWebHostBuilder CreateWebHostBuilder(string[] args) =>
            WebHost
                .CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    ServicePointManager.DefaultConnectionLimit = 1024;
                    services.Configure<ProxyOptions>(context.Configuration.GetSection("Proxy"));
                    var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
                    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("nc-proxy", "1"));
                    services.AddSingleton(client);
                })
                .Configure((applicationBuilder) =>
                    applicationBuilder
                        .Run(async (context) =>
                        {
                            var proxyContext = new ProxyContext(context);
                            foreach (var module in _modules)
                            {
                                var @continue = await module.ExecuteAsync(proxyContext).ConfigureAwait(false);
                                if (!@continue) break;
                            }

                            using (var requestMessage = proxyContext.CreateRequest())
                            {
                                AddConnectionLeaseTimeout(proxyContext.DestinationUri);
                                using (var responseMessage = await context.SendProxyRequestAsync(requestMessage).ConfigureAwait(false))
                                {
                                    await context.CopyProxyResponseAsync(responseMessage).ConfigureAwait(false);
                                }
                            }
                        }));

        private static readonly List<IModule> _modules = new List<IModule>
        {
            new RedirectionModule(),
        };


        private static void AddConnectionLeaseTimeout(Uri endpoint)
        {
            var uri = new Uri(endpoint.GetLeftPart(UriPartial.Path));
            if (!_endpoints.Contains(uri))
            {
                lock (_endpoints)
                {
                    if (!_endpoints.Contains(uri))
                    {
                        ServicePointManager.FindServicePoint(uri).ConnectionLeaseTimeout = 60 * 1000; // 60 seconds
                        _endpoints.Add(uri);
                    }
                }
            }
        }

        private static readonly SortedSet<Uri> _endpoints = new SortedSet<Uri>();
    }

    public sealed class ProxyContext
    {
        public HttpContext HttpContext { get; }

        public Uri DestinationUri { get; set; }

        public IDictionary<string, string> Variables { get; } = new Dictionary<string, string>();

        public IHeaderDictionary Headers { get; } = new HeaderDictionary();

        public ProxyContext(HttpContext context)
        {
            HttpContext = context;
        }

        internal HttpRequestMessage CreateRequest()
        {
            var requestMessage = HttpContext.CreateProxyRequest(DestinationUri);
            foreach (var header in Headers)
            {
                requestMessage.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string>)header.Value);
            }
            return requestMessage;
        }
    }

    public interface IModule
    {
        Task<bool> ExecuteAsync(ProxyContext context);
    }

    internal sealed class RedirectionModule : IModule
    {
        public Task<bool> ExecuteAsync(ProxyContext context)
        {
            var outgoingHeaders = context.Headers;
            var httpContext = context.HttpContext;
            var httpRequest = httpContext.Request;
            var options = httpContext.RequestServices.GetRequiredService<IOptions<ProxyOptions>>().Value;
            var (success, redirection, matches) = options.GetBestRedirection(httpRequest.Path);
            if (success)
            {
                var replacements = PrepareReplacements(httpRequest, matches.Groups);

                context.DestinationUri = new Uri(Replace(redirection.Pass, replacements));

                foreach (var setHeader in redirection.SetHeaders)
                {
                    var headerValue = Replace(setHeader.Value, replacements);
                    outgoingHeaders.Add(setHeader.Key, headerValue);
                }
            }

            return Task.FromResult(false);
        }

        private static string Replace(string input, IReadOnlyDictionary<string, string> replacements)
        {
            var b = new StringBuilder(input.Length);
            var i = 0;
            while (i < input.Length)
            {
                var startIndex = i;
                var tagStartIndex = input.IndexOf("$", i);
                if (tagStartIndex == -1)
                {

                    b.Append(input.Substring(i, input.Length - startIndex));
                    return b.ToString();
                }

                b.Append(input.Substring(startIndex, tagStartIndex - startIndex));

                i = tagStartIndex + 1;
                if (input[i] == '$')
                {
                    b.Append("$");
                }
                else
                {
                    while (i < input.Length && char.IsLetter(input[i]))
                        i++;
                    var name = input.Substring(tagStartIndex, i - tagStartIndex);
                    replacements.TryGetValue(name, out var replacement);
                    b.Append(replacement ?? string.Empty);
                }
            }
            return b.ToString();
        }

        private static IReadOnlyDictionary<string, string> PrepareReplacements(HttpRequest request, GroupCollection groups)
        {
            var dict = new Dictionary<string, string>
            {
                ["$method"] = request.Method,
                ["$scheme"] = request.Scheme,
                ["$host"] = request.Host.Host,
                ["$port"] = request.Host.Port?.ToString() ?? string.Empty,
                ["$path"] = request.Path
            };
            foreach (var group in groups.ToArray())
            {
                dict[$"${group.Name}"] = group.Value;
            }
            dict["$$"] = "$";
            return dict;
        }
    }

    internal sealed class ProxyOptions
    {
        public IDictionary<string, RedirectionOptions> Redirections { get; } = new Dictionary<string, RedirectionOptions>();

        public (bool, RedirectionOptions, Match) GetBestRedirection(string path)
        {
            foreach (var redirection in Redirections)
            {
                var matches = Regex.Match(path, redirection.Key);
                if (matches.Success)
                {
                    return (true, redirection.Value, matches);
                }
            }

            return (false, null, null);
        }
    }

    internal sealed class RedirectionOptions
    {
        public string Pass { get; set; }
        public IDictionary<string, string> SetHeaders { get; } = new Dictionary<string, string>();
    }

    internal static class Extensions
    {
        public static HttpRequestMessage CreateProxyRequest(this HttpContext context, Uri uri)
        {
            var request = context.Request;

            var requestMessage = new HttpRequestMessage();
            var requestMethod = request.Method;
            if (!HttpMethods.IsGet(requestMethod) &&
                !HttpMethods.IsHead(requestMethod) &&
                !HttpMethods.IsDelete(requestMethod) &&
                !HttpMethods.IsTrace(requestMethod))
            {
                var streamContent = new StreamContent(request.Body);
                requestMessage.Content = streamContent;
            }

            // Copy the request headers
            foreach (var header in request.Headers)
            {
                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && requestMessage.Content != null)
                {
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }

            requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-Host", context.Request.Host.ToString());
            requestMessage.Headers.Host = uri.Authority;
            requestMessage.RequestUri = uri;
            requestMessage.Method = new HttpMethod(request.Method);

            return requestMessage;
        }

        public static Task<HttpResponseMessage> SendProxyRequestAsync(this HttpContext context, HttpRequestMessage requestMessage)
        {
            var client = context.RequestServices.GetRequiredService<HttpClient>();
            return client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        }

        public static async Task CopyProxyResponseAsync(this HttpContext context, HttpResponseMessage responseMessage)
        {
            var response = context.Response;

            response.StatusCode = (int)responseMessage.StatusCode;
            foreach (var header in responseMessage.Headers)
            {
                response.Headers[header.Key] = header.Value.ToArray();
            }

            foreach (var header in responseMessage.Content.Headers)
            {
                response.Headers[header.Key] = header.Value.ToArray();
            }

            // SendAsync removes chunking from the response. This removes the header so it doesn't expect a chunked response.
            response.Headers.Remove("transfer-encoding");

            using (var responseStream = await responseMessage.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                // TODO [chain multiple streams to update/convert data]
                await responseStream.CopyToAsync(response.Body, StreamCopyBufferSize, context.RequestAborted).ConfigureAwait(false);
            }
        }

        private const int StreamCopyBufferSize = 81920;
    }
}
