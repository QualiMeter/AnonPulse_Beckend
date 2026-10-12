START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012163600_AddPollQuestionsAndStatusTransitions') THEN
    ALTER TABLE polls ADD title text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012163600_AddPollQuestionsAndStatusTransitions') THEN
    CREATE TABLE poll_questions (
        id uuid NOT NULL,
        poll_id uuid NOT NULL,
        text text NOT NULL,
        position integer NOT NULL,
        CONSTRAINT "PK_poll_questions" PRIMARY KEY (id),
        CONSTRAINT ck_poll_questions_position_nonnegative CHECK (position >= 0),
        CONSTRAINT ck_poll_questions_text_not_blank CHECK (btrim(text) <> ''),
        CONSTRAINT "FK_poll_questions_polls_poll_id" FOREIGN KEY (poll_id) REFERENCES polls (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012163600_AddPollQuestionsAndStatusTransitions') THEN
    INSERT INTO poll_questions (id, poll_id, text, position)
    SELECT id, id, question, 0
    FROM polls;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012163600_AddPollQuestionsAndStatusTransitions') THEN
    ALTER TABLE poll_options DROP CONSTRAINT "FK_poll_options_polls_poll_id";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012163600_AddPollQuestionsAndStatusTransitions') THEN
    ALTER TABLE poll_options RENAME COLUMN poll_id TO question_id;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012163600_AddPollQuestionsAndStatusTransitions') THEN
    ALTER INDEX "IX_poll_options_poll_id" RENAME TO "IX_poll_options_question_id";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012163600_AddPollQuestionsAndStatusTransitions') THEN
    CREATE UNIQUE INDEX ux_poll_options_question_id_position ON poll_options (question_id, position);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012163600_AddPollQuestionsAndStatusTransitions') THEN
    ALTER TABLE poll_options ADD CONSTRAINT ck_poll_options_position_nonnegative CHECK (position >= 0);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012163600_AddPollQuestionsAndStatusTransitions') THEN
    ALTER TABLE poll_options ADD CONSTRAINT ck_poll_options_text_not_blank CHECK (btrim(text) <> '');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012163600_AddPollQuestionsAndStatusTransitions') THEN
    CREATE UNIQUE INDEX ux_poll_options_question_id_normalized_text
    ON poll_options (question_id, lower(btrim(text)));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012163600_AddPollQuestionsAndStatusTransitions') THEN
    CREATE INDEX "IX_poll_questions_poll_id" ON poll_questions (poll_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012163600_AddPollQuestionsAndStatusTransitions') THEN
    CREATE UNIQUE INDEX ux_poll_questions_poll_id_position ON poll_questions (poll_id, position);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012163600_AddPollQuestionsAndStatusTransitions') THEN
    ALTER TABLE poll_options ADD CONSTRAINT "FK_poll_options_poll_questions_question_id" FOREIGN KEY (question_id) REFERENCES poll_questions (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012163600_AddPollQuestionsAndStatusTransitions') THEN
    ALTER TABLE polls DROP COLUMN question;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012163600_AddPollQuestionsAndStatusTransitions') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261012163600_AddPollQuestionsAndStatusTransitions', '10.0.12');
    END IF;
END $EF$;
COMMIT;

