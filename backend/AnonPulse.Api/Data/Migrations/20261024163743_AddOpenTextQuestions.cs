using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnonPulse.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenTextQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "question_type",
                table: "poll_questions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "single_choice");

            migrationBuilder.AddColumn<string>(
                name: "text_purpose",
                table: "poll_questions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE poll_questions SET question_type = 'multiple_choice' " +
                "WHERE allows_multiple_answers = TRUE;");

            migrationBuilder.CreateTable(
                name: "poll_question_knowledge_settings",
                columns: table => new
                {
                    question_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_answer = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    grading_criteria = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    max_score = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poll_question_knowledge_settings", x => x.question_id);
                    table.CheckConstraint("ck_poll_question_knowledge_settings_expected_answer", "btrim(expected_answer) <> ''");
                    table.CheckConstraint("ck_poll_question_knowledge_settings_grading_criteria", "btrim(grading_criteria) <> ''");
                    table.CheckConstraint("ck_poll_question_knowledge_settings_max_score", "max_score BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "FK_poll_question_knowledge_settings_poll_questions_question_id",
                        column: x => x.question_id,
                        principalTable: "poll_questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "poll_text_answers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poll_text_answers", x => x.id);
                    table.CheckConstraint("ck_poll_text_answers_text_not_blank", "btrim(text) <> ''");
                    table.ForeignKey(
                        name: "FK_poll_text_answers_poll_questions_question_id",
                        column: x => x.question_id,
                        principalTable: "poll_questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_poll_questions_text_purpose",
                table: "poll_questions",
                sql: "(question_type = 'text' AND text_purpose IN ('feedback', 'knowledge')) OR (question_type <> 'text' AND text_purpose IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_poll_questions_type",
                table: "poll_questions",
                sql: "question_type IN ('single_choice', 'multiple_choice', 'text')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_poll_questions_type_legacy_consistent",
                table: "poll_questions",
                sql: "(question_type = 'multiple_choice') = allows_multiple_answers");

            migrationBuilder.CreateIndex(
                name: "IX_poll_text_answers_question_id",
                table: "poll_text_answers",
                column: "question_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "poll_question_knowledge_settings");

            migrationBuilder.DropTable(
                name: "poll_text_answers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_poll_questions_text_purpose",
                table: "poll_questions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_poll_questions_type",
                table: "poll_questions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_poll_questions_type_legacy_consistent",
                table: "poll_questions");

            migrationBuilder.DropColumn(
                name: "question_type",
                table: "poll_questions");

            migrationBuilder.DropColumn(
                name: "text_purpose",
                table: "poll_questions");
        }
    }
}
