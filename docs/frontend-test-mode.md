# Режим TEST и именные попытки

## Отличие TEST от SURVEY

`kind` разделяет два независимых сценария:

- `survey` — существующий опрос. Если `kind` отсутствует, backend использует `survey`. Поддерживаются `responseMode: "anonymous"` и `"identified"`, отправка остаётся на `POST /api/public/polls/{id}/votes`.
- `test` — проверка знаний без STUDENT-аккаунта. Разрешён только `responseMode: "identified"`; отправка выполняется только через `POST /api/public/polls/{id}/test-submissions`.

Старые опросы и старые именные отправки после миграции остаются `survey`. TEST не использует cookie/token анонимного голосования и не создаёт `vote_participations`, `votes`, `poll_text_answers` или `identified_poll_submissions`.

## Создание TEST

Маршрут и авторизация не изменились: `POST /api/polls`, cookie-сессия преподавателя.

```json
{
  "title": "Основы ООП",
  "kind": "test",
  "responseMode": "identified",
  "testScale": 10,
  "questions": [
    {
      "text": "Какой модификатор запрещает наследование класса в C#?",
      "type": "single_choice",
      "options": ["sealed", "static", "virtual"],
      "weight": 2,
      "correctOptionIndexes": [0]
    },
    {
      "text": "Выберите принципы ООП",
      "type": "multiple_choice",
      "options": ["Инкапсуляция", "Компрессия", "Полиморфизм"],
      "weight": 3,
      "correctOptionIndexes": [0, 2]
    },
    {
      "text": "Объясните полиморфизм",
      "type": "text",
      "textPurpose": "knowledge",
      "options": [],
      "knowledgeSettings": {
        "expectedAnswer": "Объекты разных типов используются через общий контракт.",
        "gradingCriteria": "Назван общий интерфейс и различающееся поведение.",
        "maxScore": 5
      }
    }
  ]
}
```

Допустимые `testScale`: `5`, `10`, `12`, `100`. Для `single_choice` требуется ровно один индекс правильного варианта. Для `multiple_choice` требуется один или несколько уникальных индексов. Индексы начинаются с нуля, должны попадать в массив `options`; backend создаёт UUID вариантов и сохраняет приватные связи с ними.

`weight` обязателен для закрытого вопроса TEST, должен быть больше `0` и не больше `10000`. Для текстового `knowledge` весом служит существующий `knowledgeSettings.maxScore`; `weight` и `correctOptionIndexes` для текста запрещены. Текстовые вопросы TEST должны иметь `textPurpose: "knowledge"`.

Ответ `201` защищённого API содержит `kind`, `testScale`, `weight`, `correctOptionIds`, а для knowledge-вопроса — `expectedAnswer`, `gradingCriteria`, `maxScore`. Эти поля нужны только преподавателю и не выдаются публично.

Неверный тип, шкала, режим, вес или ключ дают `400 ValidationProblemDetails`. Изменять `kind`, `responseMode`, шкалу, вопросы, веса и ключи можно только у `draft`. `PUT /api/polls/{id}` после публикации возвращает `409`. Публикация/закрытие используют существующие маршруты.

## Публичное получение формы

`GET /api/public/polls/{id}` доступен без аккаунта только для `open`.

```json
{
  "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "title": "Основы ООП",
  "responseMode": "identified",
  "kind": "test",
  "testScale": 10,
  "questions": [
    {
      "id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
      "text": "Какой модификатор запрещает наследование класса в C#?",
      "position": 0,
      "type": "single_choice",
      "allowsMultipleAnswers": false,
      "textPurpose": null,
      "options": [
        { "id": "cccccccc-cccc-cccc-cccc-cccccccccccc", "text": "sealed", "position": 0 }
      ]
    }
  ]
}
```

Публичный ответ никогда не содержит ключи, веса, `expectedAnswer`, `gradingCriteria`, ответы/баллы других участников или секреты.

## Отправка именной попытки

`POST /api/public/polls/{pollId}/test-submissions` не требует STUDENT-аккаунта, email или пароля. Обязателен заголовок `X-AnonPulse-Test: 1`. Действуют те же глобальные ограничения частоты и размера тела, что и для публичного голосования.

```json
{
  "respondentName": "Иванов Иван",
  "respondentGroup": "ИВТ-101",
  "clientRequestId": "0b66c008-4b3a-4cb9-af37-61cd1825352c",
  "answers": [
    {
      "questionId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
      "optionId": "cccccccc-cccc-cccc-cccc-cccccccccccc"
    },
    {
      "questionId": "dddddddd-dddd-dddd-dddd-dddddddddddd",
      "text": "Полиморфизм позволяет использовать общий интерфейс для разных типов."
    }
  ]
}
```

Для multiple choice передайте несколько элементов с одним `questionId` и разными `optionId`. Все вопросы обязательны. Имя (1–150 символов) и группа (1–100 символов) нормализуются по пробелам и являются самостоятельно заявленными данными, а не подтверждённой личностью.

Успех: `201 Created`.

