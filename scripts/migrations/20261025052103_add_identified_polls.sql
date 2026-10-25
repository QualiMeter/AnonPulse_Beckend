START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025052103_AddIdentifiedPolls') THEN
    ALTER TABLE votes ADD identified_submission_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025052103_AddIdentifiedPolls') THEN
    ALTER TABLE polls ADD response_mode character varying(16) NOT NULL DEFAULT 'anonymous';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025052103_AddIdentifiedPolls') THEN
    ALTER TABLE poll_text_answers ADD identified_submission_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025052103_AddIdentifiedPolls') THEN
    CREATE TABLE identified_poll_submissions (
        id uuid NOT NULL,
        poll_id uuid NOT NULL,
        respondent_name character varying(150) NOT NULL,
        CONSTRAINT "PK_identified_poll_submissions" PRIMARY KEY (id),
        CONSTRAINT ck_identified_poll_submissions_respondent_name_not_blank CHECK (btrim(respondent_name) <> ''),
        CONSTRAINT fk_identified_poll_submissions_poll FOREIGN KEY (poll_id) REFERENCES polls (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025052103_AddIdentifiedPolls') THEN
    CREATE INDEX "IX_votes_identified_submission_id" ON votes (identified_submission_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025052103_AddIdentifiedPolls') THEN
    ALTER TABLE polls ADD CONSTRAINT ck_polls_response_mode CHECK (response_mode IN ('anonymous', 'identified'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025052103_AddIdentifiedPolls') THEN
    CREATE INDEX "IX_poll_text_answers_identified_submission_id" ON poll_text_answers (identified_submission_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025052103_AddIdentifiedPolls') THEN
    CREATE INDEX "IX_identified_poll_submissions_poll_id" ON identified_poll_submissions (poll_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025052103_AddIdentifiedPolls') THEN
    ALTER TABLE poll_text_answers ADD CONSTRAINT fk_poll_text_answers_identified_submission FOREIGN KEY (identified_submission_id) REFERENCES identified_poll_submissions (id) ON DELETE CASCADE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025052103_AddIdentifiedPolls') THEN
    ALTER TABLE votes ADD CONSTRAINT fk_votes_identified_submission FOREIGN KEY (identified_submission_id) REFERENCES identified_poll_submissions (id) ON DELETE CASCADE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025052103_AddIdentifiedPolls') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025052103_AddIdentifiedPolls') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261025052103_AddIdentifiedPolls', '10.0.12');
    END IF;
END $EF$;
COMMIT;

