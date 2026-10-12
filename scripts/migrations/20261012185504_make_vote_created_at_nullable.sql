START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012185504_MakeVoteCreatedAtNullable') THEN
    ALTER TABLE votes ALTER COLUMN created_at DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012185504_MakeVoteCreatedAtNullable') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261012185504_MakeVoteCreatedAtNullable', '10.0.12');
    END IF;
END $EF$;
COMMIT;

