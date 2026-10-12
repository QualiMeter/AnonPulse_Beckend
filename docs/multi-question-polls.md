# Многовопросные опросы и переходы статусов

## ER-модель

Иерархия опроса после миграции:

- `polls` — автор, необязательный `title`, статус и даты;
- `poll_questions` — вопросы опроса, упорядоченные по `position`;
- `poll_options` — варианты конкретного вопроса, упорядоченные по `position`;
- `votes.option_id` продолжает ссылаться на неизменившийся ID варианта;
- `vote_participations.poll_id` остаётся на уровне опроса целиком.

Для `(poll_id, position)` вопросов и `(question_id, position)` вариантов созданы
уникальные индексы. Уникальность нормализованного текста варианта внутри вопроса
дополнительно обеспечена expression index по `(question_id, lower(btrim(text)))`.
Отрицательные позиции и пустой после `btrim` текст запрещены CHECK constraints.
Минимум один вопрос и минимум два варианта остаются правилами сервиса, поскольку
межстрочные cardinality-ограничения нельзя выразить простым CHECK constraint
PostgreSQL.

## API

Канонический новый запрос:

```json
{
  "title": "Обратная связь по лекции",
  "questions": [
    {
      "text": "Насколько понятен материал?",
      "options": ["Полностью", "Частично", "Непонятен"]
    },
    {
      "text": "Подходит ли темп занятий?",
      "options": ["Да", "Нет"]
    }
  ]
}
```

`title` необязателен: аналитик и frontend должны отдельно согласовать, становится
ли он обязательным позднее. Пустой или состоящий из пробелов title сохраняется как
`null`.

Старый запрос `question + options` продолжает работать и создаёт один вопрос с
позицией `0`. Нельзя одновременно передавать `questions` и legacy-поля.

Ответ содержит канонический массив `questions`, включая ID и позиции вопросов и
вариантов. Для одновопросного опроса legacy-поля `question` и `options` также
заполняются. Для многовопросного опроса `question` равен `null`, а `options` —
пустой массив. Frontend следует перейти на чтение `questions` до удаления legacy-
полей в отдельной согласованной версии API.

Переходы состояния:

- `POST /api/polls/{id}/publish`: только `draft → open`;
- `POST /api/polls/{id}/close`: только `open → closed`.

Автор-преподаватель и пользователь с ролью `admin` могут менять состояние.
Преподавателю чужой опрос возвращается как `404`, чтобы не раскрывать его наличие.
Невалидный переход возвращает `409 application/problem+json`. Конкурентные
переходы защищены optimistic concurrency через PostgreSQL `xmin`.

Текущее разграничение доступа намеренно асимметрично: администратор может
публиковать и закрывать опросы, но без дополнительной роли `teacher` получает
`403` на обычных преподавательских `GET /api/polls` и `GET /api/polls/{id}`.
Расширение доступа администратора к чтению требует отдельного согласования и в
рамках этой доработки не выполняется.

## Миграция существующих данных

Миграция `20261012163600_AddPollQuestionsAndStatusTransitions`:

1. создаёт `poll_questions`;
2. для каждого старого опроса создаёт вопрос с `id = poll.id`, прежним текстом и
   позицией `0`;
3. переименовывает `poll_options.poll_id` в `question_id`;
4. сохраняет ID вариантов, поэтому `votes.option_id` не меняется;
5. не меняет users, decisions, votes и vote_participations;
6. удаляет старую колонку `polls.question` только после переноса.

Миграция проверена интеграционным тестом на непустой PostgreSQL с существующими
vote, participation и decision.

## Применение до deployment

Необходимо окно обслуживания. Перед изменением создайте и проверьте backup:

```bash
backup_dir="$(mktemp -d)"
chmod 700 "$backup_dir"

railway ssh -p <PROJECT_ID> -s Postgres -e production -- sh -c \
  'PGPASSWORD="$POSTGRES_PASSWORD" pg_dump -Fc --no-owner --no-acl \
   -h 127.0.0.1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"' \
  > "$backup_dir/anonpulse-before-multi-question.dump"

chmod 600 "$backup_dir/anonpulse-before-multi-question.dump"
pg_restore -l "$backup_dir/anonpulse-before-multi-question.dump"
```

После проверки целевой БД примените только подготовленный скрипт:

До запуска проверьте данные старой схемы. Все запросы должны вернуть ноль строк:

```sql
SELECT id FROM polls WHERE btrim(question) = '';

SELECT poll_id, position, count(*)
FROM poll_options
GROUP BY poll_id, position
HAVING count(*) > 1;

SELECT poll_id, lower(btrim(text)), count(*)
FROM poll_options
GROUP BY poll_id, lower(btrim(text))
HAVING count(*) > 1;

SELECT id FROM poll_options WHERE btrim(text) = '';
```

Если preflight обнаружил строки, миграцию применять нельзя: нужно отдельно
согласовать исправление данных. Скрипт выполняется в транзакции и при нарушении
новых ограничений откатывается целиком.

```bash
railway ssh -p <PROJECT_ID> -s Postgres -e production -- sh -c \
  'PGPASSWORD="$POSTGRES_PASSWORD" psql -X -v ON_ERROR_STOP=1 \
   -h 127.0.0.1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"' \
  < scripts/migrations/20261012163600_add_poll_questions_and_status_transitions.sql
```

Проверьте migration history, число опросов, вопросов, вариантов и orphan rows до
deployment приложения. API не применяет миграции автоматически.

## Откат

Старая схема не может представить несколько вопросов. Down migration намеренно
отказывается работать, если хотя бы у одного опроса не ровно один вопрос. После
начала создания многовопросных опросов безопасный откат — остановить запись,
вернуть предыдущую версию приложения и восстановить проверенный pre-migration
backup. До появления новых данных допустим Down, но он удаляет необязательные
title, поэтому backup всё равно предпочтителен.
