using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace ThgOptimization.Infrastructure;

/// <summary>
/// Resiliência das chamadas de rede (Polly): cada servidor (amd.com, nvidia.com, api.github.com...) tem o seu
/// circuit breaker e todas as requisições GET/HEAD passam por uma política de nova tentativa.
/// - Nova tentativa: até 3 vezes, espera exponencial com variação (1 s, 2 s, 4 s ± jitter), em falha de conexão,
///   408, 429 e 5xx. Respeita o Retry-After do servidor quando vem.
/// - Circuit breaker: se metade das requisições a um servidor falhar (mínimo 4 em 60 s), para de tentar por 30 s e
///   falha na hora com uma mensagem clara, em vez de deixar o usuário esperando timeouts seguidos.
/// Cancelamento pelo usuário ou pelo timeout do HttpClient nunca é repetido. POST/PUT não são repetidos (não são idempotentes).
/// Só cobre até a chegada dos cabeçalhos: um download que cai no meio do corpo não é retomado aqui.
/// </summary>
public sealed class ResilientHttpHandler : DelegatingHandler
{
    private static readonly ConcurrentDictionary<string, ResiliencePipeline<HttpResponseMessage>> PerHost = new(StringComparer.OrdinalIgnoreCase);

    public ResilientHttpHandler() : base(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { }

    /// <summary>HttpClient já com a resiliência (use no lugar de "new HttpClient").</summary>
    public static HttpClient CreateClient(TimeSpan timeout) => new(new ResilientHttpHandler()) { Timeout = timeout };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head || request.RequestUri is null)
            return await base.SendAsync(request, ct);
        var pipeline = PerHost.GetOrAdd(request.RequestUri.Host, _ => Build());
        try
        {
            return await pipeline.ExecuteAsync(async token => await base.SendAsync(request, token), ct);
        }
        catch (BrokenCircuitException)
        {
            throw new HttpRequestException($"O servidor {request.RequestUri.Host} falhou várias vezes seguidas; nova tentativa em até 30 s.");
        }
    }

    private static bool Transient(HttpStatusCode s) => s is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)s >= 500;

    private static ResiliencePipeline<HttpResponseMessage> Build()
    {
        var handle = new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .Handle<TimeoutException>()
            .HandleResult(r => Transient(r.StatusCode));
        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = handle,
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                DelayGenerator = a => new ValueTask<TimeSpan?>(a.Outcome.Result?.Headers.RetryAfter?.Delta is TimeSpan d && d < TimeSpan.FromSeconds(30) ? d : null),
                OnRetry = a => { a.Outcome.Result?.Dispose(); return default; }, // libera a conexão da resposta descartada
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = handle,
                FailureRatio = 0.5,
                MinimumThroughput = 4,
                SamplingDuration = TimeSpan.FromSeconds(60),
                BreakDuration = TimeSpan.FromSeconds(30),
            })
            .Build();
    }
}
