using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EKA.Application.Abstractions.AI;
using EKA.Application.Common;

namespace EKA.Evaluation.Harness;

/// <summary>
/// Real embeddings, recorded once and replayed: what makes retrieval evaluation free and
/// deterministic on every <c>dotnet test</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why record real vectors rather than fake them.</b> Retrieval quality is a property of the
/// real model's vector space. A stand-in, like the hash vectors of the integration tests, can
/// prove a filter works, and proves nothing about whether "doctor's note" lands near "medical
/// certificate". The cache holds what <c>text-embedding-3-small</c> actually returned.
/// </para>
/// <para>
/// <b>Keyed by model and exact input text</b> (SHA-256 of both). Changing a document, a
/// question, the chunker, the embedding input format or the model changes the inputs, so the
/// cache misses. <b>A miss fails the run</b> unless recording is switched on: a silent fallback
/// to the network would turn a free, deterministic gate into a billed, flaky one without anyone
/// noticing.
/// </para>
/// <para>
/// Recording rewrites the file with only the entries this run used, sorted, so obsolete vectors
/// disappear and the diff shows what changed.
/// </para>
/// </remarks>
internal sealed class CachedEmbeddingService : IEmbeddingService
{
    private readonly Dictionary<string, float[]> _entries;
    private readonly HashSet<string> _used = [];
    private readonly IEmbeddingService? _recorder;
    private readonly Lock _gate = new();

    public CachedEmbeddingService(string model, int dimensions, IEmbeddingService? recorder)
    {
        Model = model;
        Dimensions = dimensions;
        _recorder = recorder;

        var cache = File.Exists(EvaluationSettings.EmbeddingCacheFile)
            ? JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(EvaluationSettings.EmbeddingCacheFile), EvaluationSet.Json)
            : null;

        // A cache recorded with another model or size describes another vector space. Using it
        // would score the wrong system; ignoring it silently would re-bill everything.
        if (cache is not null && (cache.Model != model || cache.Dimensions != dimensions))
        {
            if (recorder is null)
            {
                throw new InvalidOperationException(
                    $"The embedding cache was recorded with {cache.Model} ({cache.Dimensions} dimensions) and the configuration "
                    + $"says {model} ({dimensions}). Re-record it: set EKA_EVAL_RECORD=1 and run the evaluation once.");
            }

            cache = null;
        }

        _entries = cache?.Entries.ToDictionary(entry => entry.Key, entry => Decode(entry.Value)) ?? [];
    }

    public int Dimensions { get; }

    public string Model { get; }

    public int Misses { get; private set; }

    public async Task<Result<EmbeddingBatch>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var keys = inputs.Select(Key).ToList();
        List<int> missing;
        lock (_gate)
        {
            missing = [.. Enumerable.Range(0, inputs.Count).Where(i => !_entries.ContainsKey(keys[i]))];
            Misses += missing.Count;
        }

        if (missing.Count > 0)
        {
            if (_recorder is null)
            {
                throw new EmbeddingCacheMissException(missing.Count, inputs[missing[0]]);
            }

            var recorded = await _recorder.EmbedAsync([.. missing.Select(i => inputs[i])], cancellationToken).ConfigureAwait(false);
            if (recorded.IsFailure)
            {
                return recorded.Error;
            }

            lock (_gate)
            {
                for (var m = 0; m < missing.Count; m++)
                {
                    _entries[keys[missing[m]]] = recorded.Value.Vectors[m].ToArray();
                }
            }
        }

        lock (_gate)
        {
            _used.UnionWith(keys);
            return new EmbeddingBatch([.. keys.Select(key => (ReadOnlyMemory<float>)_entries[key])], Model, InputTokens: null);
        }
    }

    /// <summary>Writes the entries this run used back to the repository. Recording runs only.</summary>
    public void Save()
    {
        lock (_gate)
        {
            var file = new CacheFile(
                Model,
                Dimensions,
                new SortedDictionary<string, string>(
                    _used.ToDictionary(key => key, key => Encode(_entries[key])),
                    StringComparer.Ordinal));

            Directory.CreateDirectory(Path.GetDirectoryName(EvaluationSettings.EmbeddingCacheFile)!);
            File.WriteAllText(EvaluationSettings.EmbeddingCacheFile, JsonSerializer.Serialize(file, EvaluationSet.Json));
        }
    }

    private string Key(string input) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Model + "\n" + input)));

    private static string Encode(float[] vector) => Convert.ToBase64String(MemoryMarshal.AsBytes(vector.AsSpan()));

    private static float[] Decode(string base64) => MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(base64)).ToArray();

    private sealed record CacheFile(string Model, int Dimensions, IDictionary<string, string> Entries);
}

/// <summary>An input the cache has no vector for, in a run that may not call the model.</summary>
internal sealed class EmbeddingCacheMissException(int count, string firstInput)
    : Exception(
        $"{count} embedding input(s) are not in tests/EKA.Evaluation/Cache/embeddings.json, for example: "
        + $"\"{firstInput[..Math.Min(80, firstInput.Length)].ReplaceLineEndings(" ")}…\". The corpus, the questions, the "
        + "chunker or the embedding input format changed. Re-record with a key configured: set EKA_EVAL_RECORD=1 and run "
        + "`dotnet test tests/EKA.Evaluation` once (fractions of a cent), then review and commit the cache and the baseline.");
