using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnonPulse.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeGrading : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "poll_knowledge_grading_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    poll_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    algorithm_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    snapshot_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expected_answer_snapshot = table.Column<string>(type: "text", nullable: false),
                    grading_criteria_snapshot = table.Column<string>(type: "text", nullable: false),
                    max_score_snapshot = table.Column<int>(type: "integer", nullable: false),
                    answers_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    answer_count = table.Column<int>(type: "integer", nullable: false),
                    suggested_count = table.Column<int>(type: "integer", nullable: false),
                    reserved_input_tokens = table.Column<int>(type: "integer", nullable: false),
                    reserved_output_tokens = table.Column<int>(type: "integer", nullable: false),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: true),
                    completion_tokens = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poll_knowledge_grading_jobs", x => x.id);
                    table.CheckConstraint("ck_knowledge_grading_jobs_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_knowledge_grading_jobs_budget", "reserved_input_tokens > 0 AND reserved_output_tokens > 0");
                    table.CheckConstraint("ck_knowledge_grading_jobs_counts", "answer_count > 0 AND suggested_count >= 0 AND suggested_count <= answer_count");
                    table.CheckConstraint("ck_knowledge_grading_jobs_score", "max_score_snapshot BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_knowledge_grading_jobs_status", "status IN ('pending', 'processing', 'completed', 'failed')");
                    table.ForeignKey(
                        name: "FK_poll_knowledge_grading_jobs_poll_questions_question_id",
                        column: x => x.question_id,
                        principalTable: "poll_questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_poll_knowledge_grading_jobs_polls_poll_id",
                        column: x => x.poll_id,
                        principalTable: "polls",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "poll_knowledge_grading_suggestions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    answer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    suggested_score = table.Column<int>(type: "integer", nullable: false),
                    max_score = table.Column<int>(type: "integer", nullable: false),
                    explanation = table.Column<string>(type: "text", nullable: false),
                    correct_points = table.Column<string>(type: "jsonb", nullable: false),
                    missing_or_incorrect_points = table.Column<string>(type: "jsonb", nullable: false),
                    needs_review = table.Column<bool>(type: "boolean", nullable: false),
                    review_reasons = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poll_knowledge_grading_suggestions", x => x.id);
                    table.CheckConstraint("ck_knowledge_grading_suggestions_explanation", "btrim(explanation) <> ''");
                    table.CheckConstraint("ck_knowledge_grading_suggestions_score", "max_score BETWEEN 1 AND 100 AND suggested_score BETWEEN 0 AND max_score");
                    table.ForeignKey(
                        name: "FK_poll_knowledge_grading_suggestions_named_attempt_answers_an~",
                        column: x => x.answer_id,
                        principalTable: "named_attempt_answers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_poll_knowledge_grading_suggestions_poll_knowledge_grading_j~",
                        column: x => x.job_id,
                        principalTable: "poll_knowledge_grading_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_grading_queue",
                table: "poll_knowledge_grading_jobs",
                columns: new[] { "status", "next_attempt_at", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_poll_knowledge_grading_jobs_poll_id",
                table: "poll_knowledge_grading_jobs",
                column: "poll_id");

            migrationBuilder.CreateIndex(
                name: "ux_knowledge_grading_question_fingerprint",
                table: "poll_knowledge_grading_jobs",
                columns: new[] { "question_id", "snapshot_fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_poll_knowledge_grading_suggestions_answer_id",
                table: "poll_knowledge_grading_suggestions",
                column: "answer_id");

            migrationBuilder.CreateIndex(
                name: "ux_knowledge_grading_suggestions_job_answer",
                table: "poll_knowledge_grading_suggestions",
                columns: new[] { "job_id", "answer_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "poll_knowledge_grading_suggestions");

            migrationBuilder.DropTable(
                name: "poll_knowledge_grading_jobs");
        }
    }
}
