# ИИ-анализ обратной связи

Функция доступна только преподавателю-владельцу опроса и администратору. Анализируются
только закрытые опросы и текстовые вопросы с `textPurpose: "feedback"`. Ответы из
`knowledge` этим API не оцениваются.

## Запуск анализа

Перед отправкой запроса интерфейс обязан показать преподавателю предупреждение:

> Тексты отзывов после удаления очевидных email и телефонов будут переданы внешнему
> провайдеру GroqCloud. Очистка не гарантирует полной анонимности: сам текст может
> содержать имена или уникальные сведения.

Подтверждение проверяется backend. Запрос без `externalProcessingConfirmed: true`
отклоняется. Как и другие изменяющие cookie-authenticated запросы, запрос требует
актуальный `X-CSRF-TOKEN`.

```http
POST /api/polls/0f88e652-559b-45df-a462-f8ca2c1a2674/questions/213896e5-6ff7-4eec-aa22-73e9bb52ec98/feedback-analysis
Content-Type: application/json
X-CSRF-TOKEN: <token>

{
  "externalProcessingConfirmed": true
}
```

Успешный ответ имеет статус `202 Accepted`. Для того же отпечатка ответов backend
сохраняет один ID анализа:

- `pending` и `processing` возвращаются без создания дубликата;
- `completed` возвращает сохранённый отчёт и не вызывает модель повторно;
- `failed` атомарно переводится обратно в `pending` после явного повторного POST;
- повторный POST, включая перезапуск `failed`, снова требует
  `externalProcessingConfirmed: true`.

```json
{
  "id": "60593b75-cc0c-45f6-9c10-560f99fcf071",
  "pollId": "0f88e652-559b-45df-a462-f8ca2c1a2674",
  "questionId": "213896e5-6ff7-4eec-aa22-73e9bb52ec98",
  "status": "pending",
  "answerCount": 18,
  "report": null,
  "errorCode": null,
  "createdAt": "2026-10-25T07:30:00Z",
  "startedAt": null,
  "completedAt": null
}
```

## Получение статуса и результата

```http
GET /api/polls/0f88e652-559b-45df-a462-f8ca2c1a2674/questions/213896e5-6ff7-4eec-aa22-73e9bb52ec98/feedback-analysis
```

Опросите endpoint с разумным интервалом (например, 2–5 секунд), пока `status` равен
`pending` или `processing`. Для `completed` покажите отчёт; для `failed` покажите
сообщение по `errorCode` и кнопку «Повторить анализ». По нажатию снова покажите
предупреждение о внешней передаче, получите явное подтверждение и выполните тот же
POST. Не запускайте повтор автоматически. Все ответы endpoint имеют
`Cache-Control: no-store`.

```json
{
  "id": "60593b75-cc0c-45f6-9c10-560f99fcf071",
  "pollId": "0f88e652-559b-45df-a462-f8ca2c1a2674",
  "questionId": "213896e5-6ff7-4eec-aa22-73e9bb52ec98",
  "status": "completed",
  "answerCount": 18,
  "report": {
    "summary": "Студенты ценят понятные объяснения, но просят больше практики.",
    "themes": [
      {
        "name": "Недостаток практики",
        "description": "Несколько студентов хотели бы решать больше задач.",
        "category": "problem",
        "mentionCount": 7
      },
      {
        "name": "Понятные объяснения",
        "description": "Объяснение материала воспринимается положительно.",
        "category": "positive",
        "mentionCount": 5
      }
    ],
    "recommendations": [
      {
        "title": "Добавить практические задания",
        "description": "Добавлять короткий блок задач после каждой темы.",
        "priority": "high"
      }
    ],
    "limitations": [
      "Выводы основаны только на 18 добровольных текстовых ответах."
    ]
  },
  "errorCode": null,
  "createdAt": "2026-10-25T07:30:00Z",
  "startedAt": "2026-10-25T07:30:02Z",
  "completedAt": "2026-10-25T07:30:05Z"
}
```

`mentionCount` вычисляет backend по уникальным временным ID исходных ответов. В API
не возвращаются эти ID, индивидуальные цитаты, ФИО или идентификаторы участников.
Один ответ может учитываться в нескольких темах.

## Ошибки

Ошибки используют `application/problem+json`; стабильный машинный код находится в
`extensions.code` (в JSON это поле `code`).

```json
{
  "type": "about:blank",
  "title": "Feedback can be analyzed only after the poll is closed.",
  "status": 409,
  "code": "poll_not_closed"
}
```

Основные коды:

| HTTP | Код | Действие интерфейса |
| --- | --- | --- |
| 400 | `external_processing_consent_required` | Показать предупреждение и запросить явное подтверждение |
| 400 | `knowledge_analysis_not_supported` | Сообщить, что проверка знаний появится позже |
| 400 | `no_feedback_answers` | Сообщить, что нет непустых отзывов |
| 403 | `poll_access_forbidden` | Не показывать данные анализа |
| 404 | `question_not_found`, `analysis_not_found` | Обновить данные опроса |
| 409 | `poll_not_closed` | Предложить сначала закрыть опрос |
| 429 | `analysis_retry_cooldown` | Отключить кнопку на число секунд из `Retry-After`, затем разрешить ручной повтор |
| 413 | `feedback_limit_exceeded` | Объяснить, что набор превышает лимит бесплатной обработки |
| 503 | `ai_not_configured` | Сообщить, что функция временно недоступна |

