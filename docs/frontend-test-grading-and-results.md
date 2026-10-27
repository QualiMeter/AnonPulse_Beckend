# Финальная проверка TEST и личный результат участника

Этот контракт покрывает ручное оценивание, явное принятие предложения ИИ, финализацию попытки и
получение участником только своего результата. Все преподавательские маршруты доступны владельцу
TEST и администратору, требуют cookie-аутентификацию и не кешируются. Изменяющие запросы также
требуют `X-CSRF-TOKEN`, полученный через `GET /api/auth/csrf-token`.

ИИ остаётся необязательным: `Ai__KnowledgeGradingEnabled=false` не мешает ручной проверке. Ни одно
предложение ИИ не применяется автоматически, включая предложения с `needsReview: false`.

## Рекомендуемый поток интерфейса преподавателя

1. После закрытия TEST загрузить попытки.
2. При необходимости получить предложения ИИ по существующему контракту knowledge grading.
3. Для каждого `pending` ответа выставить собственный балл или явно принять конкретное предложение.
   Закрытый вопрос с `auto_graded` также можно скорректировать.
4. Финализировать попытку. Backend ещё раз пересчитает сумму и нормализованный результат.
5. После финализации заблокировать элементы изменения оценки. Историю показывать только
   авторизованному владельцу/администратору.

## Просмотр попыток

```http
GET /api/polls/{pollId}/test-attempts?page=1&pageSize=20
```

Маршрут работает только для закрытого TEST. Элемент `answers` теперь содержит `answerId`; остальные
поля прежнего ответа сохранены:

```json
{
  "items": [{
    "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    "respondentName": "Иванов Иван",
    "respondentGroup": "ИВТ-101",
    "state": "completed",
    "createdAt": "2026-10-27T08:00:00Z",
    "completedAt": "2026-10-27T08:02:00Z",
    "rawScore": 5.00,
    "maximumScore": 10.00,
    "normalizedScore": null,
    "isFullyGraded": false,
    "isFinal": false,
    "answers": [{
      "questionId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
      "selectedOptionIds": [],
      "text": "Ответ участника",
      "gradingState": "pending",
      "score": null,
      "maximumScore": 5.00,
      "answerId": "cccccccc-cccc-cccc-cccc-cccccccccccc"
    }]
  }],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1,
  "totalPages": 1
}
```

Состояния ответа: `pending`, `auto_graded`, `teacher_graded`. `pending` всегда имеет `score: null`;
два оценённых состояния всегда имеют числовой балл.

## Предложения ИИ

Запуск и чтение предложений описаны в
[frontend-knowledge-grading.md](frontend-knowledge-grading.md). Из ответа нужен `answerId`, а для
явного принятия — идентификатор предложения. Получить его следует из `items` конкретного job:

```http
GET /api/polls/{pollId}/questions/{questionId}/knowledge-grading/jobs/{jobId}/results
```

В текущем контракте результата есть `suggestionId` сохранённого предложения. Если frontend хранит
только агрегированный список старой версии без идентификатора предложения, нужно повторно запросить
актуальный job после обновления backend. `needsReview: true` — предупреждение для
интерфейса, но не запрет ручного решения.

## Ручное выставление или изменение балла

```http
PUT /api/polls/{pollId}/test-attempts/{attemptId}/answers/{answerId}/grade
Content-Type: application/json
X-CSRF-TOKEN: ...

{"score":4.50,"suggestionId":null}
```

Для принятия предложения ИИ отправляется только его UUID:

```json
{"score":null,"suggestionId":"dddddddd-dddd-dddd-dddd-dddddddddddd"}
```

Нужно задать ровно одно из двух полей. Ручной `score` находится в диапазоне
`0..maximumScore` и имеет не более двух знаков после запятой. Принять можно только завершённое
предложение для этого же ответа и TEST. Передача `suggestionId` не означает автоматического
применения: PUT является явным действием преподавателя.

Ответ `200 OK`:

```json
{
  "answerId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
  "score": 4.50,
  "gradingState": "teacher_graded"
}
```

Корректировка `auto_graded` создаёт запись истории со старым автоматическим баллом. Повторное
изменение также создаёт новую запись. После финализации PUT возвращает `409 attempt_finalized`.

## Финализация

```http
POST /api/polls/{pollId}/test-attempts/{attemptId}/finalize
X-CSRF-TOKEN: ...
```

Тело не требуется. Если остался хотя бы один `pending`/`score: null`, ответ —
`409 attempt_has_pending_answers`. При успехе backend под блокировкой попытки пересчитывает:

`normalizedScore = round(rawScore / maximumScore * testScale, 2)`

Половины округляются от нуля. Ответ `200 OK`:

