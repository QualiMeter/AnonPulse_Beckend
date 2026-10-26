# Предварительная ИИ-проверка текстовых ответов TEST

Knowledge grading подготавливает предложения по оценке ответов закрытого `TEST` для вопроса
`type: "text"`, `textPurpose: "knowledge"`. Это не итоговая оценка: backend не изменяет
`score`, `gradingState`, баллы попытки, `isFullyGraded`, `normalizedScore` или `isFinal`.

## Условия запуска

Маршруты доступны владельцу теста и администратору. Для запуска тест должен быть закрыт,
вопрос — принадлежать ему и иметь валидные `expectedAnswer`, `gradingCriteria`, `maxScore`, а
также хотя бы один ответ `pending`. `SURVEY`, feedback-вопросы и открытые тесты отклоняются.
Автоматического запуска при закрытии нет.

Frontend обязан предупредить о внешней обработке и отправить явное подтверждение. Все маршруты
требуют cookie-аутентификацию, не кешируются; POST также требует `X-CSRF-TOKEN`.

## Запуск

```http
POST /api/polls/11111111-1111-1111-1111-111111111111/questions/22222222-2222-2222-2222-222222222222/knowledge-grading
Content-Type: application/json
X-CSRF-TOKEN: ...

{"externalProcessingConfirmed":true}
```

Ответ `202 Accepted`:

```json
{
  "id": "33333333-3333-3333-3333-333333333333",
  "pollId": "11111111-1111-1111-1111-111111111111",
  "questionId": "22222222-2222-2222-2222-222222222222",
  "status": "pending",
  "answerCount": 24,
  "suggestedCount": 0,
  "createdAt": "2026-10-26T08:30:00Z",
  "startedAt": null,
  "completedAt": null,
  "errorCode": null,
  "model": "openai/gpt-oss-20b",
  "algorithmVersion": "knowledge-v1"
}
```

Повторный запуск с тем же снимком возвращает тот же job без нового расхода токенов. Снимок
включает набор ответов, эталон, рубрику, максимум, модель и версию алгоритма.

## Статус

```http
GET /api/polls/{pollId}/questions/{questionId}/knowledge-grading
```

Статусы: `pending`, `processing`, `completed`, `failed`. `suggestedCount` — количество полностью
сохранённых предложений. Безопасный `errorCode` не раскрывает prompt или тело ответа Groq.

Основные коды: `knowledge_grading_disabled`, `external_processing_consent_required`,
`test_not_closed`, `poll_not_test`, `question_not_knowledge_text`, `no_pending_answers`,
`knowledge_grading_limit_exceeded`, `provider_timeout`, `provider_unavailable`,
`free_quota_exhausted`, `provider_token_limit_exceeded`, `invalid_provider_response`,
`worker_interrupted`. Для временных ошибок есть ограниченные retries. `429` учитывает и возвращает
`Retry-After`.

Пример `409 Conflict`:

```json
{
  "status": 409,
  "title": "Knowledge grading can start only after the test is closed.",
  "code": "test_not_closed"
}
```

При превышении лимита возвращается `413` с `knowledge_grading_limit_exceeded`. Ответы и рубрика
не обрезаются скрыто.

## Предварительные оценки

```http
GET /api/polls/{pollId}/questions/{questionId}/knowledge-grading/results?page=1&pageSize=20
```

`pageSize` — 1–100. До завершения job маршрут отвечает `409` и
`knowledge_grading_not_completed`.

```json
{
  "jobId": "33333333-3333-3333-3333-333333333333",
  "model": "openai/gpt-oss-20b",
  "algorithmVersion": "knowledge-v1",
  "items": [{
    "answerId": "44444444-4444-4444-4444-444444444444",
    "suggestedScore": 4,
    "maxScore": 5,
    "explanation": "Идея общего интерфейса раскрыта, но пример отсутствует.",
    "correctPoints": ["Описан общий интерфейс"],
    "missingOrIncorrectPoints": ["Нет конкретного примера"],
    "needsReview": false,
    "reviewReasons": [],
    "model": "openai/gpt-oss-20b",
    "algorithmVersion": "knowledge-v1"
  }],
  "page": 1,
  "pageSize": 20,
  "totalCount": 24,
  "totalPages": 2
}
```

`needsReview: true` означает неоднозначность; причины находятся в `reviewReasons`. Это не процент
уверенности и не автоматический ноль. ФИО и группа не дублируются в ИИ-таблице. Интерфейс может
сопоставить ответ с защищёнными данными через существующий список попыток. Публичного маршрута
оценок нет.

## Очередь, ошибки и бюджет

До очереди PostgreSQL сохраняет минимальный неизменяемый снимок. `FOR UPDATE SKIP LOCKED`,
уникальный fingerprint, recovery зависших jobs, ограничение попыток и идемпотентная запись
защищают от конкуренции и рестартов. Неизвестные/повторные temporary ID, неполный результат,
пустое объяснение или балл вне диапазона завершают job ошибкой и не меняют ответ студента.

Все ответы вопроса проверяются пакетом. Перед job резервируется консервативный input/output
token budget с учётом prompt и JSON Schema. Общий HTTP pool ограничивает одновременные Groq
вызовы feedback-analysis и knowledge grading.

## Railway и включение

Функция по умолчанию выключена. Безопасный порядок:

1. Отдельно применить миграцию `20261026081034_AddKnowledgeGrading`.
2. Развернуть с `Ai__KnowledgeGradingEnabled=false`.
3. Проверить `Ai__Enabled=true`, `Ai__Provider=Groq`, `Ai__Groq__ApiKey` и мониторинг.
4. Информировать участников и проверить правовые основания внешней обработки.
5. Затем установить `Ai__KnowledgeGradingEnabled=true`.

Лимиты: `Ai__KnowledgeMaxAnswers`, `Ai__KnowledgeMaxTotalCharacters`,
`Ai__KnowledgeMaxInputTokens`, `Ai__KnowledgeMaxOutputTokens`,
`Ai__KnowledgeMaxReservedTokens`. `Ai__KnowledgeGradingModel` фиксируется как
`openai/gpt-oss-20b`; `reasoning_effort` всегда
`low`, `include_reasoning=false`.

## Приватность

Groq получает эталон, рубрику, максимум, очищенные тексты и случайные temporary ID. UUID ответов
и попыток, ФИО, группа, access secret, client request ID, cookie и IP не передаются. Email и
телефоны удаляет `PersonalDataSanitizer`, но он не гарантирует удаления любых персональных
сведений из свободного текста. Организация должна информировать участников и выполнять
применимые требования к внешней и трансграничной обработке.

См. [ручной сценарий проверки модели](manual-knowledge-grading-evaluation.md).
