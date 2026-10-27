using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnonPulse.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class FinalizeTestGrades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_named_attempts_not_final",
                table: "named_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_named_attempt_answers_grading_state",
                table: "named_attempt_answers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_named_attempt_answers_score",
                table: "named_attempt_answers");

            migrationBuilder.AddColumn<DateTime>(
                name: "finalized_at",
                table: "named_attempts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "finalized_by_user_id",
                table: "named_attempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_named_attempt_answers_attempt_id_id",
                table: "named_attempt_answers",
                columns: new[] { "attempt_id", "id" });

            migrationBuilder.CreateTable(
                name: "test_grade_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    answer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    old_score = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    new_score = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    knowledge_grading_suggestion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_grade_changes", x => x.id);
                    table.CheckConstraint("ck_test_grade_changes_scores", "(old_score IS NULL OR old_score >= 0) AND new_score >= 0");
                    table.CheckConstraint("ck_test_grade_changes_source", "source IN ('manual', 'ai_suggestion') AND ((source = 'manual' AND knowledge_grading_suggestion_id IS NULL) OR (source = 'ai_suggestion' AND knowledge_grading_suggestion_id IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_test_grade_changes_named_attempt_answers_attempt_id_answer_~",
                        columns: x => new { x.attempt_id, x.answer_id },
                        principalTable: "named_attempt_answers",
                        principalColumns: new[] { "attempt_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_test_grade_changes_named_attempts_attempt_id",
                        column: x => x.attempt_id,
                        principalTable: "named_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_test_grade_changes_poll_knowledge_grading_suggestions_knowl~",
                        column: x => x.knowledge_grading_suggestion_id,
                        principalTable: "poll_knowledge_grading_suggestions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_named_attempts_finalization",
                table: "named_attempts",
                sql: "(is_final = false AND finalized_by_user_id IS NULL AND finalized_at IS NULL) OR (is_final = true AND is_fully_graded = true AND normalized_score IS NOT NULL AND finalized_by_user_id IS NOT NULL AND finalized_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_named_attempt_answers_grading_state",
                table: "named_attempt_answers",
                sql: "grading_state IN ('auto_graded', 'pending', 'teacher_graded')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_named_attempt_answers_score",
                table: "named_attempt_answers",
                sql: "maximum_score > 0 AND ((grading_state IN ('auto_graded', 'teacher_graded') AND score IS NOT NULL AND score >= 0 AND score <= maximum_score) OR (grading_state = 'pending' AND score IS NULL))");

            migrationBuilder.CreateIndex(
                name: "ix_test_grade_changes_answer_id",
                table: "test_grade_changes",
                column: "answer_id");

            migrationBuilder.CreateIndex(
                name: "ix_test_grade_changes_attempt_changed_at",
                table: "test_grade_changes",
                columns: new[] { "attempt_id", "changed_at" });

            migrationBuilder.CreateIndex(
                name: "IX_test_grade_changes_attempt_id_answer_id",
                table: "test_grade_changes",
                columns: new[] { "attempt_id", "answer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_test_grade_changes_suggestion_id",
                table: "test_grade_changes",
                column: "knowledge_grading_suggestion_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "test_grade_changes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_named_attempts_finalization",
                table: "named_attempts");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_named_attempt_answers_attempt_id_id",
                table: "named_attempt_answers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_named_attempt_answers_grading_state",
                table: "named_attempt_answers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_named_attempt_answers_score",
                table: "named_attempt_answers");

            migrationBuilder.DropColumn(
                name: "finalized_at",
                table: "named_attempts");

            migrationBuilder.DropColumn(
                name: "finalized_by_user_id",
                table: "named_attempts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_named_attempts_not_final",
                table: "named_attempts",
                sql: "is_final = false");

            migrationBuilder.AddCheckConstraint(
                name: "ck_named_attempt_answers_grading_state",
                table: "named_attempt_answers",
                sql: "grading_state IN ('auto_graded', 'pending')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_named_attempt_answers_score",
                table: "named_attempt_answers",
                sql: "maximum_score > 0 AND ((grading_state = 'auto_graded' AND score IS NOT NULL AND score >= 0 AND score <= maximum_score) OR (grading_state = 'pending' AND score IS NULL))");
        }
    }
}
