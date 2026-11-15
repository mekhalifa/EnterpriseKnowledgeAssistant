using EKA.Domain.Common;

namespace EKA.Application.Abstractions.AI;

/// <summary>
/// How many tokens a piece of text will cost when sent to the model.
/// </summary>
/// <remarks>
/// A port, so a real tokenizer can replace the estimate without touching the prompt
/// builder. Exactness is not the contract; <b>never under-counting</b> is. An
/// over-estimate trims history slightly early. An under-estimate overflows the context
/// window, and the provider rejects the whole request.
/// </remarks>
public interface ITokenCounter
{
    TokenCount Count(string text);
}

/// <summary>
/// The conservative estimate: one token per four characters, rounded up.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not a real tokenizer.</b> The chat model is a configuration value behind
/// OpenRouter (ADR-0015). An exact OpenAI tokenizer is wrong for Claude or Llama, so it
/// would be exactly right for one model and silently wrong for the rest. Four characters
/// per token is close to how English text tokenizes on current BPE vocabularies. Used
/// together with the exact output counts providers report for assistant messages, it
/// keeps the budget honest without a package or tokenizer data files.
/// </para>
/// <para>
/// <b>Where it is weak.</b> Code, URLs and non-Latin scripts tokenize denser than four
/// characters per token, so the estimate <i>under</i>-counts them. The budget keeps
/// headroom below the model's real context window for this reason (see
/// <c>ChatOptions.MaxPromptTokens</c>). Step 11 measures whether that headroom is enough.
/// </para>
/// </remarks>
public sealed class EstimatingTokenCounter : ITokenCounter
{
    private const int CharactersPerToken = 4;

    public TokenCount Count(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return new TokenCount((text.Length + CharactersPerToken - 1) / CharactersPerToken);
    }
}
