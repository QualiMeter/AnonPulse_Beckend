START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025072432_AddFeedbackAnalyses') THEN
    CREATE TABLE poll_feedback_analyses (
        id uuid NOT NULL,
        poll_id uuid NOT NULL,
        question_id uuid NOT NULL,
        status character varying(16) NOT NULL,
        provider character varying(32) NOT NULL,
        model character varying(128) NOT NULL,
        algorithm_version character varying(32) NOT NULL,
        answer_count integer NOT NULL,
        answer_fingerprint character varying(64) NOT NULL,
        report jsonb,
        created_at timestamp with time zone NOT NULL,
        started_at timestamp with time zone,
        completed_at timestamp with time zone,
        next_attempt_at timestamp with time zone,
        attempts integer NOT NULL,
        error_code character varying(64),
        prompt_tokens integer,
        completion_tokens integer,
        CONSTRAINT "PK_poll_feedback_analyses" PRIMARY KEY (id),
        CONSTRAINT ck_poll_feedback_analyses_answer_count CHECK (answer_count > 0),
        CONSTRAINT ck_poll_feedback_analyses_attempts CHECK (attempts >= 0),
        CONSTRAINT ck_poll_feedback_analyses_status CHECK (status IN ('pending', 'processing', 'completed', 'failed')),
        CONSTRAINT "FK_poll_feedback_analyses_poll_questions_question_id" FOREIGN KEY (question_id) REFERENCES poll_questions (id) ON DELETE CASCADE,
        CONSTRAINT "FK_poll_feedback_analyses_polls_poll_id" FOREIGN KEY (poll_id) REFERENCES polls (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025072432_AddFeedbackAnalyses') THEN
    CREATE INDEX ix_feedback_analyses_queue ON poll_feedback_analyses (status, next_attempt_at, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025072432_AddFeedbackAnalyses') THEN
    CREATE INDEX "IX_poll_feedback_analyses_poll_id" ON poll_feedback_analyses (poll_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025072432_AddFeedbackAnalyses') THEN
    CREATE UNIQUE INDEX ux_feedback_analyses_question_fingerprint ON poll_feedback_analyses (question_id, answer_fingerprint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025072432_AddFeedbackAnalyses') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261025072432_AddFeedbackAnalyses', '10.0.12');
    END IF;
END $EF$;
COMMIT;