```json
{
  "attemptId": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
  "accessSecret": "oXy7...одноразовый_секрет..."
}
```

Фронтенд обязан сразу сохранить `accessSecret` в безопасном клиентском хранилище. Backend генерирует 256 случайных бит криптографическим RNG, возвращает plaintext только в первом успешном ответе и сохраняет только SHA-256 хеш. Секрет не является `clientRequestId`, не выводится в списках и не логируется приложением. Endpoint личного результата появится в следующем PR.

## Идемпотентность и потерянный ответ

`clientRequestId` — новый случайный UUID для одной логической отправки. Уникальность обеспечивается для пары `(pollId, clientRequestId)`, а backend хранит хеш канонического содержимого.

- Первый запрос сохраняет попытку атомарно и возвращает секрет.
- Повтор с тем же UUID и изменённым содержимым: `409`, код `idempotency_key_reused`.
- Повтор с тем же UUID и тем же содержимым не создаёт дубль: `409`, код `submission_already_stored_secret_unavailable`.

Последний ответ означает, что первоначальная транзакция завершилась, но секрет мог быть потерян вместе с HTTP-ответом. Backend намеренно не хранит plaintext и не может повторно выдать его. Интерфейс должен объяснить это пользователю; если требуется новая попытка и тест ещё открыт, пользователь подтверждает повторную отправку с новым `clientRequestId`. Автоматически менять UUID при сетевой ошибке нельзя — это создало бы дубликат.

## Оценивание

- `single_choice`: точный правильный вариант даёт полный `weight`, иначе `0`.
- `multiple_choice`: только точное совпадение множества даёт полный `weight`; пропуск или лишний вариант дают `0`.
- knowledge-текст сохраняется со `gradingState: "pending"` и `score: null`. Это не ноль.

Для полностью проверенной попытки:

`normalizedScore = round(rawScore / maximumScore * testScale, 2)`

Округление — до двух десятичных знаков, половины округляются от нуля. `maximumScore` — сумма весов закрытых вопросов и `knowledgeSettings.maxScore` текстовых. Пока есть `pending`, `normalizedScore` равен `null`, `isFullyGraded` равен `false`. Даже полностью автоматически проверенная попытка возвращается с `isFinal: false`: финальное подтверждение преподавателя не входит в этот PR.

Обычный `GET /api/polls/{id}/results` для TEST возвращает `409`; агрегаты SURVEY не смешиваются с попытками TEST.

## Просмотр попыток преподавателем

`GET /api/polls/{pollId}/test-attempts?page=1&pageSize=20` доступен владельцу-преподавателю и администратору только после `closed`. Чужому преподавателю возвращается `404`; до закрытия и для SURVEY — `409`. Используется существующая cookie-авторизация.

```json
{
  "items": [
    {
      "id": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
      "respondentName": "Иванов Иван",
      "respondentGroup": "ИВТ-101",
      "state": "completed",
      "createdAt": "2026-10-26T04:00:00Z",
      "completedAt": "2026-10-26T04:00:00Z",
      "rawScore": 5,
      "maximumScore": 10,
      "normalizedScore": null,
      "isFullyGraded": false,
      "isFinal": false,
      "answers": [
        {
          "questionId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
          "selectedOptionIds": ["cccccccc-cccc-cccc-cccc-cccccccccccc"],
          "text": null,
          "gradingState": "auto_graded",
          "score": 2,
          "maximumScore": 2
        }
      ]
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1,
  "totalPages": 1
}
```

Ни секрет, ни его хеш, ни `clientRequestId` административным ответом не выдаются.

## HTTP-коды

| Маршрут | Код | Значение |
|---|---:|---|
| `POST /api/polls` | 201 | Черновик создан |
| `POST /api/polls` | 400 | Невалидная конфигурация TEST |
| `PUT /api/polls/{id}` | 409 | Опрос уже опубликован/закрыт |
| `GET /api/public/polls/{id}` | 404 | Опрос не существует |
| `GET /api/public/polls/{id}` | 409 | Опрос не открыт |
| `POST .../test-submissions` | 201 | Попытка сохранена, секрет выдан один раз |
| `POST .../test-submissions` | 400 | Невалидные ФИО, группа, UUID или ответы; записей не создано |
| `POST .../test-submissions` | 404 | Опрос не существует |
| `POST .../test-submissions` | 409 | Не TEST, не открыт или конфликт идемпотентности |
| `POST .../test-submissions` | 429 | Ограничение частоты |
| `GET .../test-attempts` | 401/403 | Нет подходящей аутентификации/роли |
| `GET .../test-attempts` | 404 | Тест не найден либо принадлежит другому преподавателю |
| `GET .../test-attempts` | 409 | Не TEST или тест ещё не закрыт |

## Приватность и последующие PR

ФИО и группа заявлены самим участником; backend не связывает их с аккаунтом, IP, cookie или анонимными голосами. В этом PR нет Groq-проверки knowledge-текста, личной выдачи результата, ручного подтверждения оценки, регистрации студентов или экспорта.