После исчерпания бесплатной квоты задача повторяется ограниченное число раз с учётом
`Retry-After`, затем получает `failed` и `errorCode: "free_quota_exhausted"`. Интерфейс
не должен создавать циклические перезапуски и не должен предлагать платный тариф.
Окончательный `failed` сохраняет cooldown не короче `Ai__RetryCooldownSeconds` и не
короче полученного от Groq `Retry-After`. Ручной POST внутри этого окна отвечает:

```http
HTTP/1.1 429 Too Many Requests
Retry-After: 87
Cache-Control: no-store
```

```json
{
  "type": "about:blank",
  "title": "Feedback analysis cannot be restarted until the provider retry cooldown expires.",
  "status": 429,
  "code": "analysis_retry_cooldown"
}
```

## Конфигурация backend

Имена переменных окружения используют стандартное ASP.NET Core-разделение `__`:

| Переменная | По умолчанию | Назначение |
| --- | ---: | --- |
| `Ai__Enabled` | `false` | Глобальное включение внешней обработки |
| `Ai__Provider` | `Groq` | Имя провайдера |
| `Ai__Model` | `openai/gpt-oss-20b` | Модель со Structured Outputs |
| `Ai__Groq__ApiKey` | пусто | Секрет GroqCloud; не хранить в Git |
| `Ai__Groq__BaseUrl` | `https://api.groq.com/openai/v1/` | OpenAI-compatible API |
| `Ai__TimeoutSeconds` | `60` | Timeout одного вызова |
| `Ai__MaxRetries` | `2` | Повторные попытки после первой |
| `Ai__MaxConcurrentJobs` | `1` | Параллельные фоновые задачи |
| `Ai__MaxAnswers` | `100` | Максимум ответов |
| `Ai__MaxTotalCharacters` | `20000` | Максимальный объём текстов |
| `Ai__MaxInputTokens` | `6000` | Консервативная оценка входных токенов |
| `Ai__MaxOutputTokens` | `2000` | Лимит ответа модели |
| `Ai__ReasoningEffort` | `low` | Режим reasoning GPT-OSS; уменьшает расход reasoning-токенов для суммаризации |
| `Ai__RetryCooldownSeconds` | `60` | Минимальная пауза перед ручным повтором после окончательного HTTP 429 |
| `Ai__WorkerPollSeconds` | `5` | Интервал проверки очереди |

Если набор не помещается, backend возвращает `413` и ничего не обрезает. Пакетное
разбиение в текущей версии не реализовано. Оценка выполняется консервативно по
UTF-8-размеру полного OpenAI-compatible payload, включая тексты, временные ID,
служебные инструкции и строгую JSON-схему. Это намеренно строже прежней формулы
`characters / 4`, особенно для кириллицы. Отказ Groq `413` преобразуется в безопасный
`provider_token_limit_exceeded` без автоматических повторов.

Для `openai/gpt-oss-20b` используется `reasoning_effort: "low"`,
`include_reasoning: false` и `max_completion_tokens: 2000`. GPT-OSS не поддерживает
`reasoning_format`; одновременно передавать его с `include_reasoning` также нельзя.
Модель допускает `low`, `medium` и `high`; низкий режим достаточен для структурированного резюме и
лучше соответствует бесплатным квотам. Актуальные параметры:
[GPT OSS 20B](https://console.groq.com/docs/model/openai/gpt-oss-20b),
[Reasoning](https://console.groq.com/docs/reasoning),
[Rate Limits](https://console.groq.com/docs/rate-limits).

## Защита данных и эксплуатация

Backend не загружает и не отправляет GroqCloud ФИО из именной отправки, user ID,
participation ID, token hash, IP, cookies или session ID. Email и похожие на телефоны
последовательности заменяются маркерами. Это минимизация, а не гарантия анонимности.
Отзывы помещаются в отдельный JSON-блок, а system prompt запрещает исполнять их как
инструкции. Полные prompts и ответы модели не журналируются.

На 25 октября 2026 документация Groq указывает, что inference-запросы подходят для
Zero Data Retention, но ZDR включается администратором организации в **Data Controls**.
Перед эксплуатационным включением `Ai__Enabled` администратор обязан проверить ZDR
именно в используемой Groq-организации и зафиксировать результат проверки. Наличие
ZDR этим приложением не проверяется и не предполагается. Актуальные условия:
[Your Data in GroqCloud](https://console.groq.com/docs/your-data).

## Очередь и миграция

Миграция `20261025072432_AddFeedbackAnalyses` создаёт таблицу
`poll_feedback_analyses`. Развёртывание: сделать резервную копию, проверить SQL
`scripts/migrations/20261025072432_add_feedback_analyses.sql` на staging PostgreSQL 18,
применить миграцию при выключенном `Ai__Enabled`, развернуть backend, настроить секрет
и ZDR, затем отдельно включить функцию. Откат удаляет только таблицу анализов; перед
откатом сохраните нужные отчёты.

Задача захватывается атомарно через `FOR UPDATE SKIP LOCKED`. После перезапуска
зависшие `processing`-задачи возвращаются в очередь до исчерпания лимита попыток.
Ручной перезапуск `failed` выполняется условным атомарным обновлением той же строки:
ID и уникальный отпечаток сохраняются, счётчик попыток и поля результата очищаются.
Конкурентные POST не создают новую строку и не дают двум worker одновременно
захватить один анализ.
Граница между внешним вызовом и сохранением результата не является транзакцией:
если процесс остановится после ответа Groq, но до commit, возможен повторный вызов.
Уникальный отпечаток набора ответов предотвращает параллельное создание двух задач,
но не устраняет этот остаточный риск внешнего at-least-once вызова.
