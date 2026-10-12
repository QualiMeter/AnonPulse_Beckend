using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnonPulse.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakeVoteCreatedAtNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "created_at",
                table: "votes",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $EF$
                BEGIN
                    IF EXISTS (SELECT 1 FROM votes WHERE created_at IS NULL) THEN
                        RAISE EXCEPTION 'Cannot restore votes.created_at NOT NULL while anonymous votes without timestamps exist. Restore a pre-migration backup instead.';
                    END IF;
                END $EF$;
                """);

            migrationBuilder.AlterColumn<DateTime>(
                name: "created_at",
                table: "votes",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
