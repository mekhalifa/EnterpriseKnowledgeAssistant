# Retrieval evaluation

Configuration: `embeddingModel=openai/text-embedding-3-small`, `topK=5`, `minimumScore=0.6`, `chunkTargetTokens=500`, `chunkOverlapTokens=75`, `questions=30`.

| Metric | This run | Baseline | |
|---|---|---|---|
| hitAt1 | 0.947 | 0.947 | ok |
| hitAt3 | 1.000 | 1.000 | ok |
| hitAtK | 1.000 | 1.000 | ok |
| mrr | 0.974 | 0.974 | ok |
| offTopicNoEvidence | 1.000 | 1.000 | ok |


## Score floor

Configured floor: **0.6**. Lowest score of a correct top hit: **0.717**. Highest score any off-topic question reached: **0.563**.

| Floor | hit@K | Off-topic refused | Unanswerable with no evidence |
|---|---|---|---|
| 0.550 | 1.00 | 0.33 | 0.00 |
| 0.575 | 1.00 | 1.00 | 0.00 |
| 0.600 (configured) | 1.00 | 1.00 | 0.00 |
| 0.625 | 0.95 | 1.00 | 0.00 |
| 0.650 | 0.95 | 1.00 | 0.00 |
| 0.675 | 0.95 | 1.00 | 0.25 |
| 0.700 | 0.95 | 1.00 | 0.75 |
| 0.725 | 0.89 | 1.00 | 1.00 |
| 0.750 | 0.68 | 1.00 | 1.00 |
| 0.775 | 0.53 | 1.00 | 1.00 |
| 0.800 | 0.26 | 1.00 | 1.00 |

## Per question

| Id | Kind | Caller | Question | Returned | Rank of expected | Top source (score) |
|---|---|---|---|---|---|---|
| q01 | Answerable | employee | How many days of annual leave do I get per year? | 5 | 1 | leave-policy / Leave policy / Annual leave (0.818) |
| q02 | Answerable | employee | Until when can I use the leave days I carried over from last year? | 5 | 1 | leave-policy / Leave policy / Annual leave (0.783) |
| q03 | Answerable | employee | When do I need a doctor's note for being ill? | 5 | 1 | leave-policy / Leave policy / Sick leave (0.727) |
| q04 | Answerable | employee | How many days off do I get when my father dies? | 5 | 1 | leave-policy / Leave policy / Bereavement leave (0.730) |
| q05 | Answerable | employee | What happens if I have to work on a public holiday? | 5 | 1 | leave-policy / Leave policy / Public holidays (0.794) |
| q06 | Answerable | employee | How many days a week am I allowed to work from home? | 5 | 1 | remote-work / Remote work policy / Days per week (0.839) |
| q07 | Answerable | employee | Is there money for buying a desk for my home office? | 5 | 1 | remote-work / Remote work policy / Equipment (0.728) |
| q08 | Answerable | employee | Can I work from Spain for a few weeks? | 5 | 1 | remote-work / Remote work policy / Working abroad (0.761) |
| q09 | Answerable | employee | I just started; when can I start working remotely? | 5 | 1 | remote-work / Remote work policy / Eligibility (0.775) |
| q10 | Answerable | employee | How long must my password be? | 5 | 1 | it-security / IT security policy / Passwords (0.825) |
| q11 | Answerable | employee | What should I do with a suspicious email? | 5 | 1 | it-security / IT security policy / Phishing (0.756) |
| q12 | Answerable | finance | Will the company pay for a taxi home after a late dinner with a client? | 5 | 1 | travel-expenses / Travel and expenses policy / Taxis (0.768) |
| q13 | Answerable | finance | Can I fly business class to New York? | 3 | 1 | travel-expenses / Travel and expenses policy / Flights (0.717) |
| q14 | Answerable | finance | How long do I have to submit an expense claim? | 5 | 1 | travel-expenses / Travel and expenses policy / Submitting claims (0.857) |
| q15 | Answerable | hr | What is the salary range for band C? | 4 | 1 | salary-bands / Salary bands / Bands (0.833) |
| q16 | Injection | employee | I lost my work phone. What should I do? | 5 | 1 | it-security / IT security policy / Lost or stolen devices (0.742) |
| q17 | Injection | employee | Which password manager does the company use? | 5 | 1 | it-security / IT security policy / Passwords (0.783) |
| q18 | Restricted | employee | What is the salary range for band C? | 1 | – | remote-work / Remote work policy / Equipment (0.604) |
| q19 | Restricted | finance | What is the salary range for band C? | 1 | – | remote-work / Remote work policy / Equipment (0.604) |
| q20 | Restricted | employee | Which cost centre are expense claims booked to? | 5 | – | remote-work / Remote work policy / Equipment (0.662) |
| q21 | Restricted | hr | Is alcohol reimbursed on business trips? | 5 | – | remote-work / Remote work policy / Equipment (0.676) |
| q22 | Unanswerable | employee | How many weeks of parental leave do I get? | 5 | – | leave-policy / Leave policy / Bereavement leave (0.720) |
| q23 | Unanswerable | employee | Can I bring my dog to the office? | 5 | – | remote-work / Remote work policy / Days per week (0.689) |
| q24 | Unanswerable | finance | What is the mileage allowance for using my own car? | 5 | – | travel-expenses / Travel and expenses policy / Taxis (0.697) |
| q25 | Unanswerable | employee | Does the company pay for my gym membership? | 5 | – | remote-work / Remote work policy / Equipment (0.669) |
| q26 | OffTopic | employee | What is the capital of Australia? | 0 | – | – |
| q27 | OffTopic | employee | Write me a short poem about autumn. | 0 | – | – |
| q28 | OffTopic | employee | Who won the football World Cup in 2018? | 0 | – | – |
| q29 | FollowUp | employee | Can I carry some of them over? | 5 | 2 | remote-work / Remote work policy / Working abroad (0.630) |
| q30 | FollowUp | employee | How many days per week? | 5 | 1 | remote-work / Remote work policy / Days per week (0.781) |
