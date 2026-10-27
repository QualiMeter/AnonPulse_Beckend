using AnonPulse.Api.Data.Configurations;
using AnonPulse.Api.Models;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AnonPulse.Api.Data;

public class ApplicationDbContext
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>,
        IDataProtectionKeyContext
{
    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Poll> Polls => Set<Poll>();

    public DbSet<PollQuestion> PollQuestions => Set<PollQuestion>();

    public DbSet<PollOption> PollOptions => Set<PollOption>();

    public DbSet<PollTextAnswer> PollTextAnswers => Set<PollTextAnswer>();

    public DbSet<PollFeedbackAnalysis> PollFeedbackAnalyses => Set<PollFeedbackAnalysis>();

    public DbSet<PollKnowledgeGradingJob> PollKnowledgeGradingJobs =>
        Set<PollKnowledgeGradingJob>();

    public DbSet<PollKnowledgeGradingSuggestion> PollKnowledgeGradingSuggestions =>
        Set<PollKnowledgeGradingSuggestion>();

    public DbSet<AiTokenBudgetPeriod> AiTokenBudgetPeriods => Set<AiTokenBudgetPeriod>();

    public DbSet<AiTokenBudgetReservation> AiTokenBudgetReservations =>
        Set<AiTokenBudgetReservation>();

    public DbSet<PollQuestionKnowledgeSettings> PollQuestionKnowledgeSettings =>
        Set<PollQuestionKnowledgeSettings>();

    public DbSet<Vote> Votes => Set<Vote>();

    public DbSet<VoteParticipation> VoteParticipations => Set<VoteParticipation>();

    public DbSet<IdentifiedPollSubmission> IdentifiedPollSubmissions =>
        Set<IdentifiedPollSubmission>();

    public DbSet<PollQuestionCorrectOption> PollQuestionCorrectOptions =>
        Set<PollQuestionCorrectOption>();

    public DbSet<NamedAttempt> NamedAttempts => Set<NamedAttempt>();

    public DbSet<NamedAttemptAnswer> NamedAttemptAnswers => Set<NamedAttemptAnswer>();

    public DbSet<NamedAttemptAnswerOption> NamedAttemptAnswerOptions =>
        Set<NamedAttemptAnswerOption>();

    public DbSet<TestGradeChange> TestGradeChanges => Set<TestGradeChange>();

    public DbSet<Decision> Decisions => Set<Decision>();

    public DbSet<AdminAuditEvent> AdminAuditEvents => Set<AdminAuditEvent>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        ConfigureIdentityTables(builder);
    }

    private static void ConfigureIdentityTables(ModelBuilder builder)
    {
        builder.Entity<IdentityRole<Guid>>().ToTable("roles");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
    }
}
