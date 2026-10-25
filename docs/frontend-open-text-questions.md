# Frontend contract: open text questions

Creation and full draft replacement keep the existing routes. `type` is `single_choice`, `multiple_choice`, or `text`. Old requests may omit `type` and use `allowsMultipleAnswers`; when both are supplied they must agree.

```json
{
  "title": "Обратная связь",
  "questions": [
    { "text": "Понятность", "type": "single_choice", "options": ["Да", "Нет"] },
    { "text": "Темы", "type": "multiple_choice", "options": ["LINQ", "EF Core"] },
    { "text": "Что улучшить?", "type": "text", "textPurpose": "feedback" },
    { "text": "Что такое полиморфизм?", "type": "text", "textPurpose": "knowledge",
      "knowledgeSettings": { "expectedAnswer": "...", "gradingCriteria": "...", "maxScore": 10 } }
  ]
}
```

Choice questions require at least two unique non-empty options. Text questions require no options and cannot set `allowsMultipleAnswers: true`. Text answers may contain at most 4000 characters. `maxScore` must be an integer from 1 through 100. Knowledge settings are private and must never be copied from the teacher response into a student view.

Public poll questions return `type`, compatible `allowsMultipleAnswers`, `textPurpose`, and options (empty for text). They never return knowledge settings.

```json
{
  "answers": [
    { "questionId": "...", "optionId": "..." },
    { "questionId": "...", "optionId": "..." },
    { "questionId": "...", "text": "Больше практических примеров." }
  ]
}
```

Send exactly one text item per text question and one or more distinct option items for multiple choice. Do not mix `text` and `optionId`. Continue sending `X-AnonPulse-Vote: 1` and credentials so the protected voter cookie is included. Validation errors are `400 application/problem+json`; duplicate voting or a non-open poll is `409`.

Owner/admin text review: `GET /api/polls/{pollId}/questions/{questionId}/text-answers?page=1&pageSize=20`. It is available only after closure and returns the standard paged envelope. Raw text is not present in `/results`; a text result contains `textAnswerCount` and an empty `options` array.
