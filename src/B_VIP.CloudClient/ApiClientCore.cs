using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using B_VIP.Contracts.Auth;

namespace B_VIP.CloudClient
{
    public sealed class ApiClientOptions
    {
        public string BaseUrl { get; set; } = "https://b-vip.onrender.com";

        public string? ClientVersion { get; set; }

        public JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
    }

    /// <summary>Thrown when the backend answers with a failure payload.</summary>
    public sealed class ApiException : Exception
    {
        public ApiException(HttpStatusCode statusCode, string code, string message, string? correlationId, string? body)
            : base(message)
        {
            StatusCode = statusCode;
            Code = code;
            CorrelationId = correlationId;
            Body = body;
        }

        public HttpStatusCode StatusCode { get; }

        public string Code { get; }

        public string? CorrelationId { get; }

        public string? Body { get; }

        public bool IsUnauthorized => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    }

    /// <summary>Persists the session tokens between application restarts.</summary>
    public interface ITokenStore
    {
        Task<AuthTokenDto?> LoadAsync(CancellationToken ct = default);

        Task SaveAsync(AuthTokenDto tokens, CancellationToken ct = default);

        Task ClearAsync(CancellationToken ct = default);
    }

    /// <summary>Keeps tokens only in memory; used when no store is supplied.</summary>
    public sealed class InMemoryTokenStore : ITokenStore
    {
        private AuthTokenDto? _tokens;

        public Task<AuthTokenDto?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_tokens);

        public Task SaveAsync(AuthTokenDto tokens, CancellationToken ct = default)
        {
            _tokens = tokens;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            _tokens = null;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Typed client for the B_VIP backend. It sends the bearer token, refreshes it once on a 401
    /// and surfaces backend errors as <see cref="ApiException"/> with the server's error code.
    /// </summary>
    public partial class BVipApiClient
    {
        private readonly HttpClient _http;
        private readonly ITokenStore _store;
        private readonly SemaphoreSlim _refreshGate = new(1, 1);
        private AuthTokenDto? _tokens;

        public BVipApiClient(HttpClient http, ApiClientOptions? options = null, ITokenStore? tokenStore = null)
        {
            _http = http;
            Options = options ?? new ApiClientOptions();
            _store = tokenStore ?? new InMemoryTokenStore();
            if (_http.BaseAddress is null)
            {
                _http.BaseAddress = new Uri(Options.BaseUrl.TrimEnd('/') + "/");
            }
        }

        public ApiClientOptions Options { get; }

        public event Func<AuthTokenDto, Task>? TokensChanged;

        public event Func<ApiException, Task>? RequestFailed;

        public bool IsAuthenticated => _tokens is not null;

        public AuthTokenDto? CurrentTokens => _tokens;

        public async Task RestoreSessionAsync(CancellationToken ct = default)
        {
            AuthTokenDto? tokens = await _store.LoadAsync(ct);
            if (tokens is not null)
            {
                _tokens = tokens;
            }
        }

        public async Task SetTokensAsync(AuthTokenDto tokens, CancellationToken ct = default)
        {
            _tokens = tokens;
            await _store.SaveAsync(tokens, ct);
            if (TokensChanged is not null)
            {
                await TokensChanged(tokens);
            }
        }

        public async Task ClearSessionAsync(CancellationToken ct = default)
        {
            _tokens = null;
            await _store.ClearAsync(ct);
            if (TokensChanged is not null)
            {
                await TokensChanged(new AuthTokenDto(string.Empty, string.Empty, DateTime.MinValue, DateTime.MinValue));
            }
        }

        private async Task<T> SendAsync<T>(HttpRequestMessage request, bool allowRefresh, CancellationToken ct)
        {
            HttpResponseMessage response = await SendCoreAsync(request, allowRefresh, ct);
            return await ReadAsync<T>(response, ct);
        }

        private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, bool allowRefresh, CancellationToken ct)
        {
            await AttachTokenAsync(request, ct);
            HttpResponseMessage response = await _http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized && allowRefresh && _tokens is not null)
            {
                bool refreshed = await TryRefreshAsync(ct);
                if (refreshed)
                {
                    response.Dispose();
                    HttpRequestMessage retry = await CloneAsync(request, ct);
                    await AttachTokenAsync(retry, ct);
                    response = await _http.SendAsync(retry, ct);
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                ApiException error = await ToApiExceptionAsync(response, ct);
                if (RequestFailed is not null)
                {
                    await RequestFailed(error);
                }

                throw error;
            }

            return response;
        }

        private async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpRequestMessage clone = new(request.Method, request.RequestUri);
            if (request.Content is not null)
            {
                byte[] body = await request.Content.ReadAsByteArrayAsync(ct);
                clone.Content = new ByteArrayContent(body);
                if (request.Content.Headers.ContentType is not null)
                {
                    clone.Content.Headers.ContentType = request.Content.Headers.ContentType;
                }
            }

            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }

        private async Task AttachTokenAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (_tokens is null)
            {
                _tokens = await _store.LoadAsync(ct);
            }

            if (!string.IsNullOrEmpty(_tokens?.AccessToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.AccessToken);
            }
        }

        private async Task<bool> TryRefreshAsync(CancellationToken ct)
        {
            await _refreshGate.WaitAsync(ct);
            try
            {
                if (_tokens is null || string.IsNullOrEmpty(_tokens.RefreshToken))
                {
                    return false;
                }

                HttpRequestMessage request = new(HttpMethod.Post, "api/auth/refresh")
                {
                    Content = JsonContent(new RefreshRequest(_tokens.RefreshToken))
                };

                HttpResponseMessage response = await _http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    await ClearSessionAsync(ct);
                    return false;
                }

                AuthTokenDto tokens = await ReadAsync<AuthTokenDto>(response, ct);
                await SetTokensAsync(tokens, ct);
                return true;
            }
            catch (ApiException)
            {
                return false;
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        private static async Task<ApiException> ToApiExceptionAsync(HttpResponseMessage response, CancellationToken ct)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            string code = ((int)response.StatusCode).ToString();
            string message = $"The server returned {(int)response.StatusCode} {response.ReasonPhrase}.";
            string? correlationId = response.Headers.TryGetValues("X-Correlation-Id", out IEnumerable<string>? values)
                ? values.FirstOrDefault()
                : null;

            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("error", out JsonElement error))
                {
                    if (error.TryGetProperty("code", out JsonElement codeElement))
                    {
                        code = codeElement.GetString() ?? code;
                    }

                    if (error.TryGetProperty("message", out JsonElement messageElement))
                    {
                        message = messageElement.GetString() ?? message;
                    }

                    if (error.TryGetProperty("correlationId", out JsonElement correlationElement))
                    {
                        correlationId = correlationElement.GetString() ?? correlationId;
                    }
                }
            }
            catch (JsonException)
            {
                // Not a JSON payload: keep the HTTP level message.
            }

            return new ApiException(response.StatusCode, code, message, correlationId, body);
        }

        private async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
        {
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return default!;
            }

            byte[] payload = await response.Content.ReadAsByteArrayAsync(ct);
            if (payload.Length == 0)
            {
                return default!;
            }

            string json = Encoding.UTF8.GetString(payload);
            T? value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return value is null ? default! : value;
        }

        internal StringContent JsonContent<T>(T value)
            => new(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");

        internal JsonSerializerOptions JsonOptions => Options.Json;

        internal string Url(string path) => path.TrimStart('/');

        internal async Task<T> GetAsync<T>(string path, CancellationToken ct)
            => await SendAsync<T>(new HttpRequestMessage(HttpMethod.Get, Url(path)), true, ct);

        internal async Task<TResponse> PostAsync<TResponse>(string path, object? body, CancellationToken ct)
        {
            HttpRequestMessage request = new(HttpMethod.Post, Url(path));
            if (body is not null)
            {
                request.Content = JsonContent(body);
            }

            return await SendAsync<TResponse>(request, true, ct);
        }

        internal async Task<TResponse> PatchAsync<TResponse>(string path, object? body, CancellationToken ct)
        {
            HttpRequestMessage request = new(HttpMethod.Patch, Url(path));
            if (body is not null)
            {
                request.Content = JsonContent(body);
            }

            return await SendAsync<TResponse>(request, true, ct);
        }

        internal async Task<TResponse> PutAsync<TResponse>(string path, object? body, CancellationToken ct)
        {
            HttpRequestMessage request = new(HttpMethod.Put, Url(path));
            if (body is not null)
            {
                request.Content = JsonContent(body);
            }

            return await SendAsync<TResponse>(request, true, ct);
        }

        internal async Task DeleteAsync(string path, CancellationToken ct)
        {
            HttpRequestMessage request = new(HttpMethod.Delete, Url(path));
            HttpResponseMessage response = await SendCoreAsync(request, true, ct);
            response.Dispose();
        }

        internal async Task<TResponse> DeleteAsync<TResponse>(string path, CancellationToken ct)
        {
            HttpRequestMessage request = new(HttpMethod.Delete, Url(path));
            return await SendAsync<TResponse>(request, true, ct);
        }
    }
}
