namespace B_VIP.Core.Common
{
    /// <summary>Uniform result wrapper used across the application layers.</summary>
    public readonly record struct Result(bool IsSuccess, string? ErrorCode, string? ErrorMessage)
    {
        public static Result Success() => new(true, null, null);

        public static Result Failure(string code, string message) => new(false, code, message);

        public override string ToString() => IsSuccess ? "Success" : $"Failure({ErrorCode}): {ErrorMessage}";
    }

    public readonly record struct Result<T>(bool IsSuccess, T? Value, string? ErrorCode, string? ErrorMessage)
    {
        public static Result<T> Success(T value) => new(true, value, null, null);

        public static Result<T> Failure(string code, string message) => new(false, default, code, message);

        public Result<T> Ensure(Func<T, bool> predicate, string code, string message)
            => IsSuccess && !predicate(Value!)
                ? Failure(code, message)
                : this;
    }

    /// <summary>Page of items returned by list endpoints.</summary>
    public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
    {
        public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

        public static PagedResult<T> Empty(int page, int pageSize) => new(Array.Empty<T>(), 0, page, pageSize);
    }

    /// <summary>Raised by domain rules; converted to an HTTP problem response by the backend.</summary>
    public sealed class DomainException : Exception
    {
        public string Code { get; }
        public int StatusCode { get; }

        public DomainException(string code, string message, int statusCode = 400)
            : base(message)
        {
            Code = code;
            StatusCode = statusCode;
        }
    }

    public static class Paging
    {
        public const int DefaultPageSize = 25;
        public const int MaxPageSize = 200;

        public static int NormalizePage(int page) => page < 1 ? 1 : page;

        public static int NormalizePageSize(int pageSize)
        {
            if (pageSize <= 0) return DefaultPageSize;
            return pageSize > MaxPageSize ? MaxPageSize : pageSize;
        }
    }
}