```json
{
  "attemptId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "rawScore": 8.50,
  "maximumScore": 10.00,
  "normalizedScore": 8.50,
  "isFullyGraded": true,
  "isFinal": true,
  "finalizedByUserId": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
  "finalizedAt": "2026-10-27T08:15:00Z"
}
```

Повторный POST идемпотентен: возвращает тот же результат, ответственного и время. Оценивание и
финализация сериализуются на одной попытке, поэтому конкурентные запросы не создают частично
утверждённый результат.

## История решений

```http
GET /api/polls/{pollId}/test-attempts/{attemptId}/grade-history
```

```json
{
  "attemptId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "changes": [{
    "id": "ffffffff-ffff-ffff-ffff-ffffffffffff",
    "attemptId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    "answerId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
    "actorId": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
    "oldScore": null,
    "newScore": 4.00,
    "source": "ai_suggestion",
    "suggestionId": "dddddddd-dddd-dddd-dddd-dddddddddddd",
    "changedAt": "2026-10-27T08:10:00Z"
  }],
  "finalizedByUserId": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
  "finalizedAt": "2026-10-27T08:15:00Z"
}
```

`source` равен `manual` или `ai_suggestion`. История не содержит ФИО, группу, текст ответа,
секрет, ключи правильных ответов или данные Groq.

## Сохранение accessSecret на фронтенде

Успешная отправка TEST по-прежнему единственный раз возвращает `attemptId` и `accessSecret`.
Сохраните пару локально для конкретного TEST (например, в защищаемом приложением local storage),
не помещайте секрет в URL, query string, аналитику, логи, crash reports или сообщения об ошибках.
Backend хранит только SHA-256 и не может восстановить потерянный секрет. Не создавайте cookie и не
пытайтесь заменить этот секрет другим идентификатором.

## Личный результат участника

```http
POST /api/public/polls/{pollId}/test-attempts/{attemptId}/result
Content-Type: application/json

{"accessSecret":"base64url-secret-from-submission"}
```

Маршрут остаётся доступным после закрытия TEST, не создаёт cookie и всегда отвечает
`Cache-Control: no-store`. До финализации правильный секрет даёт только безопасный статус:

```json
{"attemptId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","isFinal":false}
```

После финализации ответ содержит только эту попытку и её ответы:

```json
{
  "attemptId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "isFinal": true,
  "testTitle": "Основы ООП",
  "respondentName": "Иванов Иван",
  "respondentGroup": "ИВТ-101",
  "rawScore": 8.50,
  "maximumScore": 10.00,
  "normalizedScore": 8.50,
  "testScale": 10,
  "answers": [{
    "answerId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
    "questionId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
    "questionText": "Объясните принцип полиморфизма",
    "selectedOptionIds": [],
    "selectedOptions": [],
    "text": "Ответ участника",
    "score": 4.50,
    "maximumScore": 5.00
  }]
}
```

Ответ не содержит правильные ответы, рубрики, историю, чужие попытки, Groq-данные или хеш секрета.
Неверный секрет, чужой `attemptId`, подменённый `pollId` и отсутствующая попытка дают одинаковый
`404 test_result_not_found` без ФИО, статуса или баллов.

## Ошибки

| HTTP | Код/случай | Действие frontend |
| --- | --- | --- |
| `400` | validation problem, `grade_source_invalid`, `score_precision_invalid`, `score_out_of_range` | Исправить тело/балл; malformed `accessSecret` не повторять автоматически |
| `401` | нет действующей сессии преподавателя | Показать вход |
| `403` | пользователь аутентифицирован, но не имеет роли Teacher/Admin | Не показывать защищённые действия |
| `404` | `test_attempt_not_found`, `test_result_not_found`; также чужой владелец или подмена вложенных UUID | Не различать причины публично; обновить список для преподавателя |
| `409` | `not_a_test`, `test_not_closed`, `attempt_has_pending_answers`, `attempt_finalized` | Исправить состояние/завершить проверку; не повторять без изменения данных |
| `429` | отдельный лимит публичной выдачи результата | Уменьшить частоту и повторить позже |

Все преподавательские UUID проверяются как единая цепочка `poll -> attempt -> answer/suggestion`.
`SURVEY` эти операции не принимает.

Пример ошибки бизнес-состояния (`409`):

```json
{"status":409,"title":"Every answer must have a final score before finalization.","code":"attempt_has_pending_answers"}
```

Пример validation problem (`400`):

```json
{
  "title": "Score must have no more than two decimal places.",
  "status": 400,
  "errors": {"Score":["Score must have no more than two decimal places."]},
  "code": "score_precision_invalid"
}
```

Типовые ответы инфраструктуры для `401`, `403` и `429` могут не иметь бизнес-кода; frontend должен
в первую очередь ветвиться по HTTP status. Безопасный публичный `404` всегда имеет вид:

```json
{"status":404,"title":"Test result was not found.","code":"test_result_not_found"}
```
