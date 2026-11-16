# Step 11: Evaluation

**Status:** ✅ Complete
**Date:** 2026-11-16
**Branch:** `step-11-evaluation` (branched from `step-10-grounded-answers`)
**Result:** 632 tests (630 run on every `dotnet test`; the 2 billed evaluation tests are opt-in),
zero warnings. Baselines recorded:
[retrieval](../evaluation/retrieval.md), [answers](../evaluation/answers.md).

> *A baseline report exists and regressions fail the build.*

Until now "it works" meant the tests pass and a few questions on a real stack looked right.
This step makes quality a number with a history. A fixed corpus and question set run through
the real system. Retrieval is scored on every build for free, answers are scored on demand
with a calibrated judge, and the build fails when either gets worse than the checked-in
baseline. Building it found three defects that had nothing to do with evaluation, and one in
the evaluation itself.

---

## 1. What was built

| Piece | Where | Role |
|---|---|---|
| Corpus: 5 policies, `corpus.json` | `tests/EKA.Evaluation/Corpus` | Two restricted (`group:finance`, `group:hr`), one with a planted instruction; canary strings. |
| 30 questions, six kinds | `tests/EKA.Evaluation/Questions` | answerable 15, injection 2, restricted 4, unanswerable 4, off-topic 3, follow-up 2. |
| `EvaluationHost` | `Harness/` | The real handlers (extract → index → retrieve → ask) over in-memory stores; the shipped prompt and configuration. |
| `CachedEmbeddingService` | `Harness/` | Real embeddings recorded once, replayed; a miss fails unless `EKA_EVAL_RECORD=1`. |
| `RetrievalEvaluation`, `AnswerEvaluation` | `Harness/` | Metrics, hard rules, reports, floor sweep. |
| `GroundednessJudge` (prompt v2, majority vote) | `Harness/` | LLM-as-judge for groundedness only. |
| `Baseline` | `Harness/`, `Baselines/*.json` | Comparison with per-tier tolerance; reports to `docs/evaluation/`. |
| `RetrievalEvaluationTests` | tier 1 | Every `dotnet test`. Free, deterministic, tolerance 0. |
| `AnswerEvaluationTests` | tier 2 (`Category=Evaluation`) | `EKA_EVAL_ANSWERS=1`. Billed (about 5 cents), tolerance 0.10, plus judge calibration. |
| `Ai:Temperature` (0), `Ai:MaxOutputTokens` (1,000) | `EKA.AI` | Production answer settings, also what evaluation measures (§5.2). |
| `AiErrors.QuotaExceeded` (402) | Application + `ProviderFailure` | Out of credit is no longer "rejected" (§5.3). |
| `Message.PromptTemplateVersion` + migration `AnswerPromptVersion` | Domain, Infrastructure | Every stored answer says which prompt produced it (open since Step 6). |
| `AddTextExtraction()` | `EKA.Infrastructure` | The extractor chain, public without opening the extractors. |

How to run it:

```powershell
dotnet test                                            # tier 1 runs with everything else
$env:EKA_EVAL_ANSWERS="1"; dotnet test tests/EKA.Evaluation --filter Category=Evaluation   # tier 2, billed
$env:EKA_EVAL_RECORD="1"            # after changing corpus/questions/chunker/model: re-record embeddings
$env:EKA_EVAL_UPDATE_BASELINE="1"   # deliberately accept new numbers; review the diff
```

---

## 2. Two tiers, because a model is billed and not deterministic

**Tier 1, retrieval, free.** Whether "doctor's note" lands near "medical certificate" is a
property of the real embedding model, so a fake vector cannot measure it. But calling the
model on every build costs money and needs a key. **Recorded embeddings** resolve both: the
real vectors, keyed by SHA-256 of model and exact input text, replayed on every run. Changing
anything that changes the inputs causes a miss, and a miss **fails** instead of quietly
calling the network. A silent fallback would have turned a free, deterministic gate into a
billed, flaky one without anyone noticing.

**Tier 2, answers, billed.** Real answers from the shipped prompt at the shipped temperature,
through the real `AskQuestionHandler`, scored three ways that need no judge (answer rate,
citation correctness, refusal correctness) and one that does (groundedness).

