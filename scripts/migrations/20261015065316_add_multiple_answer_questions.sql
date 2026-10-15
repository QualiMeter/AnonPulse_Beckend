START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM "__EFMigrationsHistory"
        WHERE "MigrationId" = '20261015065316_AddMultipleAnswerQuestions'
    ) THEN
        ALTER TABLE poll_questions
            ADD COLUMN allows_multiple_answers boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM "__EFMigrationsHistory"
        WHERE "MigrationId" = '20261015065316_AddMultipleAnswerQuestions'
    ) THEN
        INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
        VALUES ('20261015065316_AddMultipleAnswerQuestions', '10.0.12');
    END IF;
END $EF$;

COMMIT;
