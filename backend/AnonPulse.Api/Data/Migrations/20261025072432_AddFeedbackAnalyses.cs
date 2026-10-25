using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnonPulse.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedbackAnalyses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "poll_feedback_analyses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    poll_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    algorithm_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    answer_count = table.Column<int>(type: "integer", nullable: false),
                    answer_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    report = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: true),
                    completion_tokens = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poll_feedback_analyses", x => x.id);
                    table.CheckConstraint("ck_poll_feedback_analyses_answer_count", "answer_count > 0");
                    table.CheckConstraint("ck_poll_feedback_analyses_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_poll_feedback_analyses_status", "status IN ('pending', 'processing', 'completed', 'failed')");
                    table.ForeignKey(
                        name: "FK_poll_feedback_analyses_poll_questions_question_id",
                        column: x => x.question_id,
                        principalTable: "poll_questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_poll_feedback_analyses_polls_poll_id",
                        column: x => x.poll_id,
                        principalTable: "polls",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_feedback_analyses_queue",
                table: "poll_feedback_analyses",
                columns: new[] { "status", "next_attempt_at", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_poll_feedback_analyses_poll_id",
                table: "poll_feedback_analyses",
                column: "poll_id");

            migrationBuilder.CreateIndex(
                name: "ux_feedback_analyses_question_fingerprint",
                table: "poll_feedback_analyses",
                columns: new[] { "question_id", "answer_fingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "poll_feedback_analyses");
        }
    }
}
