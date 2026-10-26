using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnonPulse.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTestModeAndNamedAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "polls",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "survey");

            migrationBuilder.AddColumn<int>(
                name: "test_scale",
                table: "polls",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "test_weight",
                table: "poll_questions",
                type: "numeric(10,2)",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_poll_options_question_id_id",
                table: "poll_options",
                columns: new[] { "question_id", "id" });

            migrationBuilder.CreateTable(
                name: "named_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    poll_id = table.Column<Guid>(type: "uuid", nullable: false),
                    respondent_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    respondent_group = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    client_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    access_secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    raw_score = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    maximum_score = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    normalized_score = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    is_fully_graded = table.Column<bool>(type: "boolean", nullable: false),
                    is_final = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_named_attempts", x => x.id);
                    table.CheckConstraint("ck_named_attempts_group", "btrim(respondent_group) <> ''");
                    table.CheckConstraint("ck_named_attempts_name", "btrim(respondent_name) <> ''");
                    table.CheckConstraint("ck_named_attempts_not_final", "is_final = false");
                    table.CheckConstraint("ck_named_attempts_request_hash", "length(request_hash) = 64");
                    table.CheckConstraint("ck_named_attempts_scores", "raw_score >= 0 AND maximum_score > 0 AND raw_score <= maximum_score AND (normalized_score IS NULL OR normalized_score >= 0)");
                    table.CheckConstraint("ck_named_attempts_secret_hash", "length(access_secret_hash) = 64");
                    table.CheckConstraint("ck_named_attempts_state", "state IN ('completed')");
                    table.ForeignKey(
                        name: "FK_named_attempts_polls_poll_id",
                        column: x => x.poll_id,
                        principalTable: "polls",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "poll_question_correct_options",
                columns: table => new
                {
                    question_id = table.Column<Guid>(type: "uuid", nullable: false),
                    option_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poll_question_correct_options", x => new { x.question_id, x.option_id });
                    table.ForeignKey(
                        name: "FK_poll_question_correct_options_poll_options_question_id_opti~",
                        columns: x => new { x.question_id, x.option_id },
                        principalTable: "poll_options",
                        principalColumns: new[] { "question_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_poll_question_correct_options_poll_questions_question_id",
                        column: x => x.question_id,
                        principalTable: "poll_questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "named_attempt_answers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "text", nullable: true),
                    grading_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    score = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    maximum_score = table.Column<decimal>(type: "numeric(10,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_named_attempt_answers", x => x.id);
                    table.CheckConstraint("ck_named_attempt_answers_grading_state", "grading_state IN ('auto_graded', 'pending')");
                    table.CheckConstraint("ck_named_attempt_answers_score", "maximum_score > 0 AND ((grading_state = 'auto_graded' AND score IS NOT NULL AND score >= 0 AND score <= maximum_score) OR (grading_state = 'pending' AND score IS NULL))");
                    table.CheckConstraint("ck_named_attempt_answers_text", "text IS NULL OR btrim(text) <> ''");
                    table.ForeignKey(
                        name: "FK_named_attempt_answers_named_attempts_attempt_id",
                        column: x => x.attempt_id,
                        principalTable: "named_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_named_attempt_answers_poll_questions_question_id",
                        column: x => x.question_id,
                        principalTable: "poll_questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "named_attempt_answer_options",
                columns: table => new
                {
                    answer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    option_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_named_attempt_answer_options", x => new { x.answer_id, x.option_id });
                    table.ForeignKey(
                        name: "FK_named_attempt_answer_options_named_attempt_answers_answer_id",
                        column: x => x.answer_id,
                        principalTable: "named_attempt_answers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_named_attempt_answer_options_poll_options_option_id",
                        column: x => x.option_id,
                        principalTable: "poll_options",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_polls_kind",
                table: "polls",
                sql: "kind IN ('survey', 'test')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_polls_test_configuration",
                table: "polls",
                sql: "(kind = 'survey' AND test_scale IS NULL) OR (kind = 'test' AND response_mode = 'identified' AND test_scale IN (5, 10, 12, 100))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_poll_questions_test_weight",
                table: "poll_questions",
                sql: "test_weight IS NULL OR test_weight > 0 AND test_weight <= 10000");

            migrationBuilder.CreateIndex(
                name: "IX_named_attempt_answer_options_option_id",
                table: "named_attempt_answer_options",
                column: "option_id");

            migrationBuilder.CreateIndex(
                name: "IX_named_attempt_answers_question_id",
                table: "named_attempt_answers",
                column: "question_id");

            migrationBuilder.CreateIndex(
                name: "ux_named_attempt_answers_attempt_id_question_id",
                table: "named_attempt_answers",
                columns: new[] { "attempt_id", "question_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_named_attempts_poll_id",
                table: "named_attempts",
                column: "poll_id");

            migrationBuilder.CreateIndex(
                name: "ux_named_attempts_poll_id_client_request_id",
                table: "named_attempts",
                columns: new[] { "poll_id", "client_request_id" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE FUNCTION validate_poll_test_configuration_change()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF (NEW.kind IS DISTINCT FROM OLD.kind OR
                        NEW.test_scale IS DISTINCT FROM OLD.test_scale)
                       AND OLD.status <> 'draft' THEN
                        RAISE EXCEPTION 'test configuration can be changed only for draft polls'
                            USING ERRCODE = '23514',
                                  CONSTRAINT = 'ck_polls_test_configuration_draft_only';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER trg_polls_test_configuration_draft_only
                BEFORE UPDATE OF kind, test_scale ON polls
                FOR EACH ROW
                EXECUTE FUNCTION validate_poll_test_configuration_change();

                CREATE FUNCTION validate_named_attempt_poll()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM polls
                        WHERE id = NEW.poll_id
                          AND kind = 'test'
                          AND response_mode = 'identified'
                          AND status = 'open'
                    ) THEN
                        RAISE EXCEPTION 'named attempts require an open identified test'
                            USING ERRCODE = '23514',
                                  CONSTRAINT = 'ck_named_attempt_poll';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER trg_named_attempt_poll
                BEFORE INSERT OR UPDATE OF poll_id ON named_attempts
                FOR EACH ROW
                EXECUTE FUNCTION validate_named_attempt_poll();

                CREATE FUNCTION validate_named_attempt_answer_poll()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    attempt_poll_id uuid;
                    question_poll_id uuid;
                BEGIN
                    SELECT poll_id INTO attempt_poll_id FROM named_attempts
                    WHERE id = NEW.attempt_id;
                    SELECT poll_id INTO question_poll_id FROM poll_questions
                    WHERE id = NEW.question_id;
                    IF attempt_poll_id IS DISTINCT FROM question_poll_id THEN
                        RAISE EXCEPTION 'answer and named attempt must belong to the same test'
                            USING ERRCODE = '23514',
                                  CONSTRAINT = 'ck_named_attempt_answer_poll';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER trg_named_attempt_answer_poll
                BEFORE INSERT OR UPDATE OF attempt_id, question_id ON named_attempt_answers
                FOR EACH ROW
                EXECUTE FUNCTION validate_named_attempt_answer_poll();

                CREATE FUNCTION validate_named_attempt_option_question()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    answer_question_id uuid;
                    option_question_id uuid;
                BEGIN
                    SELECT question_id INTO answer_question_id FROM named_attempt_answers
                    WHERE id = NEW.answer_id;
                    SELECT question_id INTO option_question_id FROM poll_options
                    WHERE id = NEW.option_id;
                    IF answer_question_id IS DISTINCT FROM option_question_id THEN
                        RAISE EXCEPTION 'selected option must belong to the answered question'
                            USING ERRCODE = '23514',
                                  CONSTRAINT = 'ck_named_attempt_option_question';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER trg_named_attempt_option_question
                BEFORE INSERT OR UPDATE OF answer_id, option_id ON named_attempt_answer_options
                FOR EACH ROW
                EXECUTE FUNCTION validate_named_attempt_option_question();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_named_attempt_option_question
                    ON named_attempt_answer_options;
                DROP FUNCTION IF EXISTS validate_named_attempt_option_question();
                DROP TRIGGER IF EXISTS trg_named_attempt_answer_poll
                    ON named_attempt_answers;
                DROP FUNCTION IF EXISTS validate_named_attempt_answer_poll();
                DROP TRIGGER IF EXISTS trg_named_attempt_poll ON named_attempts;
                DROP FUNCTION IF EXISTS validate_named_attempt_poll();
                DROP TRIGGER IF EXISTS trg_polls_test_configuration_draft_only ON polls;
                DROP FUNCTION IF EXISTS validate_poll_test_configuration_change();
                """);

            migrationBuilder.DropTable(
                name: "named_attempt_answer_options");

            migrationBuilder.DropTable(
                name: "poll_question_correct_options");

            migrationBuilder.DropTable(
                name: "named_attempt_answers");

            migrationBuilder.DropTable(
                name: "named_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_polls_kind",
                table: "polls");

            migrationBuilder.DropCheckConstraint(
                name: "ck_polls_test_configuration",
                table: "polls");

            migrationBuilder.DropCheckConstraint(
                name: "ck_poll_questions_test_weight",
                table: "poll_questions");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_poll_options_question_id_id",
                table: "poll_options");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "polls");

            migrationBuilder.DropColumn(
                name: "test_scale",
                table: "polls");

            migrationBuilder.DropColumn(
                name: "test_weight",
                table: "poll_questions");
        }
    }
}
