using CaAIA.Domain.Enums;

namespace CaAIA.Domain.AI;

/// <summary>Clase de error de proveedor (determina reintento, rotación o escalado).</summary>
public enum AIErrorKind
{
    Unknown = 0,
    Authentication = 1,
    Authorization = 2,
    RateLimited = 3,
    ModelNotFound = 4,
    ContextTooLong = 5,
    Timeout = 6,
    Network = 7,
    Cancelled = 8,
    ProviderInternal = 9,
}

/// <summary>Excepción tipada de proveedor con reintentabilidad explícita.</summary>
public sealed class AIProviderException : Exception
{
    public string ProviderId { get; }
    public AIErrorKind Kind { get; }
    public bool IsRetryable { get; }
    public TimeSpan? RetryAfter { get; }

    public AIProviderException(
        string providerId,
        AIErrorKind kind,
        string message,
        bool isRetryable = false,
        TimeSpan? retryAfter = null,
        Exception? inner = null)
        : base(message, inner)
    {
        ProviderId = providerId;
        Kind = kind;
        IsRetryable = isRetryable;
        RetryAfter = retryAfter;
    }
}
