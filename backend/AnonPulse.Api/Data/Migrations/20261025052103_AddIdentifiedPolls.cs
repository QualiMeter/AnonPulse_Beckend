using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnonPulse.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentifiedPolls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "identified_submission_id",
                table: "votes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "response_mode",
                table: "polls",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "anonymous");

            migrationBuilder.AddColumn<Guid>(
                name: "identified_submission_id",
                table: "poll_text_answers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "identified_poll_submissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    poll_id = table.Column<Guid>(type: "uuid", nullable: false),
                    respondent_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identified_poll_submissions", x => x.id);
                    table.CheckConstraint("ck_identified_poll_submissions_respondent_name_not_blank", "btrim(respondent_name) <> ''");
                    table.ForeignKey(
                        name: "fk_identified_poll_submissions_poll",
                        column: x => x.poll_id,
                        principalTable: "polls",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_votes_identified_submission_id",
                table: "votes",
                column: "identified_submission_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_polls_response_mode",
                table: "polls",
                sql: "response_mode IN ('anonymous', 'identified')");

            migrationBuilder.CreateIndex(
                name: "IX_poll_text_answers_identified_submission_id",
                table: "poll_text_answers",
                column: "identified_submission_id");

            migrationBuilder.CreateIndex(
                name: "IX_identified_poll_submissions_poll_id",
                table: "identified_poll_submissions",
                column: "poll_id");

            migrationBuilder.AddForeignKey(
                name: "fk_poll_text_answers_identified_submission",
                table: "poll_text_answers",
                column: "identified_submission_id",
                principalTable: "identified_poll_submissions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_votes_identified_submission",
                table: "votes",
                column: "identified_submission_id",
                principalTable: "identified_poll_submissions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.Sql("""
                CREATE FUNCTION validate_poll_response_mode_change()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF NEW.response_mode IS DISTINCT FROM OLD.response_mode
                       AND OLD.status <> 'draft' THEN
                        RAISE EXCEPTION 'response_mode can be changed only for draft polls'
                            USING ERRCODE = '23514',
                                  CONSTRAINT = 'ck_polls_response_mode_draft_only';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER trg_polls_response_mode_draft_only
                BEFORE UPDATE OF response_mode ON polls
                FOR EACH ROW
                EXECUTE FUNCTION validate_poll_response_mode_change();

                CREATE FUNCTION validate_identified_submission_mode()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM polls
                        WHERE id = NEW.poll_id AND response_mode = 'identified'
                    ) THEN
                        RAISE EXCEPTION 'identified submissions require an identified poll'
                            USING ERRCODE = '23514',
                                  CONSTRAINT = 'ck_identified_submission_poll_mode';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER trg_identified_submission_mode
                BEFORE INSERT OR UPDATE OF poll_id ON identified_poll_submissions
                FOR EACH ROW
                EXECUTE FUNCTION validate_identified_submission_mode();

                CREATE FUNCTION validate_identified_answer_poll()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    submission_poll_id uuid;
                    answer_poll_id uuid;
                BEGIN
                    IF NEW.identified_submission_id IS NULL THEN
                        RETURN NEW;
                    END IF;

                    SELECT poll_id INTO submission_poll_id
                    FROM identified_poll_submissions
                    WHERE id = NEW.identified_submission_id;

                    IF TG_TABLE_NAME = 'votes' THEN
                        SELECT q.poll_id INTO answer_poll_id
                        FROM poll_options o
                        JOIN poll_questions q ON q.id = o.question_id
                        WHERE o.id = NEW.option_id;
                    ELSE
                        SELECT poll_id INTO answer_poll_id
                        FROM poll_questions
                        WHERE id = NEW.question_id;
                    END IF;

                    IF submission_poll_id IS DISTINCT FROM answer_poll_id THEN
                        RAISE EXCEPTION 'answer and identified submission must belong to the same poll'
                            USING ERRCODE = '23514',
                                  CONSTRAINT = 'ck_identified_answer_submission_poll';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER trg_votes_identified_submission_poll
                BEFORE INSERT OR UPDATE OF option_id, identified_submission_id ON votes
                FOR EACH ROW
                EXECUTE FUNCTION validate_identified_answer_poll();

                CREATE TRIGGER trg_text_answers_identified_submission_poll
                BEFORE INSERT OR UPDATE OF question_id, identified_submission_id ON poll_text_answers
                FOR EACH ROW
                EXECUTE FUNCTION validate_identified_answer_poll();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_text_answers_identified_submission_poll
                    ON poll_text_answers;
                DROP TRIGGER IF EXISTS trg_votes_identified_submission_poll ON votes;
                DROP FUNCTION IF EXISTS validate_identified_answer_poll();
                DROP TRIGGER IF EXISTS trg_identified_submission_mode
                    ON identified_poll_submissions;
                DROP FUNCTION IF EXISTS validate_identified_submission_mode();
                DROP TRIGGER IF EXISTS trg_polls_response_mode_draft_only ON polls;
                DROP FUNCTION IF EXISTS validate_poll_response_mode_change();
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_poll_text_answers_identified_submission",
                table: "poll_text_answers");

            migrationBuilder.DropForeignKey(
                name: "fk_votes_identified_submission",
                table: "votes");

            migrationBuilder.DropTable(
                name: "identified_poll_submissions");

            migrationBuilder.DropIndex(
                name: "IX_votes_identified_submission_id",
                table: "votes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_polls_response_mode",
                table: "polls");

            migrationBuilder.DropIndex(
                name: "IX_poll_text_answers_identified_submission_id",
                table: "poll_text_answers");

            migrationBuilder.DropColumn(
                name: "identified_submission_id",
                table: "votes");

            migrationBuilder.DropColumn(
                name: "response_mode",
                table: "polls");

            migrationBuilder.DropColumn(
                name: "identified_submission_id",
                table: "poll_text_answers");
        }
    }
}