**Why the real handlers matter.** The corpus is ingested by `ExtractDocumentTextHandler` and
`IndexDocumentHandler`, questions go through `ChunkRetriever` and `AskQuestionHandler`, and the
prompt is the v2 text the migration seeds (made public for this, and left in the migration,
because migrations are snapshots). An evaluation of a re-implementation measures the
re-implementation.

---

## 3. The gate

| Rule | Tier | Fails on |
|---|---|---|
| Retrieval returns text the caller may not read | 1 | one case |
| A canary in an answer, or in a restricted question's prompt | 2 | one case |
| A must-refuse question answered with a citation | 2 | one case |
| A restricted document cited | 2 | one case |
| An answer the judge could not grade | 2 | one case |
| hit@1, hit@3, hit@K, MRR, off-topic refusal | 1 | any drop below baseline (tolerance 0) |
| answer rate, citation correctness, refusal correctness, groundedness | 2 | drop > 0.10 below baseline |

**Baseline, not absolute bars.** A bar of 0.9 cannot see 0.98 falling to 0.91. **Improvements do
not move the bar by themselves.** Raising it is `EKA_EVAL_UPDATE_BASELINE=1` and a reviewed diff,
and a run with violations is never accepted as a baseline.

**Proved, not assumed.** `Retrieval__MinimumScore=0.75`, a configuration change with no code
change, failed the ordinary gate on hit@1, hit@3, hit@K and MRR. Restored, it passed.

---

## 4. What the numbers say

**Retrieval** ([report](../evaluation/retrieval.md)): hit@1 0.947, hit@3 1.0, hit@5 1.0, MRR
0.974, off-topic refused 3/3. The one rank-2 hit is q29, "Can I carry some of them over?": a
pronoun follow-up, the weakness ADR-0029 accepted.

**The score floor, finally from data.** Correct top hits score 0.717 or more. The highest any
off-topic question reaches is 0.563. The sweep:

| Floor | hit@K | Off-topic refused | Unanswerable with no evidence |
|---|---|---|---|
| 0.575 | 1.00 | 1.00 | 0.00 |
| **0.600** | **1.00** | **1.00** | 0.00 |
| 0.625 | 0.95 | 1.00 | 0.00 |
| 0.700 | 0.95 | 1.00 | 0.75 |
| 0.750 | 0.68 | 1.00 | 1.00 |

0.6 stays (ADR-0028 called it a guess; it is now a measurement). The last column is the
lesson: **retrieval cannot tell "related" from "covered".** Unanswerable questions about parental
leave or dogs find related chunks at 0.67–0.72, which is why the model's refusal layer
(ADR-0029) exists.

**Answers** ([report](../evaluation/answers.md)): 1.0 on all four metrics. Every restricted,
unanswerable and off-topic question was refused, with no canary anywhere. Both injection
questions were answered normally. Step 10's taxi question now gets the conditional answer.

**Read the 1.0s critically.** A perfect score on 30 questions says nothing is broken. It does not
say the system is excellent, and it leaves no headroom to show an improvement. Harder questions
(multi-step, numeric, conflicting sources) are the next lever, recorded in PROGRESS.

---

## 5. What went wrong

### 5.1 The harness reported a perfect score it never measured

The first answer run reported **groundedness 1.000**. Every judge call had failed, the failures
were excluded as "unparseable", and the mean of an empty list was taken as 1. The report even
said "19 unparseable", in a line I would have skipped if I had not read the per-question table.

**Fix:** a metric with nothing to measure scores 0, and an answer the judge could not grade is a
hard violation. **Lesson:** an evaluation must fail when its instrument fails. And read the
per-question table before believing the summary, especially when it is perfect.

### 5.2 No output cap, so every request reserved 16k tokens of credit

The judge calls failed with HTTP 402. OpenRouter reserves credit for the request's `max_tokens`,
and we never set one, so every call asked for the model's maximum (16,384 for gpt-4o). A
nearly spent budget refused requests it could easily afford. Production had the same gap, plus
no ceiling on a runaway answer. **Fix:** `Ai:MaxOutputTokens` (1,000 shipped; 400 for the judge),
validated, with a test that it reaches the wire. `Ai:Temperature` (0 shipped) was added for the
same reason: the evaluation must measure the configuration that answers users.

### 5.3 Running out of credit would have failed documents permanently

