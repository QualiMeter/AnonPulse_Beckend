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
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "text_purpose",
                table: "poll_questions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE poll_questions
                SET question_type = CASE
                    WHEN allows_multiple_answers THEN 'multiple_choice'
                    ELSE 'single_choice'
                END;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "question_type",
                table: "poll_questions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.Sql("""
                CREATE FUNCTION set_poll_question_type_for_legacy_writes()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF TG_NAME = 'trg_poll_questions_10_validate_explicit_type_update' THEN
                        IF ((NEW.question_type = 'multiple_choice') IS DISTINCT FROM NEW.allows_multiple_answers) THEN
                            RAISE EXCEPTION 'question_type and allows_multiple_answers are inconsistent'
                                USING ERRCODE = '23514',
                                      CONSTRAINT = 'ck_poll_questions_type_legacy_consistent';
                        END IF;

                        RETURN NEW;
                    END IF;

                    IF TG_OP = 'INSERT' AND NEW.question_type IS NULL THEN
                        NEW.question_type := CASE
                            WHEN NEW.allows_multiple_answers THEN 'multiple_choice'
                            ELSE 'single_choice'
                        END;
                    ELSIF TG_OP = 'UPDATE'
                        AND NEW.allows_multiple_answers IS DISTINCT FROM OLD.allows_multiple_answers
                        AND NEW.question_type IS NOT DISTINCT FROM OLD.question_type THEN
                        NEW.question_type := CASE
                            WHEN NEW.allows_multiple_answers THEN 'multiple_choice'
                            ELSE 'single_choice'
                        END;
                    END IF;

                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER trg_poll_questions_10_validate_explicit_type_update
                BEFORE UPDATE OF question_type ON poll_questions
                FOR EACH ROW
                EXECUTE FUNCTION set_poll_question_type_for_legacy_writes();

                CREATE TRIGGER trg_poll_questions_20_legacy_insert_type
                BEFORE INSERT ON poll_questions
                FOR EACH ROW
                EXECUTE FUNCTION set_poll_question_type_for_legacy_writes();

                CREATE TRIGGER trg_poll_questions_20_legacy_update_type
                BEFORE UPDATE OF allows_multiple_answers ON poll_questions
                FOR EACH ROW
                EXECUTE FUNCTION set_poll_question_type_for_legacy_writes();
                """);

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
                    table.CheckConstraint("ck_poll_question_knowledge_settings_max_score", "max_score BETWEEN 1 AND 100");
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
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_poll_questions_10_validate_explicit_type_update ON poll_questions;
                DROP TRIGGER IF EXISTS trg_poll_questions_20_legacy_insert_type ON poll_questions;
                DROP TRIGGER IF EXISTS trg_poll_questions_20_legacy_update_type ON poll_questions;
                DROP FUNCTION IF EXISTS set_poll_question_type_for_legacy_writes();
                """);

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
