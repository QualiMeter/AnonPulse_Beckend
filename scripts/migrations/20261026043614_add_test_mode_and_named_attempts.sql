START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026043614_AddTestModeAndNamedAttempts') THEN
    ALTER TABLE polls ADD kind character varying(16) NOT NULL DEFAULT 'survey';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026043614_AddTestModeAndNamedAttempts') THEN
    ALTER TABLE polls ADD test_scale integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026043614_AddTestModeAndNamedAttempts') THEN
    ALTER TABLE poll_questions ADD test_weight numeric(10,2);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026043614_AddTestModeAndNamedAttempts') THEN
    ALTER TABLE poll_options ADD CONSTRAINT ak_poll_options_question_id_id UNIQUE (question_id, id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026043614_AddTestModeAndNamedAttempts') THEN
    CREATE TABLE named_attempts (
        id uuid NOT NULL,
        poll_id uuid NOT NULL,
        respondent_name character varying(150) NOT NULL,
        respondent_group character varying(100) NOT NULL,
        client_request_id uuid NOT NULL,
        request_hash character varying(64) NOT NULL,
        access_secret_hash character varying(64) NOT NULL,
        state character varying(16) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        completed_at timestamp with time zone NOT NULL,
        raw_score numeric(10,2) NOT NULL,
        maximum_score numeric(10,2) NOT NULL,
        normalized_score numeric(10,2),
        is_fully_graded boolean NOT NULL,
        is_final boolean NOT NULL DEFAULT FALSE,
        CONSTRAINT "PK_named_attempts" PRIMARY KEY (id),
        CONSTRAINT ck_named_attempts_group CHECK (btrim(respondent_group) <> ''),
        CONSTRAINT ck_named_attempts_name CHECK (btrim(respondent_name) <> ''),
        CONSTRAINT ck_named_attempts_not_final CHECK (is_final = false),
        CONSTRAINT ck_named_attempts_request_hash CHECK (length(request_hash) = 64),
        CONSTRAINT ck_named_attempts_scores CHECK (raw_score >= 0 AND maximum_score > 0 AND raw_score <= maximum_score AND (normalized_score IS NULL OR normalized_score >= 0)),
        CONSTRAINT ck_named_attempts_secret_hash CHECK (length(access_secret_hash) = 64),
        CONSTRAINT ck_named_attempts_state CHECK (state IN ('completed')),
        CONSTRAINT "FK_named_attempts_polls_poll_id" FOREIGN KEY (poll_id) REFERENCES polls (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026043614_AddTestModeAndNamedAttempts') THEN
    CREATE TABLE poll_question_correct_options (
        question_id uuid NOT NULL,
        option_id uuid NOT NULL,
        CONSTRAINT "PK_poll_question_correct_options" PRIMARY KEY (question_id, option_id),
        CONSTRAINT "FK_poll_question_correct_options_poll_options_question_id_opti~" FOREIGN KEY (question_id, option_id) REFERENCES poll_options (question_id, id) ON DELETE RESTRICT,
        CONSTRAINT "FK_poll_question_correct_options_poll_questions_question_id" FOREIGN KEY (question_id) REFERENCES poll_questions (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026043614_AddTestModeAndNamedAttempts') THEN
    CREATE TABLE named_attempt_answers (
        id uuid NOT NULL,
        attempt_id uuid NOT NULL,
        question_id uuid NOT NULL,
        text text,
        grading_state character varying(16) NOT NULL,
        score numeric(10,2),
        maximum_score numeric(10,2) NOT NULL,
        CONSTRAINT "PK_named_attempt_answers" PRIMARY KEY (id),
        CONSTRAINT ck_named_attempt_answers_grading_state CHECK (grading_state IN ('auto_graded', 'pending')),
        CONSTRAINT ck_named_attempt_answers_score CHECK (maximum_score > 0 AND ((grading_state = 'auto_graded' AND score IS NOT NULL AND score >= 0 AND score <= maximum_score) OR (grading_state = 'pending' AND score IS NULL))),
        CONSTRAINT ck_named_attempt_answers_text CHECK (text IS NULL OR btrim(text) <> ''),
        CONSTRAINT "FK_named_attempt_answers_named_attempts_attempt_id" FOREIGN KEY (attempt_id) REFERENCES named_attempts (id) ON DELETE CASCADE,
        CONSTRAINT "FK_named_attempt_answers_poll_questions_question_id" FOREIGN KEY (question_id) REFERENCES poll_questions (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026043614_AddTestModeAndNamedAttempts') THEN
    CREATE TABLE named_attempt_answer_options (
        answer_id uuid NOT NULL,
        option_id uuid NOT NULL,
        CONSTRAINT "PK_named_attempt_answer_options" PRIMARY KEY (answer_id, option_id),
        CONSTRAINT "FK_named_attempt_answer_options_named_attempt_answers_answer_id" FOREIGN KEY (answer_id) REFERENCES named_attempt_answers (id) ON DELETE CASCADE,
        CONSTRAINT "FK_named_attempt_answer_options_poll_options_option_id" FOREIGN KEY (option_id) REFERENCES poll_options (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026043614_AddTestModeAndNamedAttempts') THEN
    ALTER TABLE polls ADD CONSTRAINT ck_polls_kind CHECK (kind IN ('survey', 'test'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026043614_AddTestModeAndNamedAttempts') THEN
    ALTER TABLE polls ADD CONSTRAINT ck_polls_test_configuration CHECK ((kind = 'survey' AND test_scale IS NULL) OR (kind = 'test' AND response_mode = 'identified' AND test_scale IN (5, 10, 12, 100)));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026043614_AddTestModeAndNamedAttempts') THEN
    ALTER TABLE poll_questions ADD CONSTRAINT ck_poll_questions_test_weight CHECK (test_weight IS NULL OR test_weight > 0 AND test_weight <= 10000);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026043614_AddTestModeAndNamedAttempts') THEN
    CREATE INDEX "IX_named_attempt_answer_options_option_id" ON named_attempt_answer_options (option_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026043614_AddTestModeAndNamedAttempts') THEN
    CREATE INDEX "IX_named_attempt_answers_question_id" ON named_attempt_answers (question_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026043614_AddTestModeAndNamedAttempts') THEN
    CREATE UNIQUE INDEX ux_named_attempt_answers_attempt_id_question_id ON named_attempt_answers (attempt_id, question_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026043614_AddTestModeAndNamedAttempts') THEN
    CREATE INDEX "IX_named_attempts_poll_id" ON named_attempts (poll_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026043614_AddTestModeAndNamedAttempts') THEN
    CREATE UNIQUE INDEX ux_named_attempts_poll_id_client_request_id ON named_attempts (poll_id, client_request_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026043614_AddTestModeAndNamedAttempts') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026043614_AddTestModeAndNamedAttempts') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261026043614_AddTestModeAndNamedAttempts', '10.0.12');
    END IF;
END $EF$;
COMMIT;

