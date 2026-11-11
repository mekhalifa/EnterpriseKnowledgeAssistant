# SOLID — with examples from EKA

Five principles for arranging classes so that change stays cheap. Every example below is
code we will actually write in this project.

The honest framing first: SOLID is a set of **heuristics for managing change**, not laws.
Each one has a cost, and applying it where nothing will ever change is waste. The test is
always *"what change am I making cheap, and is that change plausible?"*

---

## S — Single Responsibility Principle

> A class should have one reason to change.

Note the wording: *one reason to change*, not "one method" or "does one thing". Uncle
Bob's own framing is "one actor it answers to."

**The violation we would naturally write in Step 7:**

```csharp
// ✗ Four reasons to change: upload rules, PDF library, chunk tuning, provider API
public sealed class DocumentService
{
    public async Task Process(IFormFile file)
    {
        ValidateFileTypeAndSize(file);
        var text = ExtractTextFromPdf(file);
        var chunks = SplitIntoChunks(text, 1000, 200);
        var vectors = await CallOpenAiEmbeddings(chunks);
        await SaveToQdrant(vectors);
    }
}
```

If the security team changes upload limits, you edit the same file the ML engineer edits
to tune chunk overlap. Merge conflicts, and a test for one concern needs all four
collaborators.

**The split:**

```csharp
public interface IUploadValidator          { Result Validate(FileUpload file); }
public interface ITextExtractor            { Task<ExtractedText> ExtractAsync(Stream s, string contentType, CancellationToken ct); }
public interface IChunkingStrategy         { IReadOnlyList<TextChunk> Chunk(ExtractedText text); }
public interface IEmbeddingGenerator       { Task<IReadOnlyList<float[]>> GenerateAsync(IEnumerable<string> texts, CancellationToken ct); }
public interface IVectorStore              { Task UpsertAsync(IEnumerable<VectorRecord> records, CancellationToken ct); }

// The pipeline composes them and owns no rule of its own.
public sealed class IngestionPipeline(
    IUploadValidator validator,
    ITextExtractor extractor,
    IChunkingStrategy chunker,
    IEmbeddingGenerator embedder,
    IVectorStore vectors) { /* orchestrate */ }
```

Now "supported file types" lives in one file, and chunk tuning in another.

**Where not to apply it:** do not split a 30-line class into five 6-line classes because
the number feels better. Split along *axes of change*, which you discover by asking who
requests the change.

---

## O — Open/Closed Principle

> Open for extension, closed for modification: add behaviour without editing tested code.

**Step 8 will need several chunking algorithms** — fixed-size, sentence-aware,
markdown-heading-aware, and later maybe semantic. The closed-for-modification version:

```csharp
// Adding a new strategy means adding a FILE, not editing a switch statement
public interface IChunkingStrategy
{
    string Name { get; }
    IReadOnlyList<TextChunk> Chunk(ExtractedText text, ChunkingOptions options);
}

public sealed class FixedSizeChunkingStrategy  : IChunkingStrategy { public string Name => "fixed"; … }
public sealed class SentenceChunkingStrategy   : IChunkingStrategy { public string Name => "sentence"; … }
public sealed class MarkdownChunkingStrategy   : IChunkingStrategy { public string Name => "markdown"; … }

// Selected by configuration, resolved from the registered set
public sealed class ChunkingStrategyResolver(IEnumerable<IChunkingStrategy> strategies)
{
    public IChunkingStrategy Resolve(string name) =>
        strategies.FirstOrDefault(s => s.Name == name)
        ?? throw new InvalidOperationException($"Unknown chunking strategy '{name}'.");
}
```

The gain is concrete: in Step 11 we compare chunking configurations in repeatable
evaluation runs. With a `switch`, every experiment edits production code; with strategies,
an experiment is a config value.

**The trap:** OCP via abstraction is speculative. Adding an interface for a *hypothetical*
second implementation is the cost without the benefit. Rule of thumb — write it concrete
the first time, abstract it when the second case actually arrives. Chunking and AI
providers qualify because the README already names multiple variants.

---

## L — Liskov Substitution Principle

> A subtype must be usable anywhere its base type is expected, without surprises.

