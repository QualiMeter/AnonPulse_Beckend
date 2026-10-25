# Именные опросы: интеграция фронтенда

## Режим прохождения

При создании или полном обновлении черновика передавайте `responseMode`:

- `anonymous` — ответы не объединяются в индивидуальные отправки;
- `identified` — участник самостоятельно вводит отображаемое имя.

Если поле отсутствует или равно `null`, backend использует `anonymous`. Это сохраняет
совместимость старых клиентов. Режим можно менять только пока опрос имеет статус
`draft`; после публикации `PUT /api/polls/{id}` вернёт `409`.

```http
POST /api/polls
Content-Type: application/json

{
  "title": "Итоги занятия",
  "responseMode": "identified",
  "questions": [
    {
      "text": "Выберите тему",
      "type": "single_choice",
      "options": ["API", "База данных"]
    },
    {
      "text": "Что было полезно?",
      "type": "text",
      "textPurpose": "feedback",
      "options": []
    }
  ]
}
```

Ответы преподавательского `POST/GET/PUT /api/polls`, административных
`GET /api/admin/polls` и `GET /api/admin/polls/{id}`, а также публичного
`GET /api/public/polls/{id}` содержат:

```json
{
  "id": "33be9801-814b-4f8a-9c4f-81692b42a307",
  "title": "Итоги занятия",
  "responseMode": "identified",
  "questions": []
}
```

## Публичное прохождение

Поле имени показывается только если публичный опрос вернул
`responseMode: "identified"`. Подпишите его так, чтобы было ясно: имя вводит сам
участник и backend не подтверждает его личность.

Как и раньше, сначала откройте `GET /api/public/polls/{id}`, чтобы получить защищённую
cookie сессии голосования. При отправке обязателен заголовок
`X-AnonPulse-Vote: 1`.

Именной запрос:

```http
POST /api/public/polls/33be9801-814b-4f8a-9c4f-81692b42a307/votes
X-AnonPulse-Vote: 1
Content-Type: application/json

{
  "respondentName": "Иванов Иван",
  "answers": [
    {
      "questionId": "a88d92cf-ddac-4a5f-a0a2-fec3c14c44a6",
      "optionId": "886cc71d-f33a-4d3a-909c-71151c9b818d"
    },
    {
      "questionId": "03bca596-c829-4e71-adbc-d812f31a8a82",
      "text": "Практический разбор API"
    }
  ]
}
```

Для анонимного опроса оставьте прежний контракт и не отправляйте поле
`respondentName` вообще:

```json
{
  "answers": [
    {
      "questionId": "a88d92cf-ddac-4a5f-a0a2-fec3c14c44a6",
      "optionId": "886cc71d-f33a-4d3a-909c-71151c9b818d"
    }
  ]
}
```

Backend удаляет ведущие, замыкающие и повторяющиеся пробельные символы в имени.
После нормализации допустимо от 1 до 150 символов. Одинаковые имена разрешены.

Типичные ошибки имеют стандартный `ValidationProblemDetails`:

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "RespondentName": [
      "RespondentName must contain 1 to 150 characters."
    ]
  }
}
```

Если имя передано анонимному опросу:

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "RespondentName": [
      "RespondentName is not allowed for an anonymous poll."
    ]
  }
}
```

Ошибочная отправка атомарно откатывается: имя, ответы и участие не сохраняются.
Повторное прохождение с той же cookie по-прежнему возвращает `409`, а rate limiting
и остальные публичные ошибки не изменились.

## Получение именных отправок

После закрытия именного опроса его создатель или администратор может вызвать:

```http
GET /api/polls/33be9801-814b-4f8a-9c4f-81692b42a307/submissions?page=1&pageSize=20
```

`page` начинается с 1, `pageSize` — от 1 до 100. Ответ запрещён к кешированию.

```json
{
  "items": [
    {
      "id": "10e8ed67-1d06-4d9d-9ab7-df9516c06650",
      "respondentName": "Иванов Иван",
      "answers": [
        {
          "questionId": "a88d92cf-ddac-4a5f-a0a2-fec3c14c44a6",
          "selectedOptionIds": [
            "886cc71d-f33a-4d3a-909c-71151c9b818d"
          ],
          "text": null
        },
        {
          "questionId": "03bca596-c829-4e71-adbc-d812f31a8a82",
          "selectedOptionIds": [],
          "text": "Практический разбор API"
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

Коды ответа:

- `401` — пользователь не вошёл;
- `400` — неверная пагинация;
- `404` — опрос отсутствует или принадлежит другому преподавателю;
- `409` — опрос ещё не закрыт либо имеет режим `anonymous`.

## Что не меняется

- статусы и переходы остаются `draft → open → closed`;
- форматы `single_choice`, `multiple_choice`, `text` (`feedback` и `knowledge`)
  работают в обоих режимах;
- `GET /api/polls/{id}/results` остаётся агрегированным и никогда не возвращает имена;
- публичный API не показывает имена других участников;
- старые опросы и запросы без `responseMode` остаются анонимными;
- старый просмотр отдельных анонимных текстовых ответов сохраняет свой контракт;
- имена не являются учётными записями или подтверждёнными личностями.
