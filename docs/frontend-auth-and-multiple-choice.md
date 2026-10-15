# Контракт frontend: remember me и множественный выбор

## Авторизация

`POST /api/auth/login` обратно совместим с прежним телом:

```json
{
  "email": "teacher@example.com",
  "password": "not-shown-here",
  "rememberMe": true
}
```

`rememberMe` необязателен и по умолчанию равен `false`.

- `false`: серверный ticket действует 8 часов, cookie является сессионной и не
  содержит `Expires`/`Max-Age`; браузер должен удалить её при завершении сессии;
- `true`: cookie постоянная, серверный ticket и `Expires` действуют 30 дней;
- `SlidingExpiration` включён: активность после половины выбранного срока
  перевыпускает ticket/cookie на тот же срок;
- максимальный настраиваемый persistent lifetime — 30 дней;
- cookie остаётся `HttpOnly`, `Secure`, `SameSite=Strict`, поэтому frontend не
  должен читать её или копировать пароль/токен в `localStorage`;
- `POST /api/auth/logout` завершает оба типа сессии.

Frontend передаёт `credentials: "include"` для same-site запросов и показывает
обычный checkbox «Запомнить меня». Старый запрос без поля продолжает работать.

## Вопросы и голосование

В каждом объекте `questions` создание и полная замена черновика принимают:

```json
{
  "text": "Какие темы разобрать подробнее?",
  "options": ["LINQ", "EF Core", "ASP.NET Core"],
  "allowsMultipleAnswers": true
}
```

Поле возвращается в преподавательских ответах создания/списка/просмотра/PUT,
публичном `GET /api/public/polls/{id}` и статистике. Если оно отсутствует при
создании, сервер использует `false`. Legacy `question + options` остаётся
одиночным выбором.

Для публичного POST формат массива не изменён. Одиночный вопрос имеет ровно один
элемент; множественный — несколько элементов с одинаковым `questionId` и
различными `optionId`. Минимум один вариант обязателен для каждого вопроса.

```json
{
  "answers": [
    { "questionId": "QUESTION_1", "optionId": "OPTION_1" },
    { "questionId": "QUESTION_1", "optionId": "OPTION_2" },
    { "questionId": "QUESTION_2", "optionId": "OPTION_3" }
  ]
}
```

Frontend должен использовать radio controls при `false` и checkbox controls при
`true`. В статистике процент множественного варианта — доля участников,
выбравших его; сумма процентов такого вопроса может быть больше 100%.