Violations are about *behaviour*, not compilation. Our live risk is in Step 5, where
`IChatClient` is implemented by OpenAI, Azure OpenAI, and a test fake:

```csharp
// ✗ LSB violation: a "fake" that cannot actually stand in for the real thing
public sealed class FakeChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(…) => throw new NotImplementedException();
}
```

Subtler and more realistic: a provider adapter that **throws** on a system message
because its API lacks one, while the other provider accepts it. The use case now behaves
differently per provider, which is exactly what the abstraction promised to prevent.

The adapter's job is to make the contract true — translate the system message into
whatever the provider supports, or document the limitation in the interface itself.
Our guard is a shared **contract test suite** run against every implementation:

```csharp
public abstract class ChatClientContractTests   // one subclass per implementation
{
    protected abstract IChatClient CreateClient();

    [Fact] public async Task Honours_system_instructions() { … }
    [Fact] public async Task Respects_cancellation() { … }
    [Fact] public async Task Throws_ProviderException_on_transport_failure() { … }
}
```

Also: prefer composition over inheritance. Most LSP violations are inheritance hierarchies
that should have been interfaces plus delegation.

---

## I — Interface Segregation Principle

> No client should be forced to depend on methods it does not use.

**The violation:**

```csharp
// ✗ The read-only retrieval path is forced to depend on write and delete
public interface IDocumentStore
{
    Task<Document?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Document>> SearchAsync(string query, CancellationToken ct);
    Task AddAsync(Document doc, CancellationToken ct);
    Task UpdateAsync(Document doc, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task ReindexAllAsync(CancellationToken ct);
}
```

In Step 9 the retrieval handler needs exactly one method. Depending on this interface
means its test fake must stub six, and a reader cannot tell whether retrieval mutates
data — which, for the component enforcing our ACL rule, is precisely the question a
reviewer should be able to answer at a glance.

**Segregated:**

```csharp
public interface IDocumentReader  { Task<Document?> GetAsync(Guid id, CancellationToken ct); }
public interface IDocumentWriter  { Task AddAsync(Document doc, CancellationToken ct); … }
public interface IDocumentIndexer { Task ReindexAllAsync(CancellationToken ct); }
```

One infrastructure class may implement all three; clients depend only on what they use.
This is also the cheapest honest step toward CQRS: reads and writes get different
contracts without the machinery of separate models.

---

## D — Dependency Inversion Principle

> Depend on abstractions, and let the abstraction be owned by the *policy*, not the detail.

The second half is the part usually missed. It is not enough that
`UploadDocumentHandler` depends on `IDocumentRepository` — the interface must live in
**`EKA.Application`**, the layer that *needs* it, not in `EKA.Infrastructure`, the layer
that provides it. Otherwise `Application` still references `Infrastructure` and the arrow
never inverted.

```
EKA.Application/Abstractions/IDocumentRepository.cs   ← interface declared by the consumer
EKA.Infrastructure/Persistence/SqlDocumentRepository.cs ← implements it, references upward
EKA.Api/Program.cs                                    ← binds the two together
```

Full treatment with diagrams: [clean-architecture.md](clean-architecture.md).

---

## When SOLID becomes the problem

Worth writing down, because over-application is the more common failure in .NET codebases:

| Symptom | What went wrong |
|---|---|
| `IFooMapper`, `IFooFactory`, `IFooValidator`, each with exactly one implementation that will never change | DIP applied past the point of benefit — indirection with no payoff |
| Navigating five files to understand one request | Responsibilities split by line count rather than by axis of change |
| `IRepository<T>` with `IQueryable` leaking everywhere | ISP violated by a "reusable" abstraction that abstracts nothing |
| Interfaces created solely to enable mocking | Often a sign the class should be a pure function or a value object |

The counter-heuristic: **an abstraction must earn its place by naming a real seam** — a
swappable detail, a process boundary, or something that must be faked to test policy. If
it names none of those, delete it and use the concrete type.

## Further reading

- Robert C. Martin, *Agile Software Development: Principles, Patterns, and Practices*
- Mark Seemann, *Dependency Injection Principles, Practices, and Patterns* — the best
  treatment of DIP in .NET specifically