402 fell through to `ai.rejected`, and `IndexDocumentHandler` treats a rejection as permanent.
Every document being embedded when the budget ran out would have been marked **Failed** for
good, instead of resuming after a top-up. **Fix:** `AiErrors.QuotaExceeded`, not retried within a
request, transient for indexing, both tested. Later in this step the budget ran out for real,
and the failure read `ai.quota_exceeded: … usage limit … reached`: the fix, validated live.

### 5.4 The judge passed the over-reach it exists to catch

Before trusting the judge, I gave it three known answers. Judge prompt **v1** graded Step 10's
real over-reach, *"Yes, you can take a taxi home after a late meeting, as taxis are reimbursed
22:00–06:00…"*, as **SUPPORTED**. It saw the condition restated and missed the unconditional
"yes". **Fix:** prompt **v2** makes the judge list claims, the direct answer first, at the strength
the answer states them, before it may give a verdict. It then graded the over-reach PARTIAL:
*"assumes that a late meeting will end between 22:00 and 06:00."* The answer baseline was
re-recorded under v2.

**Then measured, not hoped.** Repeated at temperature 0, judge v2 graded the over-reach PARTIAL in
about four of five samples and SUPPORTED in the fifth. "Supported" and "unsupported" never
wavered. The calibration test now takes a **majority of three votes**, ties going to the stricter
verdict (unit-tested). The three-vote calibration has **not yet passed against the real model**:
the budget ran out while measuring the single-vote flip rate. It is the first thing to run when
the key has credit again.

### 5.5 Small ones

- Tier 2 asks follow-ups' history questions, and tier 1 never embedded them, so the cache missed.
  It failed loudly, as designed. Tier 1 now retrieves for history questions too, so both tiers
  share one input set, and recording either keeps the cache whole.
- A recording run compared before saving and failed against its own missing baseline. It now
  saves first.
- `DocumentAccess` is both a domain class and the Step 9 migration's class name, so the harness
  imports only the migration it needs.

---

## 6. LLM-as-judge: what it is good for, and its limits

- **Use it only where exactness is impossible.** Refusal, citation, leakage and injection are
  checked exactly. Only "does this source support this sentence" goes to the judge.
- **Different, stronger model** (gpt-4o judging gpt-4o-mini), because a model tends to approve of
  its own style.
- **Versioned prompt, temperature 0, capped output, a reason with every verdict,** so a person can
  audit a sample.
- **Calibrate it** with known good, borderline and bad answers before trusting its numbers, and
  again after every judge-prompt change. v1 would have certified the exact failure the judge was
  added to detect.
- **Expect noise.** Temperature 0 is not determinism, so tier 2 has a tolerance, and the
  calibration votes.

---

## 7. Alternatives, and when each would win

| Choice | Alternative | It wins when |
|---|---|---|
| Two tiers | Console tool only | Reports are enough and no one needs a gate |
| Recorded embeddings | Live embedding in CI | A funded CI key, and drift in the provider's model is itself a concern |
| Exact search in tier 1 | Real Qdrant in tier 1 | The corpus is large enough for HNSW approximation to matter |
| LLM judge for groundedness | Exact checks only | Over-reach is not a risk (it is) |
| LLM judge | Human labels | A release decision on a real corpus |
| Baseline + tolerance | Absolute thresholds | A contractual quality bar exists |
| One judge vote in the full run | Three votes | The budget allows three times the judge cost |

---

## 8. What to take away from Step 11

1. **Make the free part deterministic and gate every build with it.** Record what is expensive
   and stable (embeddings), and keep what is expensive and noisy (answers) behind an explicit
   switch.
2. **An instrument that fails must fail the measurement.** "Unparseable, excluded" turned into
   "perfect". Empty means zero, not one.
3. **Calibrate the judge before reading its scores.** A judge that never says no and a system that
   never errs produce identical reports.
4. **Evaluations find bugs outside themselves.** The output cap, the 402 classification and the
   permanent-failure risk were production defects, found because the evaluation used the real
   adapters at real limits.
5. **A perfect score is a question, not an answer.** It may mean the test set is too easy.

---

## Next

**Step 12: Security and enterprise controls.**
- A real identity provider replaces the development scheme.
- Document-level authorization tests over the real token path.
- Who may change access becomes a role.
- Rate limiting and cost controls, building on `MaxOutputTokens`.
- Secret and log hygiene, and a threat model.

The evaluation's hard rules (no leak, no obeyed injection) become part of the security test story.
