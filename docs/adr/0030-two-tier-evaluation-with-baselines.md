# ADR-0030: Two-tier evaluation: recorded embeddings gate every build, a calibrated LLM judge gates billed runs

- **Status:** Accepted
- **Date:** 2026-11-16
- **Deciders:** project owner

## Context

Step 11's exit criterion: *a baseline report exists and regressions fail the build.* The
system's quality depends on a model that is billed, needs a personal key, and is not
deterministic. A gate on every build must therefore be free and repeatable. A gate on answer
quality cannot be either. Step 10 also found the failure that matters most: an answer that
cites the right source and still overstates it. No exact check can see that.

## Decision

- **A fixed evaluation set in the repository:** five synthetic policies (two restricted to a
  group, one with a planted instruction) and 30 questions in six kinds: answerable,
  follow-up, injection, restricted, unanswerable, off-topic. Expected sources are named by
  heading. Canary strings must never appear in an answer or a non-member's prompt.
- **Tier 1, on every `dotnet test`, free:** retrieval through the shipped retriever and
  configuration, over the corpus ingested by the real extraction and indexing handlers, with
  **real embeddings recorded once** into `Cache/embeddings.json` and replayed. A cache miss
  fails the run unless `EKA_EVAL_RECORD=1`. Metrics: hit@1, hit@3, hit@K, MRR, off-topic
  refusal. **Tolerance 0**: the tier is deterministic, so any drop fails. A floor sweep is
  reported alongside.
- **Tier 2, billed and opt-in (`EKA_EVAL_ANSWERS=1`):** real answers through the real
  `AskQuestionHandler` and the shipped v2 prompt, at the shipped temperature (0).
  - Exact metrics: answer rate, citation correctness, refusal correctness.
  - Groundedness by an **LLM judge**: a different, stronger model (gpt-4o), a versioned
    prompt, temperature 0, output capped.
  - **Tolerance 0.10.**
- **Hard rules fail on a single case, in both tiers:** text the caller may not read, retrieved
  or cited; a canary in an answer or in a restricted question's prompt; a must-refuse question
  answered with a citation; an answer the judge could not grade.
- **The judge is calibrated before it is trusted.** A billed test feeds it a supported, an
  over-reaching (Step 10's real taxi answer) and an unsupported answer, and requires the three
  verdicts. Judge prompt v1 failed it, and v2 replaced it. The calibration takes a majority of
  three votes, ties going to the stricter verdict.
- **Baselines are files in the repository,** with the configuration that produced them. They
  change only through `EKA_EVAL_UPDATE_BASELINE=1`, in a reviewed commit, and never from a run
  with a violation. Reports go to `docs/evaluation/` when a baseline is recorded.

## Consequences

### Positive

- Every build guards retrieval quality and access at no cost, with no key and no flakiness.
  Raising the floor to 0.75 through configuration alone failed the gate on four metrics.
- The floor (0.6) is now chosen from data: off-topic questions peak at 0.563, and correct top
  hits start at 0.717.
- The evaluation found three defects outside itself: no output cap; 402 reported as a
  rejection, which would have failed documents permanently; and a judge that passed the
  over-reach.
- A score names its configuration (models, prompt and judge versions, K, floor, chunking),
  so a change in a number can be traced to a change in the system.

### Negative / costs we accept

- **The set is small and synthetic.** 30 questions over five short documents is a smoke
  detector, not a benchmark. One question moves a tier-2 metric by about 0.05–0.25. Its
  perfect scores say that nothing is broken, not that the system is excellent.
- **The baseline has no headroom.** Every metric is 1.0, so an improvement cannot show, and
  only harder questions will tell systems apart.
- **The judge is a model.** Even calibrated, single votes flipped toward leniency about one
  time in five on the borderline case. The full run uses one vote (cost), so it can miss an
  over-reach, though it will not invent one.
- **Recorded embeddings must be re-recorded** whenever the corpus, the questions, the chunker,
  the embedding input format or the model change. The cache is about 430 KB of JSON in the
  repository.
- **Exact search in tier 1** is not Qdrant's approximate search. They agree at this size, and
  will not on a large corpus.
- **Billed runs consume a personal budget.** Building this step exhausted the key's weekly
  limit twice.

## Alternatives considered

- **A console tool only.** It wins when nobody needs a gate, only reports. It loses the "fails
  the build" property unless CI pays for every PR.
- **Everything in `dotnet test`.** It wins with a funded CI key and a tolerant team. It costs
  money on every local run and fails randomly.
- **Exact checks only, no judge.** It wins if over-reach is not a concern. Step 10 showed that
  it is.
- **Human-labelled answers.** It wins for a release decision on a real corpus. It does not
  scale, and cannot gate a build.
- **Absolute thresholds.** Simpler to state. They are blind to a drop that stays above the bar,
  and the bars would be guesses.
- **Same model as judge** (gpt-4o-mini). A tenth of the cost. It wins on a tight budget, at the
  price of self-preference. Recorded in the baseline configuration if ever chosen.

## Revisit when

- A real corpus exists: build a larger set from it, with questions written by people who did
  not read the chunks.
- The tier-2 baseline needs to separate two good systems: add harder questions (multi-hop,
  numeric reasoning, conflicting sources).
- Budget allows: run the full answer evaluation with three judge votes and re-baseline.
- The corpus grows past what exact search can stand in for: run tier 1 against Qdrant.
