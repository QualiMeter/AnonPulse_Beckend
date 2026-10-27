using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.Polls;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnonPulse.Api.Tests.Polls;

public sealed class PollTextPurposeValidationTests
{
    [Theory]
    [InlineData("feedback", true)]
    [InlineData("knowledge", false)]
    public async Task Survey_AllowsOnlyFeedbackTextQuestions(
        string textPurpose,
        bool shouldSucceed)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        var service = new PollService(db, TimeProvider.System);

        var request = new CreatePollRequest
        {
            Kind = "survey",
            Questions =
            [
                new CreatePollQuestionRequest(
                    Text: "What should we improve?",
                    Options: [],
                    Type: "text",
                    TextPurpose: textPurpose,
                    KnowledgeSettings: textPurpose == "knowledge"
                        ? new KnowledgeQuestionSettingsRequest(
                            "Expected answer",
                            "Grading criteria",
                            5)
                        : null)
            ]
        };

        PollCreationResult result =
            await service.CreateAsync(Guid.NewGuid(), request);

        Assert.Equal(shouldSucceed, result.Succeeded);

        if (!shouldSucceed)
        {
            Assert.Contains(
                "Questions[0].TextPurpose",
                result.ValidationErrors.Keys);
        }
    }

    [Fact]
    public async Task LegacySurveyWithKnowledgeQuestion_CannotBePublished()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);

        Guid teacherId = Guid.NewGuid();

        var poll = new Poll
        {
            Id = Guid.NewGuid(),
            CreatorId = teacherId,
            Kind = PollKind.Survey,
            Status = PollStatus.Draft,
            CreatedAt = DateTime.UtcNow
        };

        var question = new PollQuestion
        {
            Id = Guid.NewGuid(),
            PollId = poll.Id,
            Text = "Explain the concept",
            Position = 0,
            Type = PollQuestionType.Text,
            TextPurpose = TextQuestionPurpose.Knowledge
        };

        question.KnowledgeSettings = new PollQuestionKnowledgeSettings
        {
            QuestionId = question.Id,
            ExpectedAnswer = "Expected answer",
            GradingCriteria = "Grading criteria",
            MaxScore = 5
        };

        poll.Questions.Add(question);
        db.Polls.Add(poll);
        await db.SaveChangesAsync();

        var service = new PollService(db, TimeProvider.System);

        PollTransitionResult result =
            await service.PublishAsync(teacherId, false, poll.Id);

        Assert.Equal(PollTransitionOutcome.Conflict, result.Outcome);

        Poll savedPoll = await db.Polls
            .SingleAsync(item => item.Id == poll.Id);

        Assert.Equal(PollStatus.Draft, savedPoll.Status);
        Assert.Single(savedPoll.Questions);
    }

    [Theory]
    [InlineData("knowledge", true)]
    [InlineData("feedback", false)]
    public async Task Test_AllowsOnlyKnowledgeTextQuestions(
        string textPurpose,
        bool shouldSucceed)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        var service = new PollService(db, TimeProvider.System);

        var request = new CreatePollRequest
        {
            Kind = "test",
            ResponseMode = "identified",
            TestScale = 10,
            Questions =
            [
                new CreatePollQuestionRequest(
                    Text: "Explain the concept",
                    Options: [],
                    Type: "text",
                    TextPurpose: textPurpose,
                    KnowledgeSettings: textPurpose == "knowledge"
                        ? new KnowledgeQuestionSettingsRequest(
                            "Expected answer",
                            "Grading criteria",
                            5)
                        : null)
            ]
        };

        PollCreationResult result =
            await service.CreateAsync(Guid.NewGuid(), request);

        Assert.Equal(shouldSucceed, result.Succeeded);

        if (!shouldSucceed)
        {
            Assert.Contains(
                "Questions[0].TextPurpose",
                result.ValidationErrors.Keys);
        }
    }

    [Fact]
    public async Task Survey_InvalidUpdate_PreservesExistingQuestion()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        var service = new PollService(db, TimeProvider.System);
        Guid teacherId = Guid.NewGuid();

        var validRequest = new CreatePollRequest
        {
            Kind = "survey",
            Questions =
            [
                new CreatePollQuestionRequest(
                    Text: "What should we improve?",
                    Options: [],
                    Type: "text",
                    TextPurpose: "feedback")
            ]
        };

        PollCreationResult created =
            await service.CreateAsync(teacherId, validRequest);

        Assert.True(created.Succeeded);
        Guid pollId = created.Poll!.Id;

        var invalidRequest = new CreatePollRequest
        {
            Kind = "survey",
            Questions =
            [
                new CreatePollQuestionRequest(
                    Text: "Explain the concept",
                    Options: [],
                    Type: "text",
                    TextPurpose: "knowledge",
                    KnowledgeSettings: new KnowledgeQuestionSettingsRequest(
                        "Expected answer",
                        "Grading criteria",
                        5))
            ]
        };

        PollUpdateResult updated =
            await service.UpdateAsync(teacherId, pollId, invalidRequest);

        Assert.Equal(PollUpdateOutcome.Invalid, updated.Outcome);

        PollQuestion savedQuestion = await db.PollQuestions
            .SingleAsync(question => question.PollId == pollId);

        Assert.Equal("What should we improve?", savedQuestion.Text);
        Assert.Equal(TextQuestionPurpose.Feedback, savedQuestion.TextPurpose);
    }

    [Theory]
    [InlineData(PollStatus.Open)]
    [InlineData(PollStatus.Closed)]
    public async Task HistoricalSurveyWithKnowledgeQuestion_RemainsReadable(
        PollStatus status)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        Guid teacherId = Guid.NewGuid();
        var poll = new Poll
        {
            Id = Guid.NewGuid(),
            CreatorId = teacherId,
            Kind = PollKind.Survey,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };
        var question = new PollQuestion
        {
            Id = Guid.NewGuid(),
            PollId = poll.Id,
            Text = "Historical knowledge question",
            Position = 0,
            Type = PollQuestionType.Text,
            TextPurpose = TextQuestionPurpose.Knowledge
        };
        question.KnowledgeSettings = new PollQuestionKnowledgeSettings
        {
            QuestionId = question.Id,
            ExpectedAnswer = "Expected answer",
            GradingCriteria = "Grading criteria",
            MaxScore = 5
        };
        poll.Questions.Add(question);
        db.Polls.Add(poll);
        await db.SaveChangesAsync();

        var service = new PollService(db, TimeProvider.System);
        PollResponse? response = await service.GetByIdAsync(teacherId, poll.Id);

        Assert.NotNull(response);
        Assert.Equal(status == PollStatus.Open ? "open" : "closed", response.Status);
        Assert.Equal("knowledge", Assert.Single(response.Questions).TextPurpose);
        Assert.Equal(TextQuestionPurpose.Knowledge, question.TextPurpose);
        Assert.NotNull(question.KnowledgeSettings);
    }

    [Theory]
    [InlineData("survey", "single_choice")]
    [InlineData("survey", "multiple_choice")]
    [InlineData("test", "single_choice")]
    [InlineData("test", "multiple_choice")]
    public async Task ChoiceQuestions_AreAllowedInBothModes(
        string kind,
        string questionType)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        var service = new PollService(db, TimeProvider.System);

        bool isTest = kind == "test";

        var request = new CreatePollRequest
        {
            Kind = kind,
            ResponseMode = isTest ? "identified" : "anonymous",
            TestScale = isTest ? 10 : null,
            Questions =
            [
                new CreatePollQuestionRequest(
                    Text: "Choose the correct answer",
                    Options: ["A", "B", "C"],
                    Type: questionType,
                    Weight: isTest ? 2m : null,
                    CorrectOptionIndexes: isTest
                        ? questionType == "multiple_choice"
                            ? [0, 1]
                            : [0]
                        : null)
            ]
        };

        PollCreationResult result =
            await service.CreateAsync(Guid.NewGuid(), request);

        Assert.True(
            result.Succeeded,
            string.Join("; ", result.ValidationErrors.SelectMany(
                error => error.Value)));
    }
}
