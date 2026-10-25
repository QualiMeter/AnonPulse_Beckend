# Open text questions

AnonPulse supports `single_choice`, `multiple_choice`, and `text` questions. The persisted `question_type` is the source of truth. The legacy `allowsMultipleAnswers` field remains accepted and returned; a database check keeps it consistent with the explicit type.

For `text`, `textPurpose` is required: `feedback` or `knowledge`. A knowledge question additionally requires `knowledgeSettings` with non-empty `expectedAnswer` and `gradingCriteria` (up to 4000 characters each) and `maxScore` from 1 through 100. These settings are returned only by authenticated teacher/admin poll APIs and are never included in `GET /api/public/polls/{id}` or results.

Text answers are stored in `poll_text_answers(id, question_id, text)`. There is intentionally no user, participation, token, session, IP, grouping identifier, or timestamp. This prevents database-level reconstruction of one participant's mixed answer set, but arbitrary text may itself contain identifying information, so absolute anonymity is not promised.

`POST /api/public/polls/{id}/votes` accepts `optionId` for choice questions and `text` for a text question. Every question must be answered. Text is trimmed and limited by `TextAnswers:MaximumLength` (default and storage maximum 4000). `TextAnswers:MaximumRequestBodySize` is enforced before MVC model binding (default 262144 bytes). The entire submission and its single participation record commit in the existing poll-row-locked PostgreSQL transaction. Duplicate participation remains `409`.

After a poll is closed, its owner or an active administrator can read a single text question through `GET /api/polls/{pollId}/questions/{questionId}/text-answers?page=1&pageSize=20`. Results are ordered by answer UUID and contain only answer ID and text. Before closure the endpoint returns `409`; foreign resources are hidden as `404`.

Poll results expose `type`, `textPurpose`, and `textAnswerCount`; text question `options` is empty and raw text is never included. Administrative deletion removes text answers and private settings before questions, in the same transaction. Audit metadata records only the number of deleted text answers.

## Deployment

Apply `20261024163743_AddOpenTextQuestions` before deploying code that writes question types. The migration adds `question_type` as nullable, backfills it from `allows_multiple_answers`, makes it non-null, creates the two new tables and restrictive foreign keys, then adds consistency checks. Compatibility triggers derive the type only when a legacy insert omits it or a legacy update changes only `allows_multiple_answers`; explicitly supplied contradictory values are rejected rather than corrected. This keeps the PR #16 backend writable while the migration is applied. Back up and restore-test PostgreSQL first; apply only `scripts/migrations/20261024163743_add_open_text_questions.sql`; verify history, counts, IDs/FKs, both legacy type mappings, and legacy writes; deploy the new backend; smoke-test mixed creation/voting/results. No production migration is run automatically.

The separate knowledge settings table and ungrouped answer IDs are the foundation for later asynchronous AI analysis. This change does not add AI jobs, providers, grading, or external data transfer.
