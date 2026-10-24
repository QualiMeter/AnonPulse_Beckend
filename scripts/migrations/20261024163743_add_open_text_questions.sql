START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024163743_AddOpenTextQuestions') THEN
    ALTER TABLE poll_questions ADD question_type character varying(32) NOT NULL DEFAULT 'single_choice';
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
    UPDATE poll_questions SET question_type = 'multiple_choice' WHERE allows_multiple_answers = TRUE;
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
        CONSTRAINT ck_poll_question_knowledge_settings_max_score CHECK (max_score BETWEEN 1 AND 10),
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

