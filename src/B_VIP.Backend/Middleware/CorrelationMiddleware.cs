namespace B_VIP.Backend.Middleware
{
    /// <summary>Assigns every request a correlation id used across logs, audit records and errors.</summary>
    public sealed class CorrelationMiddleware
    {
        public const string HeaderName = "X-Correlation-ID";

        private readonly RequestDelegate _next;
        private readonly ILogger<CorrelationMiddleware> _logger;

        public CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            string correlationId = context.Request.Headers.TryGetValue(HeaderName, out var header) &&
                                   !string.IsNullOrWhiteSpace(header)
                ? header.ToString()
                : Guid.NewGuid().ToString("N");

            context.Items["B_VIP.CorrelationId"] = correlationId;
            context.Response.Headers[HeaderName] = correlationId;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await _next(context);
            }
            finally
            {
                stopwatch.Stop();
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    _logger.LogInformation("HTTP {Method} {Path} -> {Status} in {Elapsed}ms ({CorrelationId})",
                        context.Request.Method,
                        context.Request.Path.Value,
                        context.Response.StatusCode,
                        stopwatch.ElapsedMilliseconds,
                        correlationId);
                }
            }
        }
    }
}
