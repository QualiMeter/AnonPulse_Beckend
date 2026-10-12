using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnonPulse.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPollQuestionsAndStatusTransitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "title",
                table: "polls",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "poll_questions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    poll_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poll_questions", x => x.id);
                    table.CheckConstraint("ck_poll_questions_position_nonnegative", "position >= 0");
                    table.CheckConstraint("ck_poll_questions_text_not_blank", "btrim(text) <> ''");
                    table.ForeignKey(
                        name: "FK_poll_questions_polls_poll_id",
                        column: x => x.poll_id,
                        principalTable: "polls",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO poll_questions (id, poll_id, text, position)
                SELECT id, id, question, 0
                FROM polls;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_poll_options_polls_poll_id",
                table: "poll_options");

            migrationBuilder.RenameColumn(
                name: "poll_id",
                table: "poll_options",
                newName: "question_id");

            migrationBuilder.RenameIndex(
                name: "IX_poll_options_poll_id",
                table: "poll_options",
                newName: "IX_poll_options_question_id");

            migrationBuilder.CreateIndex(
                name: "ux_poll_options_question_id_position",
                table: "poll_options",
                columns: new[] { "question_id", "position" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_poll_options_position_nonnegative",
                table: "poll_options",
                sql: "position >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_poll_options_text_not_blank",
                table: "poll_options",
                sql: "btrim(text) <> ''");

            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX ux_poll_options_question_id_normalized_text
                ON poll_options (question_id, lower(btrim(text)));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_poll_questions_poll_id",
                table: "poll_questions",
                column: "poll_id");

            migrationBuilder.CreateIndex(
                name: "ux_poll_questions_poll_id_position",
                table: "poll_questions",
                columns: new[] { "poll_id", "position" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_poll_options_poll_questions_question_id",
                table: "poll_options",
                column: "question_id",
                principalTable: "poll_questions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropColumn(
                name: "question",
                table: "polls");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $EF$
                BEGIN
                    IF EXISTS (
                        SELECT p.id
                        FROM polls AS p
                        LEFT JOIN poll_questions AS q ON q.poll_id = p.id
                        GROUP BY p.id
                        HAVING COUNT(q.id) <> 1
                    ) THEN
                        RAISE EXCEPTION 'Cannot downgrade polls containing multiple questions. Restore the pre-migration backup instead.';
                    END IF;
                END $EF$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_poll_options_poll_questions_question_id",
                table: "poll_options");

            migrationBuilder.DropIndex(
                name: "ux_poll_options_question_id_position",
                table: "poll_options");

            migrationBuilder.DropCheckConstraint(
                name: "ck_poll_options_position_nonnegative",
                table: "poll_options");

            migrationBuilder.DropCheckConstraint(
                name: "ck_poll_options_text_not_blank",
                table: "poll_options");

            migrationBuilder.DropIndex(
                name: "ux_poll_options_question_id_normalized_text",
                table: "poll_options");

            migrationBuilder.AddColumn<string>(
                name: "question",
                table: "polls",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "poll_id",
                table: "poll_options",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE polls AS p
                SET question = q.text
                FROM poll_questions AS q
                WHERE q.poll_id = p.id AND q.position = 0;

                UPDATE poll_options AS o
                SET poll_id = q.poll_id
                FROM poll_questions AS q
                WHERE q.id = o.question_id;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "question",
                table: "polls",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "poll_id",
                table: "poll_options",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.DropIndex(
                name: "IX_poll_options_question_id",
                table: "poll_options");

            migrationBuilder.DropColumn(
                name: "question_id",
                table: "poll_options");

            migrationBuilder.DropTable(
                name: "poll_questions");

            migrationBuilder.DropColumn(
                name: "title",
                table: "polls");

            migrationBuilder.CreateIndex(
                name: "IX_poll_options_poll_id",
                table: "poll_options",
                column: "poll_id");

            migrationBuilder.AddForeignKey(
                name: "FK_poll_options_polls_poll_id",
                table: "poll_options",
                column: "poll_id",
                principalTable: "polls",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
