using System.Security.Cryptography;
using System.Text;
using EKA.Application.Abstractions.AI;
using EKA.Application.Common;
using EKA.Domain.Common;

namespace EKA.IntegrationTests.Api;

/// <summary>
/// The embedding model's stand-in for end-to-end tests: the same text always yields the same
/// small unit vector, and one chosen input can be refused once, as a provider refuses input
/// it will not embed.
/// </summary>
internal sealed class DeterministicEmbeddingService : IEmbeddingService
{
    public const int VectorSize = 8;

    private readonly Lock _gate = new();
    private string? _rejectOnce;
    private int _calls;

    public int Dimensions => VectorSize;

    public string Model => "deterministic-test-embedding";

    public int Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls;
            }
        }
    }

    /// <summary>The next batch containing <paramref name="marker"/> fails with <see cref="AiErrors.Rejected"/>.</summary>
    public void RejectOnceWhenInputContains(string marker)
    {
        lock (_gate)
        {
            _rejectOnce = marker;
        }
    }

    public Task<Result<EmbeddingBatch>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _calls++;

            if (_rejectOnce is { } marker && inputs.Any(input => input.Contains(marker, StringComparison.Ordinal)))
            {
                _rejectOnce = null;
                return Task.FromResult<Result<EmbeddingBatch>>(AiErrors.Rejected);
            }
        }

        var vectors = inputs.Select(Vector).ToList();
        return Task.FromResult<Result<EmbeddingBatch>>(new EmbeddingBatch(vectors, Model, new TokenCount(inputs.Count)));
    }

    private static ReadOnlyMemory<float> Vector(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var vector = Enumerable.Range(0, VectorSize).Select(i => hash[i] + 1f).ToArray();
        var length = MathF.Sqrt(vector.Sum(v => v * v));

        return vector.Select(v => v / length).ToArray();
    }
}
