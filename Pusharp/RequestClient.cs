using Pusharp.Models;
using Pusharp.RequestParameters;
using Pusharp.Utilities;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Voltaic.Serialization.Json;

namespace Pusharp
{
    internal class RequestClient
    {
        public const int FreeAccountPushLimit = 500; // Pushbullet Free accounts are limited to 500 pushes per month.

        private const string RatelimitLimitHeaderName = "X-Ratelimit-Limit";
        private const string RatelimitRemainingHeaderName = "X-Ratelimit-Remaining";
        private const string RatelimitResetHeaderName = "X-Ratelimit-Reset";
        
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _semaphore;
        private readonly JsonSerializer _serializer;
        private readonly PushBulletClient _client;

        private string _rateLimit;
        private string _rateLimitReset;
        private string _remaining;

        public RequestClient(PushBulletClient client, PushBulletClientConfig config, JsonSerializer serializer)
        {
            _client = client;

            _http = new HttpClient
            {
                BaseAddress = new Uri(config.ApiBaseUrl)
            };

            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _http.DefaultRequestHeaders.Add("Access-Token", config.Token);

            _serializer = serializer;
            _semaphore = new SemaphoreSlim(1, 1);
        }

        private int RateLimit => int.TryParse(_rateLimit, out var value) ? value : 0;
        private int Remaining => int.TryParse(_remaining, out var amount) ? amount : 0;

        private DateTimeOffset RateLimitReset => DateTimeOffset.FromUnixTimeSeconds(long.TryParse(_rateLimitReset, out var seconds) ? seconds : 0);
        
        public Task SendAsync(string endpoint, HttpMethod method, ParametersBase parameters)
        {
            return SendAsync<EmptyModel>(endpoint, method, parameters);
        }

        public async Task<T> SendAsync<T>(string endpoint, HttpMethod method, ParametersBase parameters)
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                var request = new HttpRequestMessage(method, endpoint);
                parameters = parameters ?? EmptyParameters.Create();

                /* Needs changing to not throw
                var builder = new ParameterBuilder();
                parameters.VerifyParameters(builder);
                builder.ValidateParameters();
                */

                request.Content = new StringContent(parameters.BuildContent(_serializer), Encoding.UTF8,
                    "application/json");

                var requestTime = Stopwatch.StartNew();
                using (var response = await _http.SendAsync(request).ConfigureAwait(false))
                {
                    requestTime.Stop();

                    await _client.InternalLogAsync(new LogMessage(LogLevel.Verbose, $"{method} {endpoint}: {requestTime.ElapsedMilliseconds}ms")).ConfigureAwait(false);

                    ParseResponseHeaders(response);

                    return await HandleResponseAsync<T>(response).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                await _client.InternalLogAsync(new LogMessage(LogLevel.Error, exception.ToString()));
                return default;
            }
        }

        private void ParseResponseHeaders(HttpResponseMessage message)
        {
            var headers = message.Headers.ToDictionary(x => x.Key, x => x.Value.FirstOrDefault(),
                StringComparer.OrdinalIgnoreCase);

            headers.TryGetValue(RatelimitLimitHeaderName, out _rateLimit);
            headers.TryGetValue(RatelimitResetHeaderName, out _rateLimitReset);
            headers.TryGetValue(RatelimitRemainingHeaderName, out _remaining);
        }

        private async Task<T> HandleResponseAsync<T>(HttpResponseMessage message)
        {
            var result = await message.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

            switch (message.StatusCode)
            {
                case (HttpStatusCode)200:
                    break;

                case (HttpStatusCode)400:
                    //missing parameter
                    break;

                case (HttpStatusCode)401:
                    //no valid access token
                    break;

                case (HttpStatusCode)403:
                    //access token not valid for context
                    break;

                case (HttpStatusCode)404:
                    //requested item does not exist
                    break;

                case (HttpStatusCode)429:
                    //ratelimit
                    break;

                default:
                    //internal server error
                    break;
            }
            _semaphore.Release();
            return _serializer.ReadUtf8<T>(new ReadOnlySpan<byte>(result));
        }
    }
}