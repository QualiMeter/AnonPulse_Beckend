START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024163743_AddOpenTextQuestions') THEN
    ALTER TABLE poll_questions ADD question_type character varying(32);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024163743_AddOpenTextQuestions') THEN
    ALTER TABLE poll_questions ADD text_purpose character varying(16);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024163743_AddOpenTextQuestions') THEN
    UPDATE poll_questions
    SET question_type = CASE
        WHEN allows_multiple_answers THEN 'multiple_choice'
        ELSE 'single_choice'
    END;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024163743_AddOpenTextQuestions') THEN
    ALTER TABLE poll_questions ALTER COLUMN question_type SET NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024163743_AddOpenTextQuestions') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024163743_AddOpenTextQuestions') THEN
    CREATE TABLE poll_question_knowledge_settings (
        question_id uuid NOT NULL,
        expected_answer character varying(4000) NOT NULL,
        grading_criteria character varying(4000) NOT NULL,
        max_score integer NOT NULL,
        CONSTRAINT "PK_poll_question_knowledge_settings" PRIMARY KEY (question_id),
        CONSTRAINT ck_poll_question_knowledge_settings_expected_answer CHECK (btrim(expected_answer) <> ''),
        CONSTRAINT ck_poll_question_knowledge_settings_grading_criteria CHECK (btrim(grading_criteria) <> ''),
        CONSTRAINT ck_poll_question_knowledge_settings_max_score CHECK (max_score BETWEEN 1 AND 100),
        CONSTRAINT "FK_poll_question_knowledge_settings_poll_questions_question_id" FOREIGN KEY (question_id) REFERENCES poll_questions (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024163743_AddOpenTextQuestions') THEN
    CREATE TABLE poll_text_answers (
        id uuid NOT NULL,
        question_id uuid NOT NULL,
        text character varying(4000) NOT NULL,
        CONSTRAINT "PK_poll_text_answers" PRIMARY KEY (id),
        CONSTRAINT ck_poll_text_answers_text_not_blank CHECK (btrim(text) <> ''),
        CONSTRAINT "FK_poll_text_answers_poll_questions_question_id" FOREIGN KEY (question_id) REFERENCES poll_questions (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024163743_AddOpenTextQuestions') THEN
    ALTER TABLE poll_questions ADD CONSTRAINT ck_poll_questions_text_purpose CHECK ((question_type = 'text' AND text_purpose IN ('feedback', 'knowledge')) OR (question_type <> 'text' AND text_purpose IS NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024163743_AddOpenTextQuestions') THEN
    ALTER TABLE poll_questions ADD CONSTRAINT ck_poll_questions_type CHECK (question_type IN ('single_choice', 'multiple_choice', 'text'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024163743_AddOpenTextQuestions') THEN
    ALTER TABLE poll_questions ADD CONSTRAINT ck_poll_questions_type_legacy_consistent CHECK ((question_type = 'multiple_choice') = allows_multiple_answers);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024163743_AddOpenTextQuestions') THEN
    CREATE INDEX "IX_poll_text_answers_question_id" ON poll_text_answers (question_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024163743_AddOpenTextQuestions') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261024163743_AddOpenTextQuestions', '10.0.12');
    END IF;
END $EF$;
COMMIT;

