using AnonPulse.Api.Contracts.Decisions;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AnonPulse.Api.Services.Decisions;

public sealed class DecisionService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : IDecisionService
{
    public async Task<DecisionUpsertResult> UpsertAsync(
        Guid actorId,
        bool isAdmin,
        Guid pollId,
        UpsertDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        DecisionTextValidation validation = Validate(request);
        if (validation.Errors.Count > 0)
        {
            return new DecisionUpsertResult(
                DecisionUpsertOutcome.Invalid,
                ValidationErrors: validation.Errors);
        }

        await using IDbContextTransaction? transaction =
            dbContext.Database.IsRelational()
                ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
                : null;

        Poll? poll = transaction is null
            ? await dbContext.Polls
                .Include(item => item.Decision)
                .SingleOrDefaultAsync(item => item.Id == pollId, cancellationToken)
            : await dbContext.Polls
                .FromSqlInterpolated(
                    $"SELECT p.*, p.xmin FROM polls AS p WHERE p.id = {pollId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);

        if (poll is null || (!isAdmin && poll.CreatorId != actorId))
        {
            await RollbackAsync(transaction, cancellationToken);
            return new DecisionUpsertResult(DecisionUpsertOutcome.NotFound);
        }

        if (poll.Status == PollStatus.Draft)
        {
            await RollbackAsync(transaction, cancellationToken);
            return new DecisionUpsertResult(
                DecisionUpsertOutcome.Conflict,
                Error: "A decision cannot be published for a draft poll.");
        }

        if (transaction is not null)
        {
            await dbContext.Entry(poll)
                .Reference(item => item.Decision)
                .LoadAsync(cancellationToken);
        }

        DateTime utcNow = ToPostgreSqlPrecision(
            timeProvider.GetUtcNow().UtcDateTime);
        Decision decision;
        bool changed;
        if (poll.Decision is null)
        {
            decision = new Decision
            {
                Id = Guid.NewGuid(),
                PollId = poll.Id,
                AuthorId = actorId,
                Text = validation.Text,
                PublishedAt = utcNow
            };
            dbContext.Decisions.Add(decision);
            changed = true;
        }
        else
        {
            decision = poll.Decision;
            changed = decision.PublishedAt is null ||
                      !string.Equals(
                          decision.Text,
                          validation.Text,
                          StringComparison.Ordinal);
            if (changed)
            {
                decision.Text = validation.Text;
                decision.PublishedAt = utcNow;
            }
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new DecisionUpsertResult(
            DecisionUpsertOutcome.Succeeded,
            ToResponse(decision));
    }

    public async Task<PublishedDecisionResult> GetPublishedAsync(
        Guid pollId,
        CancellationToken cancellationToken = default)
    {
        DecisionResponse? decision = await dbContext.Decisions
            .AsNoTracking()
            .Where(item =>
                item.PollId == pollId &&
                item.Poll.Status != PollStatus.Draft &&
                item.PublishedAt != null)
            .Select(item => new DecisionResponse(
                item.Id,
                item.PollId,
                item.Text,
                item.PublishedAt!.Value))
            .SingleOrDefaultAsync(cancellationToken);

        return decision is null
            ? new PublishedDecisionResult(PublishedDecisionOutcome.NotFound)
            : new PublishedDecisionResult(
                PublishedDecisionOutcome.Succeeded,
                decision);
    }

    private static DecisionTextValidation Validate(UpsertDecisionRequest request)
    {
        string text = request.Text?.Trim() ?? string.Empty;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (text.Length == 0)
        {
            errors[nameof(request.Text)] = ["Decision text must not be empty."];
        }
        else if (text.Length > DecisionContractLimits.MaximumTextLength)
        {
            errors[nameof(request.Text)] =
                [$"Decision text must not exceed {DecisionContractLimits.MaximumTextLength} characters."];
        }

        return new DecisionTextValidation(text, errors);
    }

    private static async Task RollbackAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    private static DecisionResponse ToResponse(Decision decision) => new(
        decision.Id,
        decision.PollId,
        decision.Text,
        decision.PublishedAt ?? throw new InvalidOperationException(
            "A published decision must have a publication timestamp."));

    private static DateTime ToPostgreSqlPrecision(DateTime value) => new(
        value.Ticks - value.Ticks % 10,
        DateTimeKind.Utc);

    private sealed record DecisionTextValidation(
        string Text,
        IReadOnlyDictionary<string, string[]> Errors);
}
